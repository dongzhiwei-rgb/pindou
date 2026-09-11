/**
 * 文件：collab.ts
 * 用途：好友联机状态仓库——房间创建、邀请码、申请审批、SSE 实时同步、编辑批处理与格子锁展示。
 * 核心职责：隔离联机网络细节与编辑状态；格子锁用普通 Map + 版本号控制重绘开销，避免大图纸深度响应式。
 * 版权：@董志伟-联系方式-makabak1204
 * 最后修改：2026-08-27
 */

import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import {
  ApiError, applyCollab, cancelCollabApply, createCollabEventSource, decideCollab, getCollabApplyResult, hostCollab,
  kickCollabMember, leaveCollab, refreshCollabInvite, setCollabPermission, setCollabSavePermission,
  submitCollabClear, submitCollabEdits, submitCollabHistory, submitCollabPermApply, submitCollabPermDecide, submitCollabReplaceDecide,
  submitCollabReplaceRequest, submitCollabResync,
} from '../api'
import type {
  CollabApprovedPayload, CollabEditItem, CollabEditsEvent, CollabExpiredPayload, CollabLockEvent, CollabMemberDto,
  CollabPendingPermDto, CollabReplaceDto, CollabRoomDto, CollabSnapshotDto, CollabStateEvent,
} from '../types'
import { useEditorStore } from './editor'

// 联机成员颜色：0 = 房主系统默认色（与画布悬停框一致），1..4 = 好友分配色。
export const COLLAB_COLORS = ['#14543d', '#2563eb', '#f59e0b', '#8b5cf6', '#14b8a6']
// 与服务端锁 TTL 保持一致：格子短暂独占，到期自动释放。
const LOCK_TTL = 8000
const LOCK_SWEEP_INTERVAL = 2000
const EDIT_FLUSH_DELAY = 320
const MAX_EDIT_BATCH = 120
const MAX_EDIT_RETRIES = 3
const RECONNECT_BASE_DELAY = 1500
const RECONNECT_MAX_DELAY = 15_000
// 申请等待时长：与后端一致，房主 30 秒内未响应即超时作废，成员可再次发起申请。
const APPLY_WAIT_MS = 30_000
// 权限申请等待时长：与后端一致，房主 30 秒内未响应即作废。
const PERM_WAIT_MS = 30_000
// 替换图纸申请：1 分钟冷却 + 1 分钟有效期（与后端一致）；冷却持久化保存，刷新不清空。
const REPLACE_COOLDOWN_MS = 60_000
const REPLACE_SESSION_KEY = 'pindou-replace-cooldown'
// 联机会话持久化：刷新页面后凭令牌重新连接 SSE 恢复联机状态（房间/成员/快照由服务端 state 事件补齐）。
const COLLAB_SESSION_KEY = 'pindou-collab-session'

export type CollabPhase = 'idle' | 'hosting' | 'joining' | 'member'

/** 生成联机邀请链接：链接携带邀请码参数，点击打开后会自动发起联机申请。 */
export function buildInviteLink(inviteCode: string): string {
  const base = `${window.location.origin}${window.location.pathname}`
  return `${base}?invite=${encodeURIComponent(inviteCode)}`
}

interface CellOwner {
  memberId: string
  colorIndex: number
  expiresAt: number
}

interface PendingEditBatch {
  operationId: string
  edits: Map<number, number>
  retries: number
}

export const useCollabStore = defineStore('collab', () => {
  // ---------- 状态 ----------
  const phase = ref<CollabPhase>('idle')
  const room = ref<CollabRoomDto | null>(null)
  const member = ref<CollabMemberDto | null>(null)
  const token = ref('')
  const inviteCode = ref('')
  const busy = ref(false)
  // 好友申请等待状态
  const applyId = ref<string | null>(null)
  const joinWaiting = ref(false)
  const joinMessage = ref('')
  // 申请过期时间（Unix 毫秒）：提交申请后 30 秒倒计时；持久化保存，刷新页面不影响倒计时。
  const joinExpiresAt = ref<number | null>(null)
  // 成员权限申请：30 秒冷却时间（Unix 毫秒，按权限类型），持久化保存，刷新不清空。
  const permCooldowns = ref<{ edit: number; save: number }>({ edit: 0, save: 0 })
  // 成员权限申请提示（已提交 / 已通过 / 已被拒绝 / 超时等）。
  const permMessage = ref('')
  // 一次性权限申请轻提示（审批结果 / 超时）：成员端弹出 Toast 后清空，与 inline 提示互补。
  const permNotice = ref('')
  // 刷新后恢复权限申请冷却（倒计时期间不可重复申请）。
  loadPermCooldowns()
  // 房主端待审批的成员权限申请列表（SSE perm_apply 推送，perm_removed/perm_expired 移除；携带过期时间供倒计时）。
  const pendingPerms = ref<CollabPendingPermDto[]>([])
  // 成员替换图纸申请：1 分钟冷却（Unix 毫秒），持久化保存，刷新不清空。
  const replaceCooldownUntil = ref(0)
  loadReplaceCooldown()
  // 房主端待审批的成员替换图纸申请队列（SSE replace_apply 推送，replace_removed 移除；携带过期时间供倒计时）。
  const pendingReplaces = ref<CollabReplaceDto[]>([])
  // 当前联机房间权威快照：成员生成/加载自家图纸申请替换后，据此恢复本地画布避免脱节。
  const currentSnapshot = ref<CollabSnapshotDto | null>(null)
  // 一次性替换申请轻提示（已提交 / 冷却 / 审批结果 / 超时）：成员端弹出 Toast 后清空。
  const replaceNotice = ref('')
  // 联机被关闭的原因（房主结束 / 房主离线 / 授权过期等），供 UI 区分提示文案。
  const closedReason = ref('')
  // 一次性提示（房主端联机申请事件：成员离线自动取消等），UI 弹出后清空。
  const notice = ref('')
  // 刷新后正在恢复联机状态；UI 据此抑制「进入联机自动弹窗」等即时交互。
  const restoring = ref(false)

  // 按笔画分组的编辑队列：同一笔即使跨多个网络批次，服务端也会合并为一次本人历史操作。
  const editQueue: PendingEditBatch[] = []
  let activeOperationId = ''
  let flushTimer = 0
  let flushRetryTimer = 0
  let flushInFlight = false
  let flushPromise: Promise<boolean> | null = null
  let lockSweepTimer = 0
  let queueSweepTimer = 0
  let pollTimer = 0
  let eventSource: EventSource | null = null
  let reconnectTimer = 0
  let reconnectDelay = RECONNECT_BASE_DELAY
  let connecting = false
  let connectionVersion = 0
  let lastServerSeq = 0
  // 整张豆板版本：清空、换图、重同步时变化；编辑请求携带该值，服务端拒绝旧图纸上的延迟落笔。
  let boardRevision = 0
  // 进入联机前的本人豆板备份（仅成员；退出/被踢后恢复）。
  let ownBackup: CollabSnapshotDto | null = null

  function clearEditQueue(): void {
    editQueue.length = 0
    activeOperationId = ''
    if (flushTimer) { window.clearTimeout(flushTimer); flushTimer = 0 }
    if (flushRetryTimer) { window.clearTimeout(flushRetryTimer); flushRetryTimer = 0 }
  }

  // 格子锁：非响应式 Map，配合版本号控制画布重绘；避免大图纸逐格代理开销。
  const cellOwners = new Map<number, CellOwner>()
  const locksVersion = ref(0)
  function bumpLocks(): void { locksVersion.value++ }

  // ---------- 派生状态 ----------
  const isHost = computed(() => phase.value === 'hosting')
  const isMember = computed(() => phase.value === 'member')
  const isCollabing = computed(() => phase.value === 'hosting' || phase.value === 'member')
  // 房主当前可分享的邀请链接（含邀请码参数；链接有效性与邀请码一致，刷新邀请码后旧链接失效）。
  const inviteLink = computed(() => (inviteCode.value ? buildInviteLink(inviteCode.value) : ''))
  // 当前成员是否可编辑：房主恒可；成员由房主单独开关（默认关闭，加入即只读）。
  const canEditLocal = computed(() => {
    if (!isCollabing.value) return true
    if (isHost.value) return true
    return member.value?.canEdit ?? false
  })
  // 当前成员是否可保存/导出：房主恒可；成员由房主单独开关（共享权限，默认开启）。
  const canSaveLocal = computed(() => {
    if (!isCollabing.value) return true
    if (isHost.value) return true
    return member.value?.canSave ?? true
  })
  // 联机历史按钮只在本人仍持有有效格子锁时可用；服务端会继续校验锁与具体历史记录的交集。
  const hasCurrentOwnLock = computed(() => {
    void locksVersion.value
    const memberId = member.value?.memberId
    if (!memberId) return false
    const now = Date.now()
    for (const lock of cellOwners.values()) {
      if (lock.memberId === memberId && lock.expiresAt > now) return true
    }
    return false
  })
  const canUndoOwn = computed(() => Boolean(member.value?.canUndo) && hasCurrentOwnLock.value)
  const canRedoOwn = computed(() => Boolean(member.value?.canRedo) && hasCurrentOwnLock.value)
  const myMember = computed(() => {
    if (!room.value?.members || !member.value) return null
    return room.value.members.find(item => item.memberId === member.value?.memberId) ?? member.value
  })
  const hostMember = computed(() => room.value?.members.find(item => item.isHost) ?? null)
  const friendMembers = computed(() => (room.value?.members ?? []).filter(item => !item.isHost) ?? [])
  const pendingApplications = computed(() => room.value?.pending ?? [])
  const memberCount = computed(() => room.value?.members.length ?? 0)

  // 用于画布描框的有效格子锁（依赖 locksVersion 触发重算）。
  const lockFrames = computed(() => {
    void locksVersion.value
    const now = Date.now()
    const frames: Array<{ cell: number; colorIndex: number }> = []
    cellOwners.forEach((lock, cell) => {
      if (lock.expiresAt > now) frames.push({ cell, colorIndex: lock.colorIndex })
    })
    return frames
  })

  // 某格是否正被其他成员编辑（本地画笔据此拦截，避免落笔后被服务端回滚）。
  function isCellLockedByOther(index: number): boolean {
    if (index < 0 || !isCollabing.value) return false
    const lock = cellOwners.get(index)
    if (!lock || lock.expiresAt <= Date.now()) return false
    return lock.memberId !== member.value?.memberId
  }

  // ---------- 联机会话持久化 / 刷新恢复 ----------

  /** 把联机身份写入 sessionStorage（房主/成员保存令牌；申请等待阶段保存 applyId 与倒计时），刷新页面后据此恢复。 */
  function persistSession(): void {
    try {
      if (phase.value === 'hosting' || phase.value === 'member') {
        if (token.value) {
          sessionStorage.setItem(COLLAB_SESSION_KEY, JSON.stringify({
            token: token.value,
            phase: phase.value,
            inviteCode: inviteCode.value,
          }))
        } else {
          sessionStorage.removeItem(COLLAB_SESSION_KEY)
        }
      } else if (phase.value === 'joining') {
        sessionStorage.setItem(COLLAB_SESSION_KEY, JSON.stringify({
          token: '',
          phase: phase.value,
          applyId: applyId.value,
          joinExpiresAt: joinExpiresAt.value,
        }))
      } else {
        sessionStorage.removeItem(COLLAB_SESSION_KEY)
      }
    } catch {
      // 隐私模式等存储不可用时忽略，仅影响刷新自动恢复。
    }
  }

  function clearPersistedSession(): void {
    try { sessionStorage.removeItem(COLLAB_SESSION_KEY) } catch { /* 忽略 */ }
  }

  function hasSavedSession(): boolean {
    try { return !!sessionStorage.getItem(COLLAB_SESSION_KEY) } catch { return false }
  }

  /**
   * 刷新页面后恢复联机状态：读取持久化令牌并重建阶段，随后连接 SSE。
   * 服务端连接建立后会立即下发 state 事件（房间 + 豆板快照 + 本人信息），据此补齐房间与画布。
   * 若房间已不存在（房主结束/超时清理），connect 内部会识别并清理本地联机状态。
   */
  async function restoreSession(): Promise<void> {
    if (phase.value !== 'idle' || !hasSavedSession()) return
    // 注意：不使用 typeof saved，避免控制流把 saved 收窄为 null 后导致断言目标错误。
    type SavedSession = { token?: string; phase?: string; inviteCode?: string; applyId?: string; joinExpiresAt?: number }
    let saved: SavedSession | null = null
    try {
      const raw = sessionStorage.getItem(COLLAB_SESSION_KEY)
      if (raw) {
        const parsed = JSON.parse(raw) as unknown
        if (parsed && typeof parsed === 'object') saved = parsed as unknown as SavedSession
      }
    } catch { /* 忽略脏数据 */ }
    if (!saved?.phase) return
    if (saved.phase === 'joining') {
      // 申请等待阶段：按保存的过期时间恢复倒计时（刷新不影响倒计时），继续轮询审批结果。
      phase.value = 'joining'
      applyId.value = saved.applyId ?? null
      joinExpiresAt.value = saved.joinExpiresAt ?? null
      joinWaiting.value = true
      joinMessage.value = '已提交申请，等待房主审批…'
      startPolling()
      return
    }
    if (!saved.token) return
    restoring.value = true
    token.value = saved.token
    if (saved.phase === 'hosting') {
      phase.value = 'hosting'
      inviteCode.value = saved.inviteCode ?? ''
    } else {
      // 成员端：进入联机前本人豆板已保存在本地（成员联机时不自动保存），
      // 先把当前本地画布备份为「退出后恢复」的数据，随后由服务端快照覆盖为共享画布。
      const editor = useEditorStore()
      ownBackup = {
        width: editor.width,
        height: editor.height,
        cells: editor.cells.slice(),
        colors: editor.colors.map(color => ({ ...color })),
        title: editor.title,
      }
      phase.value = 'member'
    }
    startLockSweep()
    try {
      await connect()
    } finally {
      restoring.value = false
    }
  }

  // ---------- 房主：发起 / 邀请码 ----------

  async function startHost(): Promise<void> {
    const editor = useEditorStore()
    if (!editor.hasPattern) throw new Error('当前没有可联机的豆板数据。')
    busy.value = true
    joinMessage.value = ''
    try {
      const snapshot: CollabSnapshotDto = {
        width: editor.width,
        height: editor.height,
        cells: editor.cells.slice(),
        colors: editor.colors.map(color => ({ ...color })),
        title: editor.title,
      }
      const result = await hostCollab(snapshot)
      token.value = result.hostToken
      inviteCode.value = result.inviteCode
      room.value = result.room
      member.value = {
        memberId: result.memberId,
        name: result.hostName,
        colorIndex: result.colorIndex,
        isHost: true,
        canEdit: true,
        canSave: true,
        canUndo: false,
        canRedo: false,
      }
      currentSnapshot.value = { ...snapshot, cells: snapshot.cells.slice(), colors: snapshot.colors.map(color => ({ ...color })) }
      lastServerSeq = Number(result.seq) || 0
      boardRevision = normalizeBoardRevision(result.boardRevision)
      phase.value = 'hosting'
      ownBackup = null
      cellOwners.clear()
      clearEditQueue()
      bumpLocks()
      persistSession()
      startLockSweep()
      void connect()
    } finally {
      busy.value = false
    }
  }

  async function refreshInvite(): Promise<void> {
    if (!isHost.value || !token.value) return
    const result = await refreshCollabInvite(token.value)
    inviteCode.value = result.inviteCode
    persistSession()
  }

  // ---------- 好友：申请 / 等待审批 ----------

  async function join(code: string): Promise<void> {
    if (isCollabing.value) return
    // 倒计时期间不可再次发起申请：等待审批或 30 秒倒计时未结束时不重复申请。
    if (joinWaiting.value || (joinExpiresAt.value && Date.now() < joinExpiresAt.value)) return
    const normalized = code.trim().toUpperCase()
    if (!normalized) {
      joinMessage.value = '请输入邀请码。'
      return
    }
    busy.value = true
    joinMessage.value = ''
    try {
      const result = await applyCollab(normalized)
      applyId.value = result.applyId
      // 申请等待 30 秒：过期时间持久化，刷新页面不影响倒计时。
      joinExpiresAt.value = Date.now() + APPLY_WAIT_MS
      phase.value = 'joining'
      joinWaiting.value = true
      joinMessage.value = '已提交申请，等待房主审批…'
      persistSession()
      startPolling()
    } catch (reason) {
      joinMessage.value = reason instanceof Error ? reason.message : '申请失败，请稍后重试。'
    } finally {
      busy.value = false
    }
  }

  function cancelJoin(): void {
    if (phase.value !== 'joining') return
    const currentApplyId = applyId.value
    // 本地立即结束等待，网络请求在后台完成；即使响应丢失，服务端离线清扫仍会最终兜底。
    endJoining('')
    if (currentApplyId) void cancelCollabApply(currentApplyId).catch(() => undefined)
  }

  /** 结束申请等待阶段并回到 idle：清理轮询、持久化与倒计时，向成员展示给定提示文案。 */
  function endJoining(message: string): void {
    stopPolling()
    phase.value = 'idle'
    applyId.value = null
    joinWaiting.value = false
    joinExpiresAt.value = null
    joinMessage.value = message
    clearPersistedSession()
  }

  /** 申请终态文案：倒计时结束房主未响应 / 房主离线 / 房主结束联机等，成员据此再次发起申请。 */
  function expireJoinMessage(reason: string): string {
    switch (reason) {
      case 'host_no_response': return '房主未响应，可再次发起申请。'
      case 'host_offline': return '房主已离线，暂时无法联机，可稍后再试。'
      case 'host_left': return '房主已结束联机，无法加入。'
      case 'member_offline': return '申请已失效，请重新发起申请。'
      default: return '申请已失效，请重新发起申请。'
    }
  }

  function startPolling(): void {
    stopPolling()
    pollTimer = window.setTimeout(pollOnce, 1500)
  }

  function stopPolling(): void {
    if (pollTimer) { window.clearTimeout(pollTimer); pollTimer = 0 }
  }

  async function pollOnce(): Promise<void> {
    if (phase.value !== 'joining' || !applyId.value) return
    // 本地倒计时兜底：等待满 30 秒且后端尚未返回结果时，直接提示房主未响应（可再次申请）。
    if (joinExpiresAt.value && Date.now() >= joinExpiresAt.value) {
      endJoining(expireJoinMessage('host_no_response'))
      return
    }
    try {
      const result = await getCollabApplyResult(applyId.value)
      if (phase.value !== 'joining') return
      if ('pending' in result && result.pending) {
        pollTimer = window.setTimeout(pollOnce, 2000)
        return
      }
      if ('type' in result && result.type === 'approved') {
        enterRoomAsMember(result)
        return
      }
      // 终态：被拒绝 / 超时 / 房主离线等。
      if ('type' in result && result.type === 'expired') {
        endJoining(expireJoinMessage((result as CollabExpiredPayload).reason ?? ''))
        return
      }
      endJoining('房主拒绝了您的联机申请。')
    } catch {
      pollTimer = window.setTimeout(pollOnce, 2000)
    }
  }

  function enterRoomAsMember(approved: CollabApprovedPayload): void {
    stopPolling()
    const editor = useEditorStore()
    // 先备份本人豆板，退出/被踢后恢复。
    ownBackup = {
      width: editor.width,
      height: editor.height,
      cells: editor.cells.slice(),
      colors: editor.colors.map(color => ({ ...color })),
      title: editor.title,
    }
    token.value = approved.member.token
    room.value = approved.room
    member.value = {
      memberId: approved.member.memberId,
      name: approved.member.name,
      colorIndex: approved.member.colorIndex,
      isHost: false,
      // 成员默认权限由服务端下发：编辑权限默认关闭（只读），共享权限默认开启。
      canEdit: approved.member.canEdit ?? false,
      canSave: approved.member.canSave ?? true,
      canUndo: false,
      canRedo: false,
    }
    // 载入房主豆板快照（共享数据）。
    editor.applyCollabSnapshot(approved.snapshot)
    currentSnapshot.value = { ...approved.snapshot, cells: approved.snapshot.cells.slice(), colors: approved.snapshot.colors.map(color => ({ ...color })) }
    lastServerSeq = Number(approved.seq) || 0
    boardRevision = normalizeBoardRevision(approved.boardRevision)
    cellOwners.clear()
    clearEditQueue()
    bumpLocks()
    phase.value = 'member'
    applyId.value = null
    joinWaiting.value = false
    joinExpiresAt.value = null
    joinMessage.value = ''
    persistSession()
    startLockSweep()
    void connect()
  }

  // ---------- 房主操作 ----------

  function removePendingApplication(applyId: string): void {
    if (!room.value?.pending?.some(item => item.applyId === applyId)) return
    room.value = { ...room.value, pending: room.value.pending.filter(item => item.applyId !== applyId) }
  }

  async function decidePendingApplication(applyId: string, accept: boolean): Promise<void> {
    if (!token.value) return
    try {
      await decideCollab(token.value, applyId, accept)
    } catch (reason) {
      // SSE 延迟时列表可能仍显示旧申请；服务端判定失效后立即移除本地残留，再由页面展示错误轻提示。
      if (reason instanceof ApiError && reason.message.includes('联机申请已失效')) removePendingApplication(applyId)
      throw reason
    }
  }

  function approve(applyId: string): Promise<void> { return decidePendingApplication(applyId, true) }

  function reject(applyId: string): Promise<void> { return decidePendingApplication(applyId, false) }

  async function kick(memberId: string): Promise<void> {
    if (!token.value) return
    await kickCollabMember(token.value, memberId)
  }

  async function setPermission(memberId: string, canEdit: boolean): Promise<void> {
    if (!token.value) return
    await setCollabPermission(token.value, memberId, canEdit)
  }

  async function setSavePermission(memberId: string, canSave: boolean): Promise<void> {
    if (!token.value) return
    await setCollabSavePermission(token.value, memberId, canSave)
  }

  // ---------- 退出 / 收尾 ----------

  async function leave(): Promise<void> {
    const currentToken = token.value
    const wasHost = isHost.value
    if (currentToken && isCollabing.value) {
      try {
        await leaveCollab(currentToken)
      } catch {
        // 网络异常也照常退出本地联机状态。
      }
    }
    // 房主退出后保留当前共享豆板（即本人数据）；成员恢复进入联机前的本人豆板。
    teardown(wasHost)
  }

  /** 清理联机状态；成员恢复本人豆板，房主保留共享豆板。 */
  function teardown(keepBoard = false): void {
    disconnect()
    stopPolling()
    stopLockSweep()
    const editor = useEditorStore()
    if (!keepBoard && phase.value === 'member' && ownBackup) {
      editor.applyCollabSnapshot(ownBackup)
    }
    phase.value = 'idle'
    token.value = ''
    room.value = null
    member.value = null
    inviteCode.value = ''
    applyId.value = null
    joinWaiting.value = false
    pendingPerms.value = []
    pendingReplaces.value = []
    permMessage.value = ''
    permNotice.value = ''
    replaceNotice.value = ''
    currentSnapshot.value = null
    lastServerSeq = 0
    boardRevision = 0
    ownBackup = null
    cellOwners.clear()
    clearEditQueue()
    clearPersistedSession()
    bumpLocks()
  }

  // ---------- 实时连接（SSE） ----------

  function disconnect(): void {
    connectionVersion++
    connecting = false
    reconnectDelay = RECONNECT_BASE_DELAY
    if (reconnectTimer) { window.clearTimeout(reconnectTimer); reconnectTimer = 0 }
    const source = eventSource
    if (source) {
      source.onopen = null
      source.onmessage = null
      source.onerror = null
      source.close()
      eventSource = null
    }
  }

  /** 页面进入后台缓存时暂停长连接，但保留房间身份；恢复页面后可用 resumeConnection 重新申请票据。 */
  function suspendConnection(): void {
    disconnect()
  }

  function resumeConnection(): void {
    if (isCollabing.value && token.value) void connect()
  }

  function scheduleReconnect(): void {
    if (reconnectTimer || !token.value || !isCollabing.value) return
    const delay = reconnectDelay
    reconnectDelay = Math.min(RECONNECT_MAX_DELAY, reconnectDelay * 2)
    reconnectTimer = window.setTimeout(() => {
      reconnectTimer = 0
      void connect()
    }, delay)
  }

  /** 主动丢弃可能缺事件的连接；新连接首个 state 会携带完整权威快照与当前序号。 */
  function recoverAuthoritativeState(): void {
    const source = eventSource
    if (source) {
      source.onopen = null
      source.onmessage = null
      source.onerror = null
      source.close()
      eventSource = null
    }
    lastServerSeq = 0
    scheduleReconnect()
  }

  /** 检测编辑序号缺口。出现缺口时不应用不完整增量，改为重连获取完整快照。 */
  function acceptServerSequence(raw: unknown): boolean {
    const seq = Number(raw)
    if (!Number.isSafeInteger(seq) || seq <= 0) return true
    if (lastServerSeq > 0 && seq <= lastServerSeq) return false
    if (lastServerSeq > 0 && seq !== lastServerSeq + 1) {
      notice.value = '检测到联机数据缺口，正在自动恢复完整画布…'
      recoverAuthoritativeState()
      return false
    }
    lastServerSeq = seq
    return true
  }

  function updateCurrentSnapshot(changes: CollabEditItem[]): void {
    const snapshot = currentSnapshot.value
    if (!snapshot || !Array.isArray(changes)) return
    for (const change of changes) {
      if (Number.isInteger(change.index) && change.index >= 0 && change.index < snapshot.cells.length)
        snapshot.cells[change.index] = change.colorIndex
    }
  }

  function normalizeBoardRevision(raw: unknown): number {
    const revision = Number(raw)
    return Number.isSafeInteger(revision) && revision > 0 ? revision : (boardRevision || 1)
  }

  /**
   * 整板权威状态到达时丢弃尚未发送的旧编辑并清除锁显示。
   * 已在途请求无法从浏览器撤回，但其旧 boardRevision 会被服务端拒绝，随后由完整 state 收敛。
   */
  function resetTransientCanvasState(message = ''): void {
    const hadPendingEdits = editQueue.length > 0 || flushInFlight
    clearEditQueue()
    cellOwners.clear()
    bumpLocks()
    if (hadPendingEdits && message) notice.value = message
  }

  async function connect(): Promise<void> {
    if (connecting || !token.value || !isCollabing.value) return
    if (reconnectTimer) { window.clearTimeout(reconnectTimer); reconnectTimer = 0 }
    const currentVersion = ++connectionVersion
    const currentToken = token.value
    connecting = true
    try {
      const source = await createCollabEventSource(currentToken)
      if (currentVersion !== connectionVersion || currentToken !== token.value || !isCollabing.value) {
        source.close()
        return
      }
      eventSource?.close()
      eventSource = source
      source.onopen = () => { reconnectDelay = RECONNECT_BASE_DELAY }
      source.addEventListener('state', (event) => {
        try { handleEvent(JSON.parse((event as MessageEvent).data)) } catch { /* 忽略脏数据 */ }
      })
      source.onmessage = (event) => {
        try { handleEvent(JSON.parse(event.data)) } catch { /* 忽略脏数据 */ }
      }
      source.onerror = () => {
        if (eventSource !== source) return
        // 联机票据是一次性的，不能让 EventSource 用旧 URL 无限自动重连；关闭旧连接并申请新票据。
        source.close()
        eventSource = null
        scheduleReconnect()
      }
    } catch (reason) {
      // 房间已不存在（房主结束 / 空闲超时清理 / 授权到期）：清理本地联机状态与持久化会话，
      // 避免刷新后对已失效令牌无限重连；网络类故障仍按指数退避重连。
      const roomDead = reason instanceof ApiError
        && (reason.status === 409 || String(reason.message ?? '').includes('已失效'))
      if (currentVersion === connectionVersion) {
        if (roomDead) teardown(false)
        else scheduleReconnect()
      }
    } finally {
      if (currentVersion === connectionVersion) connecting = false
    }
  }

  /** 解析申请过期时间：后端未下发（旧版本事件/DTO 无 expiresAt）时按等待时长兜底，保证房主端倒计时可用。 */
  function resolveExpiresAt(raw: unknown, fallbackMs: number): number {
    const n = Number(raw)
    return n > 0 ? n : Date.now() + fallbackMs
  }

  /** 从房间 DTO 同步房主审批队列（权限 + 替换图纸），带过期时间：刷新/重连后倒计时不受影响。
   * 仅当 DTO 携带对应字段时才覆盖（旧后端无此字段时不覆盖，避免把 perm_apply/replace_apply 已加入的队列清空）。 */
  function syncApprovalQueues(roomDto: CollabRoomDto): void {
    if (Array.isArray(roomDto.pendingPerms)) {
      pendingPerms.value = roomDto.pendingPerms.map(item => ({
        ...item,
        expiresAt: resolveExpiresAt(item.expiresAt, PERM_WAIT_MS),
      }))
    }
    if (Array.isArray(roomDto.pendingReplaces)) {
      pendingReplaces.value = roomDto.pendingReplaces.map(item => ({
        ...item,
        expiresAt: resolveExpiresAt(item.expiresAt, REPLACE_COOLDOWN_MS),
      }))
    }
  }

  // SSE 事件类型不固定（state/room/edits/permission/kicked/closed），type 放宽为字符串；
  // 但保留 room/member/snapshot 等已知字段的结构，便于直接读取。
  function handleEvent(data: Omit<Partial<CollabStateEvent>, 'type'> & { type?: string } & Record<string, unknown>): void {
    if (!data || typeof data !== 'object') return
    switch (data.type) {
      case 'state': {
        const incomingRevision = normalizeBoardRevision(data.boardRevision)
        if (boardRevision > 0 && incomingRevision !== boardRevision) {
          resetTransientCanvasState('共享图纸已更新，未同步的旧编辑已取消。')
        } else {
          cellOwners.clear()
          bumpLocks()
        }
        boardRevision = incomingRevision
        lastServerSeq = Number(data.seq) || 0
        if (data.room) {
          room.value = data.room
          syncApprovalQueues(data.room)
        }
        if (data.member) member.value = data.member as CollabMemberDto
        if (data.snapshot) {
          const snapshot = data.snapshot as CollabSnapshotDto
          currentSnapshot.value = { ...snapshot, cells: snapshot.cells.slice(), colors: snapshot.colors.map(color => ({ ...color })) }
          useEditorStore().applyCollabSnapshot(snapshot)
        }
        break
      }
      case 'room': {
        room.value = data.room ?? null
        // 同步本人权限（房主在弹窗调整后，room 广播含最新 canEdit/canSave）。
        if (member.value && room.value?.members) {
          const me = room.value.members.find(item => item.memberId === member.value?.memberId)
          if (me) member.value = { ...member.value, canEdit: me.canEdit, canSave: me.canSave }
        }
        // 刷新/恢复后同步房主审批队列（权限 + 替换，携带过期时间，倒计时不受刷新影响）。
        if (data.room) syncApprovalQueues(data.room)
        break
      }
      case 'edits': {
        applyEditsEvent(data as unknown as CollabEditsEvent)
        break
      }
      case 'clear': {
        if (!acceptServerSequence(data.seq)) break
        boardRevision = normalizeBoardRevision(data.boardRevision)
        resetTransientCanvasState('共享画布已清空，未同步的旧编辑已取消。')
        if (member.value) member.value = { ...member.value, canUndo: false, canRedo: false }
        // 房主清空了共享画布：整体清空本地画布（幂等，房主本地已清空也无害）。
        if (isCollabing.value && currentSnapshot.value) {
          currentSnapshot.value = { ...currentSnapshot.value, cells: new Array(currentSnapshot.value.cells.length).fill(-1) }
          useEditorStore().applyCollabSnapshot(currentSnapshot.value)
        }
        break
      }
      case 'resync': {
        // 房主重新生成/替换整块画布：成员整体替换本地画布，保证联机豆板数据统一。
        if (isCollabing.value && data.snapshot && acceptServerSequence(data.seq)) {
          boardRevision = normalizeBoardRevision(data.boardRevision)
          resetTransientCanvasState('共享图纸已替换，未同步的旧编辑已取消。')
          if (member.value) member.value = { ...member.value, canUndo: false, canRedo: false }
          const snapshot = data.snapshot as CollabSnapshotDto
          currentSnapshot.value = { ...snapshot, cells: snapshot.cells.slice(), colors: snapshot.colors.map(color => ({ ...color })) }
          useEditorStore().applyCollabSnapshot(snapshot)
        }
        break
      }
      case 'history': {
        if (member.value && data.memberId === member.value.memberId) {
          member.value = { ...member.value, canUndo: Boolean(data.canUndo), canRedo: Boolean(data.canRedo) }
        }
        break
      }
      case 'permission': {
        const memberId = data.memberId as string
        const canEdit = Boolean(data.canEdit)
        if (room.value?.members) {
          const target = room.value.members.find(item => item.memberId === memberId)
          if (target) target.canEdit = canEdit
        }
        if (member.value && member.value.memberId === memberId) {
          member.value = { ...member.value, canEdit }
        }
        if (!canEdit) removeMemberLocks(memberId)
        break
      }
      case 'savepermission': {
        const memberId = data.memberId as string
        const canSave = Boolean(data.canSave)
        if (room.value?.members) {
          const target = room.value.members.find(item => item.memberId === memberId)
          if (target) target.canSave = canSave
        }
        if (member.value && member.value.memberId === memberId) {
          member.value = { ...member.value, canSave }
        }
        break
      }
      case 'kicked': {
        const memberId = data.memberId as string
        removeMemberLocks(memberId)
        if (member.value && memberId === member.value.memberId) {
          // 自己被踢出：恢复本人豆板。
          closedReason.value = String(data.reason ?? 'kicked')
          teardown(false)
        } else if (room.value?.members) {
          room.value.members = room.value.members.filter(item => item.memberId !== memberId)
          // 房主端轻提示：成员主动退出 / 离线 / 被踢出时「x豆已离开房间」。
          if (data.name) notice.value = `${String(data.name)} 已离开房间`
        }
        break
      }
      case 'closed': {
        // 房主退出 / 房主离线 / 授权到期：整个房间关闭。
        closedReason.value = String(data.reason ?? 'closed')
        teardown(false)
        break
      }
      case 'apply_removed': {
        // 精准事件先即时移除对应列表项；紧随其后的 room 全量事件负责状态兜底。
        removePendingApplication(String(data.applyId ?? ''))
        if (data.reason === 'member_offline') {
          notice.value = `${String(data.name ?? '该成员')} 已离线，申请已自动取消。`
        } else if (data.reason === 'member_cancelled') {
          notice.value = `${String(data.name ?? '该成员')} 已取消联机申请。`
        }
        break
      }
      case 'perm_apply': {
        // 房主收到新权限申请：加入待审批列表（独立铃铛展示，携带申请者豆子颜色与过期时间倒计时），并弹 Toast 轻提示。
        const permName = data.perm === 'save' ? '共享' : '编辑'
        const permApplyName = String(data.name ?? '该成员')
        pendingPerms.value = [...pendingPerms.value, {
          applyId: String(data.applyId),
          memberId: String(data.memberId),
          name: permApplyName,
          colorIndex: Number(data.colorIndex) || 0,
          perm: data.perm === 'save' ? 'save' : 'edit',
          expiresAt: resolveExpiresAt(data.expiresAt, PERM_WAIT_MS),
        }]
        notice.value = `${permApplyName} 申请${permName}权限`
        break
      }
      case 'replace_apply': {
        // 房主收到新替换图纸申请：加入待审批队列（x豆 申请替换当前图纸），弹出 Toast 轻提示。
        const replaceName = String(data.name ?? '该成员')
        pendingReplaces.value = [...pendingReplaces.value, {
          applyId: String(data.applyId),
          memberId: String(data.memberId),
          name: replaceName,
          colorIndex: Number(data.colorIndex) || 0,
          expiresAt: resolveExpiresAt(data.expiresAt, REPLACE_COOLDOWN_MS),
        }]
        notice.value = `${replaceName} 申请替换当前图纸`
        break
      }
      case 'replace_removed': {
        // 房主收到：申请已处理 / 超时 / 成员离线，对应替换申请移出消息队列；申请人同步收到审批结果提示。
        const applyId = String(data.applyId)
        pendingReplaces.value = pendingReplaces.value.filter(item => item.applyId !== applyId)
        if (member.value && data.memberId && data.memberId === member.value.memberId) {
          if (data.accepted === true) {
            replaceNotice.value = '房主已同意替换图纸，正在同步…'
          } else if (data.accepted === false) {
            replaceNotice.value = '房主已拒绝替换图纸申请'
          } else if (data.reason === 'expired') {
            replaceNotice.value = '替换申请已超时，房主未响应，可稍后再次申请'
          }
        }
        break
      }
      case 'perm_decided': {
        // 成员收到权限申请结果：同意后权限已由 permission/savepermission 事件同步，此处仅提示结果。
        const accepted = Boolean(data.accepted)
        const perm = data.perm === 'save' ? '共享' : '编辑'
        permMessage.value = accepted ? `房主已同意你的${perm}权限申请。` : `房主已拒绝你的${perm}权限申请。`
        // 轻提示：仅申请人弹 Toast；事件携带 memberId 精确匹配本人，避免房主/其他成员误收到。
        if (member.value && data.memberId && data.memberId === member.value.memberId) {
          permNotice.value = accepted ? `房主已同意你的${perm}权限申请` : `房主已拒绝你的${perm}权限申请`
        }
        break
      }
      case 'perm_removed': {
        // 房主收到：成员离线/退出或申请已处理，对应权限申请从列表移除。
        const applyId = String(data.applyId)
        pendingPerms.value = pendingPerms.value.filter(item => item.applyId !== applyId)
        break
      }
      case 'perm_expired': {
        // 房主收到：申请超时（房主未响应）自动作废，从待审批列表移除；申请人同步收到超时轻提示。
        const applyId = String(data.applyId)
        pendingPerms.value = pendingPerms.value.filter(item => item.applyId !== applyId)
        if (member.value && data.memberId === member.value.memberId) {
          const perm = data.perm === 'save' ? '共享' : '编辑'
          permMessage.value = `房主未响应，${perm}权限申请已超时，可稍后再次申请。`
          permNotice.value = `房主未响应，${perm}权限申请已超时`
        }
        break
      }
    }
  }

  /** 应用服务端广播的编辑：权威写入 + 冲突回滚 + 抢占锁更新。 */
  type EditApplyStatus = 'applied' | 'duplicate' | 'stale' | 'gap' | 'inactive'

  function applyEditsEvent(data: CollabEditsEvent): EditApplyStatus {
    if (!isCollabing.value) return 'inactive'
    const seq = Number(data.seq)
    // HTTP 响应与 SSE 可能携带同一事件；相同序号表示已应用，较旧序号必须丢弃，不能覆盖后来的成员编辑。
    if (Number.isSafeInteger(seq) && seq > 0) {
      if (lastServerSeq > 0 && seq === lastServerSeq) return 'duplicate'
      if (lastServerSeq > 0 && seq < lastServerSeq) return 'stale'
      if (lastServerSeq > 0 && seq !== lastServerSeq + 1) {
        notice.value = '检测到联机数据缺口，正在自动恢复完整画布…'
        recoverAuthoritativeState()
        return 'gap'
      }
      lastServerSeq = seq
    }
    const editor = useEditorStore()
    // 正常编辑与冲突回滚按服务端顺序合并为一次响应式通知，回滚位于后面时自然覆盖同格编辑。
    const changes = [...(data.edits ?? []), ...(data.reverts ?? [])]
    editor.applyCellEdits(changes)
    updateCurrentSnapshot(changes)
    applyLocks(data.locks ?? [])
    return 'applied'
  }

  function applyLocks(locks: CollabLockEvent[]): void {
    if (!locks || locks.length === 0) return
    const now = Date.now()
    let changed = false
    for (const lock of locks) {
      for (const cell of lock.cells ?? []) {
        cellOwners.set(cell, { memberId: lock.memberId, colorIndex: lock.colorIndex, expiresAt: now + LOCK_TTL })
        changed = true
      }
    }
    if (changed) bumpLocks()
  }

  function removeMemberLocks(memberId: string): void {
    if (!memberId) return
    let changed = false
    cellOwners.forEach((lock, cell) => {
      if (lock.memberId === memberId) {
        cellOwners.delete(cell)
        changed = true
      }
    })
    if (changed) bumpLocks()
  }

  // ---------- 本地编辑批量提交 ----------

  function createOperationId(): string {
    try { return crypto.randomUUID().replaceAll('-', '') } catch { return `${Date.now().toString(36)}${Math.random().toString(36).slice(2)}` }
  }

  /** 指针开始一笔编辑时创建稳定操作号；同一笔跨多个网络批次时仍属于一次撤销记录。 */
  function beginEditOperation(): void {
    if (isCollabing.value && canEditLocal.value && !activeOperationId) activeOperationId = createOperationId()
  }

  function endEditOperation(): void {
    activeOperationId = ''
  }

  /** 画布落笔后调用：按本人笔画分组并批量发送。 */
  function queueEdit(index: number, colorIndex: number): void {
    if (!isCollabing.value || !canEditLocal.value || index < 0) return
    if (!activeOperationId) beginEditOperation()
    const operationId = activeOperationId || createOperationId()
    let batch = editQueue[editQueue.length - 1]
    if (!batch || batch.operationId !== operationId || batch.edits.size >= MAX_EDIT_BATCH) {
      batch = { operationId, edits: new Map<number, number>(), retries: 0 }
      editQueue.push(batch)
    }
    batch.edits.set(index, colorIndex)
    if (batch.edits.size >= MAX_EDIT_BATCH) {
      if (flushTimer) { window.clearTimeout(flushTimer); flushTimer = 0 }
      void flushEdits()
      return
    }
    if (!flushTimer) {
      flushTimer = window.setTimeout(() => {
        flushTimer = 0
        void flushEdits()
      }, EDIT_FLUSH_DELAY)
    }
  }

  async function flushEdits(): Promise<boolean> {
    if (flushPromise) return flushPromise
    if (editQueue.length === 0) return true
    flushPromise = drainEditQueue()
    try { return await flushPromise } finally { flushPromise = null }
  }

  async function drainEditQueue(): Promise<boolean> {
    const currentToken = token.value
    if (!currentToken || !isCollabing.value) { clearEditQueue(); return false }
    flushInFlight = true
    try {
      while (editQueue.length > 0 && currentToken === token.value && isCollabing.value) {
        const batch = editQueue.shift() as PendingEditBatch
        const edits: CollabEditItem[] = []
        batch.edits.forEach((colorIndex, index) => edits.push({ index, colorIndex }))
        try {
          const result = await submitCollabEdits(currentToken, boardRevision, batch.operationId, edits)
          if (!isCollabing.value || currentToken !== token.value) return false
          const applyStatus = applyEditsEvent({
            type: 'edits', seq: result.seq, edits: result.edits ?? [], reverts: result.reverts ?? [], locks: result.locks ?? [],
          })
          if (applyStatus === 'gap' || applyStatus === 'inactive') {
            // 当前批次已被服务端处理，不能重发；丢弃尚未发送的旧本地队列，等待 state 权威快照收敛。
            clearEditQueue()
            return false
          }
          if ((applyStatus === 'applied' || applyStatus === 'duplicate') && member.value) {
            member.value = { ...member.value, canUndo: result.canUndo, canRedo: result.canRedo }
          }
        } catch (reason) {
          const status = reason instanceof ApiError ? reason.status : 0
          const retryable = status === 0 || status === 408 || status === 429 || status === 503
          if (retryable && batch.retries < MAX_EDIT_RETRIES && isCollabing.value) {
            batch.retries++
            editQueue.unshift(batch)
            const delay = Math.min(3000, 400 * (2 ** (batch.retries - 1)))
            if (!flushRetryTimer) {
              flushRetryTimer = window.setTimeout(() => {
                flushRetryTimer = 0
                void flushEdits()
              }, delay)
            }
            notice.value = '联机网络波动，正在重新提交本次编辑…'
            return false
          }
          notice.value = reason instanceof Error && reason.message ? reason.message : '编辑同步失败，正在恢复服务器画布。'
          clearEditQueue()
          recoverAuthoritativeState()
          return false
        }
      }
      return editQueue.length === 0
    } finally {
      flushInFlight = false
    }
  }

  // 房主清空共享画布：清空待提交队列后向后端请求清空，服务端广播 clear 事件让所有成员整体清空本地画布。
  async function requestClear(): Promise<void> {
    if (!isCollabing.value) return
    const currentToken = token.value
    if (!currentToken) return
    clearEditQueue()
    try {
      await submitCollabClear(currentToken)
    } catch {
      // 清空失败（非房主 / 联机失效等）：静默，本地编辑不受影响。
    }
  }

  /** 联机撤销/恢复由服务端执行，只会修改当前仍由本人锁定的格子。 */
  async function applyOwnHistory(action: 'undo' | 'redo'): Promise<void> {
    if (!isCollabing.value || !canEditLocal.value || !token.value) return
    endEditOperation()
    const flushed = await flushEdits()
    if (!flushed) return
    try {
      const result = await submitCollabHistory(token.value, action)
      const applyStatus = applyEditsEvent({ type: 'edits', seq: result.seq, edits: result.edits ?? [], reverts: [], locks: [] })
      if ((applyStatus === 'applied' || applyStatus === 'duplicate') && member.value) {
        member.value = { ...member.value, canUndo: result.canUndo, canRedo: result.canRedo }
      }
    } catch (reason) {
      notice.value = reason instanceof Error && reason.message
        ? reason.message
        : action === 'undo' ? '撤销失败，请稍后重试。' : '恢复失败，请稍后重试。'
    }
  }

  function undoOwnEdit(): Promise<void> { return applyOwnHistory('undo') }
  function redoOwnEdit(): Promise<void> { return applyOwnHistory('redo') }

  // 房主生成/替换整块画布后，把新快照同步给房间成员（服务端更新权威快照并广播 resync）。
  // 成员收到后整体替换本地画布；成员重新进入联机时也从服务端最新快照恢复。
  async function resyncSnapshot(): Promise<void> {
    if (!isCollabing.value || !isHost.value) return
    const currentToken = token.value
    if (!currentToken) return
    const editor = useEditorStore()
    if (!editor.hasPattern) return
    const snapshot: CollabSnapshotDto = {
      width: editor.width,
      height: editor.height,
      cells: editor.cells.slice(),
      colors: editor.colors.map(color => ({ ...color })),
      title: editor.title,
    }
    try {
      await submitCollabResync(currentToken, snapshot)
    } catch {
      // 同步失败静默；服务端快照保持旧值，成员可稍后重新加入获取。
    }
  }

  // ---------- 成员权限申请 / 房主审批 ----------

  const PERM_COOLDOWN_KEY = 'pindou-collab-perm-cooldowns'
  // 权限申请冷却 1 分钟：大于服务端 30 秒有效期 + 可能的清扫/网络延迟，避免上一申请未到期就重复发送。
  const PERM_APPLY_COOLDOWN_MS = 60_000

  function loadPermCooldowns(): void {
    try {
      const raw = sessionStorage.getItem(PERM_COOLDOWN_KEY)
      if (raw) {
        const parsed = JSON.parse(raw) as { edit?: number; save?: number }
        permCooldowns.value = { edit: Number(parsed.edit) || 0, save: Number(parsed.save) || 0 }
      }
    } catch {
      // 隐私模式等存储不可用时忽略。
    }
  }

  function savePermCooldowns(): void {
    try {
      sessionStorage.setItem(PERM_COOLDOWN_KEY, JSON.stringify(permCooldowns.value))
    } catch {
      // 忽略。
    }
  }

  /** 权限申请剩余冷却秒数（0 表示可申请）；冷却已过期自动归零。 */
  function permCooldownRemaining(perm: 'edit' | 'save'): number {
    const until = permCooldowns.value[perm]
    if (!until) return 0
    const remain = Math.ceil((until - Date.now()) / 1000)
    if (remain <= 0) {
      permCooldowns.value[perm] = 0
      savePermCooldowns()
      return 0
    }
    return remain
  }

  /** 成员申请权限（编辑/共享）：冷却拦截 + 提交；返回是否提交成功。失败（服务端仍在冷却/已有待审批等）时弹 Toast 并尽量在本地补回冷却。 */
  async function permApply(perm: 'edit' | 'save'): Promise<boolean> {
    if (!isCollabing.value || !isMember.value) return false
    const currentToken = token.value
    if (!currentToken) return false
    const remain = permCooldownRemaining(perm)
    if (remain > 0) {
      // 本地冷却拦截（刷新后从 sessionStorage 恢复）：轻提示剩余秒数。
      permNotice.value = `${remain} 秒后可再次申请${perm === 'edit' ? '编辑' : '共享'}权限`
      return false
    }
    try {
      await submitCollabPermApply(currentToken, perm)
      permCooldowns.value[perm] = Date.now() + PERM_APPLY_COOLDOWN_MS
      savePermCooldowns()
      permMessage.value = perm === 'edit' ? '已提交编辑权限申请，等待房主审批。' : '已提交共享权限申请，等待房主审批。'
      return true
    } catch (reason) {
      const message = reason instanceof Error && reason.message ? reason.message : '权限申请失败，请稍后重试。'
      permMessage.value = message
      // 服务端仍在冷却 / 已有待审批：弹 Toast 提示，并在本地补回冷却，避免刷新后按钮误显示为可申请。
      permNotice.value = message
      if (message.includes('频繁') || message.includes('待审批')) {
        permCooldowns.value[perm] = Date.now() + PERM_APPLY_COOLDOWN_MS
        savePermCooldowns()
      }
      return false
    }
  }

  /** 房主审批成员权限申请（同意/拒绝）。 */
  async function permDecide(applyId: string, accept: boolean): Promise<void> {
    if (!isCollabing.value || !isHost.value) return
    const currentToken = token.value
    if (!currentToken) return
    try {
      await submitCollabPermDecide(currentToken, applyId, accept)
    } catch {
      // 失败静默，SSE 会同步权限状态。
    }
  }

  // ---------- 成员替换图纸申请 / 房主审批 ----------

  function loadReplaceCooldown(): void {
    try {
      const raw = sessionStorage.getItem(REPLACE_SESSION_KEY)
      if (raw) {
        const until = Number(raw) || 0
        replaceCooldownUntil.value = until
      }
    } catch {
      // 隐私模式等存储不可用时忽略。
    }
  }

  function saveReplaceCooldown(): void {
    try {
      sessionStorage.setItem(REPLACE_SESSION_KEY, String(replaceCooldownUntil.value))
    } catch {
      // 忽略。
    }
  }

  /** 替换图纸申请剩余冷却秒数（0 表示可申请）；冷却已过期自动归零，刷新不清空。 */
  function replaceCooldownRemaining(): number {
    const until = replaceCooldownUntil.value
    if (!until) return 0
    const remain = Math.ceil((until - Date.now()) / 1000)
    if (remain <= 0) {
      replaceCooldownUntil.value = 0
      saveReplaceCooldown()
      return 0
    }
    return remain
  }

  /** 恢复本地画布为联机房间权威快照（替换申请提交/取消后使用，避免申请期间本地与共享画布脱节）。 */
  function restoreRoomCanvas(): void {
    if (currentSnapshot.value) useEditorStore().applyCollabSnapshot(currentSnapshot.value)
  }

  /** 成员申请替换整张图纸：1 分钟冷却拦截 + 恢复同步画布 + 提交后轻提示；房主同意后服务端广播 resync 全房间同步。 */
  async function submitReplaceRequest(snapshot: CollabSnapshotDto): Promise<void> {
    if (!isCollabing.value || !isMember.value) return
    const currentToken = token.value
    if (!currentToken) return
    // 提交前先恢复本地画布为联机权威快照，避免申请期间本地与共享画布脱节。
    restoreRoomCanvas()
    if (replaceCooldownRemaining() > 0) {
      replaceNotice.value = `替换申请冷却中，${replaceCooldownRemaining()} 秒后可再申请`
      return
    }
    try {
      await submitCollabReplaceRequest(currentToken, snapshot)
      replaceCooldownUntil.value = Date.now() + REPLACE_COOLDOWN_MS
      saveReplaceCooldown()
      replaceNotice.value = '已向房主申请替换当前图纸，等待审批（1 分钟内有效）'
    } catch (reason) {
      replaceNotice.value = reason instanceof Error && reason.message ? reason.message : '替换申请失败，请稍后重试。'
    }
  }

  /** 房主审批成员替换图纸申请（同意/拒绝）。 */
  async function replaceDecide(applyId: string, accept: boolean): Promise<void> {
    if (!isCollabing.value || !isHost.value) return
    const currentToken = token.value
    if (!currentToken) return
    try {
      await submitCollabReplaceDecide(currentToken, applyId, accept)
    } catch {
      // 失败静默，SSE 会同步审批结果。
    }
  }

  // ---------- 格子锁清扫 ----------

  function startLockSweep(): void {
    stopLockSweep()
    lockSweepTimer = window.setInterval(() => {
      const now = Date.now()
      let changed = false
      cellOwners.forEach((lock, cell) => {
        if (lock.expiresAt <= now) { cellOwners.delete(cell); changed = true }
      })
      if (changed) bumpLocks()
    }, LOCK_SWEEP_INTERVAL)
    // 审批队列（权限/替换）本地即时移除超时记录：倒计时归零即从消息队列消失，不等待后端清扫广播。
    queueSweepTimer = window.setInterval(() => {
      const now = Date.now()
      if (pendingPerms.value.some(item => item.expiresAt > 0 && item.expiresAt <= now)) {
        pendingPerms.value = pendingPerms.value.filter(item => !(item.expiresAt > 0 && item.expiresAt <= now))
      }
      if (pendingReplaces.value.some(item => item.expiresAt > 0 && item.expiresAt <= now)) {
        pendingReplaces.value = pendingReplaces.value.filter(item => !(item.expiresAt > 0 && item.expiresAt <= now))
      }
    }, 1000)
  }

  function stopLockSweep(): void {
    if (lockSweepTimer) { window.clearInterval(lockSweepTimer); lockSweepTimer = 0 }
    if (queueSweepTimer) { window.clearInterval(queueSweepTimer); queueSweepTimer = 0 }
  }

  return {
    phase, room, member, token, inviteCode, busy, applyId, joinWaiting, joinMessage, joinExpiresAt, closedReason, notice,
    isHost, isMember, isCollabing, inviteLink, canEditLocal, canSaveLocal, canUndoOwn, canRedoOwn, myMember, hostMember, friendMembers,
    pendingApplications, memberCount, lockFrames, locksVersion,
    pendingPerms, permMessage, permNotice, permCooldownRemaining,
    pendingReplaces, replaceCooldownRemaining, replaceNotice, currentSnapshot,
    isCellLockedByOther, beginEditOperation, endEditOperation, queueEdit, undoOwnEdit, redoOwnEdit, restoreSession,
    startHost, refreshInvite, join, cancelJoin, approve, reject, kick, setPermission, setSavePermission, requestClear, resyncSnapshot, permApply, permDecide, replaceDecide, submitReplaceRequest, restoreRoomCanvas, leave, teardown,
    suspendConnection, resumeConnection,
  }
})
