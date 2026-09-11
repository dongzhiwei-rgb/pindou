<!--
  文件：App.vue
  用途：拼了个豆工作台的页面入口与主要交互编排。
  核心职责：管理参数/功能弹窗、全屏模式、导出流程、实时保存和跨组件工作流；图纸数据由 Pinia 统一维护。
  版权：@董志伟-联系方式-makabak1204
  最后修改：2026-09-11
-->

<script setup lang="ts">
import { computed, defineAsyncComponent, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { storeToRefs } from 'pinia'
import AppIcon from './components/AppIcon.vue'
import ColorPickerPopover from './components/ColorPickerPopover.vue'
import PatternCanvas from './components/PatternCanvas.vue'
import SplashScreen from './components/SplashScreen.vue'
import StudioMobileNav from './components/StudioMobileNav.vue'
import { abortActiveApiRequests, ApiError, clearActiveLicenseKey, consumeBrowserHandoff, createBrowserHandoff, createKickEventSource, createSwitchEventSource, exportExcel, getActiveLicenseKey, getCloudSave, getLicenseInfo, getSaveLibrary, getSessionToken, getTrialStatus, heartbeatLicense, loginLicense, logoutLicense, saveCloudSave, saveSaveLibrary, setActiveLicenseKey, setSessionToken, startLicenseSession, trialHeartbeat } from './api'
import type { CollabSnapshotDto, LicenseStatus, PortableProject, SavedProject, TrialInfoResponse } from './types'
import { loadDeveloperContact, type DeveloperContact } from './config/developerContact'
import { COLLAB_COLORS, useCollabStore } from './stores/collab'
import { useEditorStore } from './stores/editor'
import { useSoundStore } from './stores/sound'

// 熨烫效果包含独立的大画布渲染逻辑，仅在用户主动查看时加载，减少工作台首屏解析与执行开销。
const IronedPreview = defineAsyncComponent(() => import('./components/IronedPreview.vue'))

type ScreenOrientationControl = {
  lock?: (orientation: 'landscape') => Promise<void>
  unlock?: () => void
}

type AutoSaveState = 'saved' | 'saving' | 'error'
type CloudSaveState = 'idle' | 'saving' | 'saved' | 'error'
type GenerationValidationIssue = { key: string; message: string }
type GenerationStep = 1 | 2 | 3 | 4
type GenerationTarget = 'image' | 'blank'
type GenerationDraft = {
  brandId: string
  paletteId: string
  boardId: string
  width: number
  height: number
  maxColors: number
  dither: boolean
  removeBackground: boolean
  backgroundThreshold: number
  noiseSuppression: number
}
type DetectedGenerationSpecs = Pick<GenerationDraft, 'width' | 'height' | 'maxColors'> & { blockCount: number }

const store = useEditorStore()
const {
  brands, boards, palette, selectedBrandId, selectedPaletteId, selectedBoardId, title,
  width, height, maxColors, dither, removeBackground, backgroundThreshold, noiseSuppression,
  beadShape, showCodes, showGrid, showBoardSplit, showCoordinates, cellSize, interactionMode,
  cells, colors, selectedColorIndex, history, future, loading, processingInfo, error,
  selectedBrand, selectedBoard, hasPattern, usedColorCount, beadCount, boardCount, colorCounts, contentRevision,
  physicalWidth, physicalHeight, sizeMismatch, editingLocked,
  mergeStats,
} = storeToRefs(store)

// PC 快捷色栏使用稳定队列：初始化时优先装入当前色和已使用色，之后不再跟随豆子数量重新排序。
// 当前选中色若已在列表中则保持原位置，只有不在列表中时才插入首位。
const quickPaletteColorIds = ref<string[]>([])

watch(colors, (currentColors, previousColors) => {
  const currentIds = currentColors.map(color => color.id)
  const currentIdSet = new Set(currentIds)
  const previousIds = previousColors?.map(color => color.id) ?? []
  // 色号选择器追加一个候选色时先保留现有队列，随后由 selectedColorIndex 监听决定是否插到首位。
  const isPaletteAppend = previousIds.length > 0
    && currentIds.length > previousIds.length
    && previousIds.every((id, index) => currentIds[index] === id)

  if (isPaletteAppend) {
    quickPaletteColorIds.value = quickPaletteColorIds.value.filter(id => currentIdSet.has(id))
    return
  }

  const selectedId = currentColors[selectedColorIndex.value]?.id
  const usedIds = currentColors
    .filter((_, index) => (colorCounts.value[index] ?? 0) > 0)
    .map(color => color.id)
  quickPaletteColorIds.value = [...new Set([
    ...(selectedId ? [selectedId] : []),
    ...usedIds,
    ...currentIds,
  ])].slice(0, 8)
}, { immediate: true, flush: 'post' })

watch(selectedColorIndex, (index) => {
  const selectedId = colors.value[index]?.id
  if (!selectedId || quickPaletteColorIds.value.includes(selectedId)) return
  quickPaletteColorIds.value = [selectedId, ...quickPaletteColorIds.value].slice(0, 8)
}, { flush: 'sync' })

const quickPaletteColors = computed(() => {
  return quickPaletteColorIds.value.flatMap(id => {
    const index = colors.value.findIndex(color => color.id === id)
    const color = colors.value[index]
    return index >= 0 && color ? [{ color, index }] : []
  })
})

// 好友联机：房间、成员、邀请码、审批与编辑权限统一由联机状态仓库维护，UI 只负责展示与触发。
const collab = useCollabStore()
const {
  phase: collabPhase, room: collabRoom, member: collabMember, inviteCode: collabInviteCode,
  busy: collabBusy, joinWaiting: collabJoinWaiting, joinMessage: collabJoinMessage, joinExpiresAt: collabJoinExpiresAt,
  closedReason: collabClosedReason, notice: collabNotice,
  isHost: collabIsHost, isMember: collabIsMember, isCollabing: collabActive,
  canEditLocal: collabCanEdit, canSaveLocal: collabCanSave, friendMembers: collabFriends, pendingApplications: collabPending,
  canUndoOwn: collabCanUndo, canRedoOwn: collabCanRedo,
  memberCount: collabMemberCount, myMember: collabMyMember, inviteLink: collabInviteLink,
  pendingPerms: collabPendingPerms, permMessage: collabPermMessage, permNotice: collabPermNotice,
  pendingReplaces: collabPendingReplaces, replaceNotice: collabReplaceNotice,
} = storeToRefs(collab)
// 音效：放豆 / 取出 / 打开色号选择器的提示音，开关由底部信息栏「声音」控制。
const sound = useSoundStore()
const { enabled: soundEnabled } = storeToRefs(sound)

// ---------- 好友联机 UI 状态 ----------
const collabDialog = ref<HTMLDialogElement | null>(null)
const collabExitDialog = ref<HTMLDialogElement | null>(null)
const collabInviteInput = ref('')
// 用户主动退出标记：用于区分「主动退出」与「被踢/房间关闭」的提示。
let collabLeftManually = false
// 联机倒计时基准：每秒刷新，用于房主申请列表与成员申请等待区的倒计时显示。
const collabNow = ref(Date.now())
let collabTickTimer = 0
// 成员权限提示「首次进入」标记：进入联机时的初始权限变化（编辑默认关闭）不弹提示，仅房主后续调整时提示。
let collabPermPrimed = false

// 联机结束原因 -> 提示文案（被踢/房主离线/房主结束等）。
function collabCloseMessage(reason: string): string {
  switch (reason) {
    case 'host_offline': return '房主已离线，联机已结束。'
    case 'host_left': return '房主已结束联机。'
    case 'host_license_expired': return '房主授权到期，联机已结束。'
    case 'idle_timeout': return '联机空闲超时，已自动结束。'
    case 'kicked': return '你已被房主踢出联机。'
    case 'member_offline': return '你已离线，联机已结束。'
    case 'license_expired': return '授权已到期，联机已结束。'
    case 'trial_expired': return '试用已到期，联机已结束。'
    default: return '已退出联机。'
  }
}

// 进入联机时自动打开联机弹窗，便于房主直接分享邀请码。
watch(collabActive, (active, wasActive) => {
  if (active && !wasActive) {
    collabPermPrimed = false
    collabDialog.value?.showModal()
  }
  if (!active && wasActive && !collabLeftManually) {
    // 被踢 / 房间关闭：按原因给出明确提示；主动退出不重复提示。
    notify(collabCloseMessage(collabClosedReason.value), true)
  }
  collabLeftManually = false
  collabClosedReason.value = ''
  // 联机结束/刷新恢复时同步收起轻弹窗，避免残留悬浮层。
  if (!active) {
    collabBellOpen.value = false
    collabMemberOpen.value = false
  }
})
// 房主端一次性事件提示（成员等待审批期间离线，申请自动取消）。
watch(collabNotice, (message) => {
  if (!message) return
  notify(message, true)
  collabNotice.value = ''
})
// 成员端权限申请一次性轻提示（审批结果 / 申请超时）：弹出 Toast 后清空。
watch(collabPermNotice, (message) => {
  if (!message) return
  notify(message, true)
  collabPermNotice.value = ''
})
// 成员端替换图纸申请一次性轻提示（已提交 / 冷却 / 审批结果 / 超时）：弹出 Toast 后清空。
watch(collabReplaceNotice, (message) => {
  if (!message) return
  notify(message, true)
  collabReplaceNotice.value = ''
})
// 申请终态轻提示：等待结束（超时/被拒/房主离线等）且存在提示文案时弹出 Toast；主动取消（空文案）不提示。
watch(collabJoinMessage, (message) => {
  if (!message || collabJoinWaiting.value || collabPhase.value !== 'idle') return
  notify(message, true)
})
// 房主单独调整本成员编辑权限时即时提示。
watch(collabCanEdit, (canEdit, wasCanEdit) => {
  if (!collabActive.value || !collabIsMember.value || canEdit === wasCanEdit) return
  // 进入联机的初始权限不提示（编辑权限默认关闭）；仅房主后续调整权限时提示。
  if (!collabPermPrimed) { collabPermPrimed = true; return }
  notify(canEdit ? '房主已恢复你的编辑权限' : '房主已关闭你的编辑权限，当前为只读', true)
})
// 共享权限（保存/导出）变更时给成员端轻提示：与编辑权限提示并列。
watch(collabCanSave, (canSave, wasCanSave) => {
  if (!collabActive.value || !collabIsMember.value || canSave === wasCanSave) return
  notify(canSave ? '房主已开启你的共享权限，可保存、导出图纸' : '房主已关闭你的共享权限，仅可查看', true)
})
// 联机成员被房主关闭编辑权限时为只读，禁用绘制/取出/复制等编辑工具。
const collabReadOnly = computed(() => collabActive.value && !collabCanEdit.value)

// ---------- 联机轻弹窗：右上角铃铛（联机申请通知）+ 顶部「联机中」成员列表 ----------
const collabBellOpen = ref(false)
const collabMemberOpen = ref(false)
// 房主权限申请铃铛：独立于联机申请铃铛，表示「x豆申请xx权限」待审批消息。
const collabPermBellOpen = ref(false)
// 铃铛仅房主联机中且有待审批申请时展示；没有申请消息和通知时不显示，悬浮页面右上角顶层，不遮挡正文。
const collabBellVisible = computed(() => collabActive.value && collabIsHost.value && collabPending.value.length > 0)
// 房主审批消息铃铛：权限申请 + 替换图纸申请统一入口（均带倒计时，超时自动移出队列）。
const collabPermBellVisible = computed(() => collabActive.value && collabIsHost.value && (collabPendingPerms.value.length > 0 || collabPendingReplaces.value.length > 0))

// 申请倒计时：给定过期时间（Unix 毫秒）距当前剩余的秒数；已过期返回 0。
function collabRemainSeconds(expiresAt: number | null | undefined): number {
  if (!expiresAt) return 0
  const remain = Math.ceil((expiresAt - collabNow.value) / 1000)
  return remain > 0 ? remain : 0
}
// 格式化倒计时文案：仅显示秒数（如 30s），不带多余文字。
function collabCountdownText(expiresAt: number | null | undefined): string {
  return `${collabRemainSeconds(expiresAt)}s`
}

function toggleCollabBell(): void {
  collabMemberOpen.value = false
  collabPermBellOpen.value = false
  collabBellOpen.value = !collabBellOpen.value
}

function toggleCollabPermBell(): void {
  collabBellOpen.value = false
  collabMemberOpen.value = false
  collabPermBellOpen.value = !collabPermBellOpen.value
}

function toggleCollabMemberPopup(): void {
  collabBellOpen.value = false
  collabPermBellOpen.value = false
  collabMemberOpen.value = !collabMemberOpen.value
}

// 点击弹窗外部区域时关闭联机轻弹窗。
function closeCollabPopupsOnOutside(event: PointerEvent): void {
  const target = event.target instanceof Element ? event.target : null
  if (!target) return
  if (collabBellOpen.value && !target.closest('.collab-bell-wrap')) collabBellOpen.value = false
  if (collabPermBellOpen.value && !target.closest('.collab-perm-bell-wrap')) collabPermBellOpen.value = false
  if (collabMemberOpen.value && !target.closest('.collab-member-wrap')) collabMemberOpen.value = false
}

// 页面自身只保存临时 UI 状态；图纸数据统一由 Pinia 管理。
const showSplash = ref(true)
const splashLeaving = ref(false)
const workstationReady = ref(false)
const imageFile = ref<File | null>(null)
const previewUrl = ref('')
const dragging = ref(false)

// 商用授权：当前已激活的密钥与校验结果，仅保存在本地。
const licensedKey = ref(getActiveLicenseKey())
const licenseDialog = ref<HTMLDialogElement | null>(null)
const syncCloudDialog = ref<HTMLDialogElement | null>(null)
const licenseInput = ref('')
// 密钥登录防过载：连续失败 5 次锁定 30 秒。
const loginFailCount = ref(0)
const loginLockUntil = ref(0)
const loginLockRemaining = ref(0)
let loginLockTimer = 0
const licenseInfo = ref<LicenseStatus | null>(null)
const licenseVerifying = ref(false)
const isLicensed = computed(() => Boolean(licensedKey.value))
// 次数用完（exhausted）时禁止上传生成/导出/下载效果图；查看类功能不受影响。
const produceLocked = computed(() => licenseInfo.value?.status === 'exhausted')
// 顶部授权按钮文案：密钥到期/次数用完显示「授权到期」，正常显示「已授权」，快到期附剩余时长。
const licenseEntryLabel = computed(() => {
  if (licenseInfo.value?.status === 'time_expired' || licenseInfo.value?.status === 'exhausted') return '授权到期'
  if (licenseRemainingSeconds.value > 0 && licenseRemainingSeconds.value <= 600) return `已授权 · 剩 ${formatDuration(licenseRemainingSeconds.value)}`
  return '已授权'
})
// 无密钥访客的试用倒计时状态；首次加载时由后端按 IP 返回，归零后提示获取密钥。
const trialRemainingSeconds = ref(0)
const trialRemainingGenerations = ref(0)
const trialTotalMinutes = ref(10)
const trialExpired = ref(false)
// 是否已成功取得后端试用状态；接口失败时保持宽松，避免误拦截。
const trialResolved = ref(false)
// 后端通过配置关闭授权/试用功能；为 true 时隐藏授权与试用入口，并保持宽松放行。
const licensingDisabled = ref(false)
// 顶部只承担异常提醒：有效授权和正常试用均不占用导航空间，入口仍可从“功能”面板进入。
const showTopLicenseEntry = computed(() => {
  if (licensingDisabled.value) return false
  if (!licensedKey.value) return trialExpired.value
  return licenseInfo.value?.status === 'time_expired' || licenseInfo.value?.status === 'exhausted'
})
// 开关状态主通道为 SSE 长连接（空闲时几乎无流量）；轮询仅作为断线时的低频兜底，避免双重请求开销。
let switchPollTimer = 0
const switchPollInterval = 60000
// SSE 断开期间的兜底轮询间隔（较短以便尽快发现开关变化）。
const switchPollFallbackInterval = 10000
let switchEventSource: EventSource | null = null
// SSE 断线重连退避：避免服务异常时高频重连风暴。
let switchReconnectDelay = 3000
let switchReconnectTimer = 0
let switchConnecting = false
let switchConnectionVersion = 0
let switchPollInFlight = false
// pagehide 后禁止任何异步回调重建长连接；从 BFCache 返回时由 pageshow 显式恢复。
let streamingActive = true
// 密钥剩余在线时长（秒），由心跳响应刷新并本地每秒倒计时；关闭浏览器后不计时。
const licenseRemainingSeconds = ref(0)
let licenseCountdownTimer = 0
// 标签页被踢下线标记（sessionStorage）：防止被踢标签页刷新后自动重登导致循环踢。
const SESSION_KICKED_KEY = 'pindou-session-kicked'
let heartbeatTimer = 0
let licenseHeartbeatInFlight = false
let activeHeartbeatIdentity = ''
let licenseHeartbeatGeneration = 0
let licenseHeartbeatFailures = 0
// 「被踢下线」SSE 长连接：新设备登录时旧设备收到 session-kicked 事件并立即下线。
let kickSource: EventSource | null = null
// 防止 refreshLicenseState 并发触发多次连接，避免同一时间创建多个长连接导致浏览器连接池被占满。
let kickConnecting = false
let kickReconnectTimer = 0
// 授权状态可能同时被首屏、SSE 和兜底轮询触发；只允许一个请求在途，其余触发合并为一次补刷。
let licenseRefreshPromise: Promise<void> | null = null
let licenseRefreshQueued = false
let cloudSaveTimer = 0
let cloudSaveInFlight = false
let cloudSavePending = false
const cloudSaveState = ref<CloudSaveState>('idle')
const cloudSaveStatus = ref('已保存到本地')
let trialTimer = 0
let trialHeartbeatTimer = 0
let trialHeartbeatInFlight = false
let trialHeartbeatGeneration = 0
let trialHeartbeatFailures = 0
const HEARTBEAT_NORMAL_DELAY = 5000
const HEARTBEAT_DEGRADED_DELAY = 15000
const HEARTBEAT_FAILURE_THRESHOLD = 3
// 主标签心跳协调：同一浏览器多标签页共用一份心跳，避免全局限流下卡顿。
const heartbeatChannel = 'BroadcastChannel' in window ? new BroadcastChannel('pindou-heartbeat-leader') : null
const heartbeatTabId = crypto.randomUUID?.() ?? `${Date.now()}-${Math.random()}`
const HEARTBEAT_LEASE_KEY = 'pindou-heartbeat-lease'
let heartbeatLeader = false
let heartbeatLeaderTimer = 0
const heartbeatLeaderPing = 'pindou-heartbeat-leader-ping'
const trialRemainingLabel = computed(() => {
  const seconds = Math.max(0, trialRemainingSeconds.value)
  const minutes = Math.floor(seconds / 60)
  const rest = seconds % 60
  return `${String(minutes).padStart(2, '0')}:${String(rest).padStart(2, '0')}`
})
// 密钥非正常状态（到期 time_expired / 次数用完 exhausted）时锁定画布编辑，仅保留清空。
watch(licenseInfo, info => {
  editingLocked.value = info != null && info.status !== 'active'
})
const toast = ref('')
// 消息提示是否显示好友联机图标：仅联机相关消息显示，普通消息保持简洁（避免图标一直出现）。
const toastIcon = ref(false)
// 快捷工具提示仅在移动端显示于底部；普通业务提示仍保持页面顶部位置。
const toastMobileBottom = ref(false)
// 剩余时长本地每秒倒计时：心跳返回基准秒数后自动启动，归零自动停止。
watch(licenseRemainingSeconds, (seconds, previous) => {
  if (seconds <= 0) {
    stopLicenseCountdown()
  } else if (previous === 0 || seconds > previous) {
    // 从 0 开始计时，或心跳校准为更大值时重启。
    startLicenseCountdown()
  }
})
const toastElement = ref<HTMLElement | null>(null)
let toastTimer = 0
const exportBusy = ref(false)
const fileInput = ref<HTMLInputElement | null>(null)
const centerStage = ref<HTMLElement | null>(null)
const stageTools = ref<HTMLElement | null>(null)
const exportDialog = ref<HTMLDialogElement | null>(null)
const saveDialog = ref<HTMLDialogElement | null>(null)
const saveName = ref('')
const boardExpandDialog = ref<HTMLDialogElement | null>(null)
const expandWidth = ref(0)
const expandHeight = ref(0)
// 拼接目标宽高上限 160：输入超过上限时自动修正为 160（NaN 输入保留原值，避免异常）。
watch(expandWidth, (value) => { if (typeof value === 'number' && value > 160) expandWidth.value = 160 })
watch(expandHeight, (value) => { if (typeof value === 'number' && value > 160) expandHeight.value = 160 })
const libraryDialog = ref<HTMLDialogElement | null>(null)
const libraryList = ref<SavedProject[]>([])
const libraryLoading = ref(false)
const pendingOpenProject = ref<SavedProject | null>(null)
const confirmOpenDialog = ref<HTMLDialogElement | null>(null)
const importDialog = ref<HTMLDialogElement | null>(null)
const importFileInput = ref<HTMLInputElement | null>(null)
const importDragging = ref(false)
const importFileName = ref('')
const importFileError = ref('')
const pendingImportProject = ref<PortableProject | null>(null)
const importBusy = ref(false)
const confirmImportDialog = ref<HTMLDialogElement | null>(null)
const boardExpandPresets = [32, 48, 64, 96, 128, 160]
const selectedLibraryIds = ref<string[]>([])
const pendingDeleteItems = ref<SavedProject[]>([])
const confirmDeleteDialog = ref<HTMLDialogElement | null>(null)
const renameDialog = ref<HTMLDialogElement | null>(null)
const renameTarget = ref<SavedProject | null>(null)
const renameInput = ref('')
const canvasMetaDialog = ref<HTMLDialogElement | null>(null)
const settingsDialog = ref<HTMLDialogElement | null>(null)
// 图纸生成在桌面端作为按需右侧面板参与工作区布局；窄屏和全屏时退化为模态底部抽屉。
const generationPanelOpen = ref(false)
const generationPanelDocked = ref(false)
const generationStep = ref<GenerationStep>(1)
const generationTarget = ref<GenerationTarget>('image')
// 生成参数与当前画板隔离：只有用户在最终确认后才会写入 Pinia，关闭面板不会损坏已有图纸。
const generationDraft = reactive<GenerationDraft>({
  brandId: '',
  paletteId: '',
  boardId: '',
  width: 32,
  height: 32,
  maxColors: 20,
  dither: false,
  removeBackground: false,
  backgroundThreshold: 10,
  noiseSuppression: 2,
})
const generationSubmitting = ref(false)
const cropDialog = ref<HTMLDialogElement | null>(null)
const cropViewport = ref<HTMLElement | null>(null)
const cropImage = ref<HTMLImageElement | null>(null)
const generateConfirmDialog = ref<HTMLDialogElement | null>(null)
const materialsDialog = ref<HTMLDialogElement | null>(null)
const replacementDialog = ref<HTMLDialogElement | null>(null)
const saveErrorDialog = ref<HTMLDialogElement | null>(null)
const clearCanvasDialog = ref<HTMLDialogElement | null>(null)
const weChatExportDialog = ref<HTMLDialogElement | null>(null)
const weChatBrowserDialog = ref<HTMLDialogElement | null>(null)
const developerDialog = ref<HTMLDialogElement | null>(null)
const patternCanvas = ref<InstanceType<typeof PatternCanvas> | null>(null)
const ironedPreview = ref<HTMLElement | null>(null)
const showIronedPreview = ref(false)
const ironedPreviewOwnsFullscreen = ref(false)
const isFullscreen = ref(false)
const usesFullscreenFallback = ref(false)
const forceLandscape = ref(false)
const ironedForceLandscape = ref(false)
const toolsCollapsed = ref(true)
const mobileLayoutMedia = '(max-width: 820px), (pointer: coarse) and (orientation: landscape) and (max-height: 520px)'
const isMobileLayout = ref(typeof window !== 'undefined' && window.matchMedia(mobileLayoutMedia).matches)
const autoSaveStatus = ref('已实时保存')
const autoSaveState = ref<AutoSaveState>('saved')
const autoSaveError = ref('')
const weChatPreviewUrl = ref('')
const weChatHasOtherFiles = ref(false)
const showSystemBrowserGuide = ref(false)
const systemBrowserAddress = ref('')
const systemBrowserPreparing = ref(false)
const replacementFromIndex = ref(-1)
const replacementToIndex = ref(0)
const exportSelection = reactive({ png: true, xlsx: true, csv: false, json: false })
const developerContact = reactive<DeveloperContact>({ weChatId: '', qrCodeUrl: '' })
const applicationVersion = 'V 0.2.1'
const gridPresets = [16, 24, 32, 48, 64, 80, 96, 128]
const generationSteps: ReadonlyArray<{ step: GenerationStep; short: string; label: string }> = [
  { step: 1, short: '上传', label: '上传图片' },
  { step: 2, short: '豆子', label: '选择豆子' },
  { step: 3, short: '图纸', label: '设置图纸' },
  { step: 4, short: '创建', label: '选择创建方式' },
]

// 裁剪状态只在裁剪弹窗存活期间使用，确认后会生成独立 PNG 文件并释放原图 URL。
const cropSourceFile = ref<File | null>(null)
const cropSourceUrl = ref('')
const cropNaturalWidth = ref(0)
const cropNaturalHeight = ref(0)
const cropBaseScale = ref(1)
const cropZoom = ref(1)
const cropOffsetX = ref(0)
const cropOffsetY = ref(0)
const cropBusy = ref(false)
const pendingDetectedSpecs = ref<DetectedGenerationSpecs | null>(null)
const pendingGenerationTitle = ref('')
let cropDetectionRequestId = 0
let cropDrag: { pointerId: number; startX: number; startY: number; offsetX: number; offsetY: number } | null = null

const currentPaletteSummary = computed(() => selectedBrand.value?.palettes.find(item => item.id === selectedPaletteId.value) || null)
// 当前色卡支持的底板：按色卡 beadSizes 过滤，豆径以 0.1mm 容差匹配，避免浮点误差。
const compatibleBoards = computed(() => {
  const sizes = currentPaletteSummary.value?.beadSizes || []
  return boards.value.filter(board => sizes.some(size => Math.abs(board.beadSize - size) < 0.1))
})
const boardSummary = computed(() => selectedBoard.value ? `${selectedBoard.value.columns}×${selectedBoard.value.rows} · ${selectedBoard.value.beadSize}mm` : '—')

// 生成面板使用自己的品牌、色卡与底板派生状态，不复用当前画板的选中结果。
const generationBrand = computed(() => brands.value.find(item => item.id === generationDraft.brandId) || null)
const generationPaletteSummary = computed(() => generationBrand.value?.palettes.find(item => item.id === generationDraft.paletteId) || null)
const generationCompatibleBoards = computed(() => {
  const sizes = generationPaletteSummary.value?.beadSizes || []
  return boards.value.filter(board => sizes.some(size => Math.abs(board.beadSize - size) < 0.1))
})
const generationBoard = computed(() => boards.value.find(board => board.id === generationDraft.boardId) || null)
const generationBoardSummary = computed(() => generationBoard.value
  ? `${generationBoard.value.columns}×${generationBoard.value.rows} · ${generationBoard.value.beadSize}mm`
  : '—')
const generationSizeMismatch = computed(() => {
  const board = generationBoard.value
  const sizes = generationPaletteSummary.value?.beadSizes || []
  return board ? !sizes.some(size => Math.abs(board.beadSize - size) < 0.1) : false
})

// 画布底部信息栏文本：合并尺寸、物理尺寸、底板与处理信息；过长省略，点击信息栏弹窗显示完整。
const canvasMetaText = computed(() => {
  const parts = [
    `${width.value}×${height.value}颗`,
    `${physicalWidth.value.toFixed(1)}×${physicalHeight.value.toFixed(1)}cm`,
    boardSummary.value,
  ]
  if (processingInfo.value) parts.push(processingInfo.value)
  return parts.join(' · ')
})

// 点击画板信息省略文本：弹窗展示完整画板信息。
function openCanvasMetaDialog(): void {
  canvasMetaDialog.value?.showModal()
}
const zoomPercent = computed(() => Math.round(cellSize.value / 20 * 100))
const minimumCanvasCellSize = ref(2)
const minimumZoomPercent = computed(() => Math.round(minimumCanvasCellSize.value / 20 * 100))

function updateMinimumCanvasZoom(nextSize: number): void {
  minimumCanvasCellSize.value = Math.min(40, Math.max(2, Math.round(nextSize)))
}
const cropAspectRatio = computed(() => {
  const specs = pendingDetectedSpecs.value
  const draftWidth = specs?.width ?? generationDraft.width
  const draftHeight = specs?.height ?? generationDraft.height
  return Math.max(1 / 10, draftWidth / Math.max(1, draftHeight))
})
const cropViewportStyle = computed(() => ({ '--crop-aspect': String(cropAspectRatio.value) }))
const cropImageStyle = computed(() => {
  const scale = cropBaseScale.value * cropZoom.value
  return {
    width: `${cropNaturalWidth.value * scale}px`,
    height: `${cropNaturalHeight.value * scale}px`,
    left: `calc(50% + ${cropOffsetX.value}px)`,
    top: `calc(50% + ${cropOffsetY.value}px)`,
  }
})
const noiseSuppressionLabel = computed(() => ['关闭', '轻度', '标准', '强力'][noiseSuppression.value] || '标准')
const generationNoiseSuppressionLabel = computed(() => ['关闭', '轻度', '标准', '强力'][generationDraft.noiseSuppression] || '标准')
const replacementSourceOptions = computed(() => {
  // 用编辑器维护的增量统计代替扫描整张图纸；大规格图纸打开替换弹窗时不再遍历数万格。
  return colorCounts.value
    .map((count, index) => ({ index, count, color: colors.value[index] }))
    .filter(item => item.count > 0)
    .filter(item => item.color)
    .sort((first, second) => second.count - first.count || first.color.code.localeCompare(second.color.code))
})
const replacementSourceColor = computed(() => colors.value[replacementFromIndex.value] || null)
const replacementTargetColor = computed(() => colors.value[replacementToIndex.value] || null)
const replacementSourceCount = computed(() => replacementSourceOptions.value.find(item => item.index === replacementFromIndex.value)?.count || 0)
const replacementSourceIndices = computed(() => replacementSourceOptions.value.map(item => item.index))
const replacementSourceCounts = computed<Record<number, number>>(() => Object.fromEntries(
  replacementSourceOptions.value.map(item => [item.index, item.count]),
))
const generationValidationIssues = computed<GenerationValidationIssue[]>(() => {
  const issues: GenerationValidationIssue[] = []
  if (!generationBrand.value) issues.push({ key: 'brand', message: '请选择豆子品牌' })
  if (!generationPaletteSummary.value) issues.push({ key: 'palette', message: '请选择可用色卡' })
  if (!generationBoard.value) issues.push({ key: 'board', message: '请选择实际使用的底板' })
  if (generationSizeMismatch.value) issues.push({ key: 'board', message: '当前色卡豆径与所选底板不兼容' })
  if (!Number.isInteger(generationDraft.width) || generationDraft.width < 8 || generationDraft.width > 160) {
    issues.push({ key: 'width', message: '横向颗数应为 8–160 的整数' })
  }
  if (!Number.isInteger(generationDraft.height) || generationDraft.height < 8 || generationDraft.height > 160) {
    issues.push({ key: 'height', message: '纵向颗数应为 8–160 的整数' })
  }

  const gridCellCount = Number.isInteger(generationDraft.width) && Number.isInteger(generationDraft.height)
    && generationDraft.width > 0 && generationDraft.height > 0
    ? generationDraft.width * generationDraft.height
    : 0
  const paletteLimit = Math.min(96, generationPaletteSummary.value?.colorCount || 96, gridCellCount || 96)
  if (!Number.isInteger(generationDraft.maxColors) || generationDraft.maxColors < 2 || generationDraft.maxColors > paletteLimit) {
    issues.push({ key: 'maxColors', message: `最多颜色应为 2–${paletteLimit}，且不能超过图纸总格数` })
  }
  if (!Number.isInteger(generationDraft.noiseSuppression) || generationDraft.noiseSuppression < 0 || generationDraft.noiseSuppression > 3) {
    issues.push({ key: 'noiseSuppression', message: '杂色抑制强度设置不正确' })
  }
  if (generationDraft.removeBackground && (!Number.isFinite(generationDraft.backgroundThreshold)
    || generationDraft.backgroundThreshold < 2 || generationDraft.backgroundThreshold > 30)) {
    issues.push({ key: 'backgroundThreshold', message: '背景容差应在 2–30 之间' })
  }
  return issues
})
const generationValidationErrors = computed(() => generationValidationIssues.value.map(issue => issue.message))
const selectedExportCount = computed(() => Object.values(exportSelection).filter(Boolean).length)
const isWeChat = typeof navigator !== 'undefined' && /MicroMessenger/i.test(navigator.userAgent)

// 开屏结束与工作台初始化可能先后完成；两者均完成后再显示微信环境提示，避免原生弹窗盖住品牌动画。
function showWeChatGuideWhenReady(): void {
  if (!isWeChat || showSplash.value || !workstationReady.value) return
  void nextTick(() => {
    if (!weChatBrowserDialog.value?.open) weChatBrowserDialog.value?.showModal()
  })
}

function beginSplashExit(): void {
  splashLeaving.value = true
}

function finishSplash(): void {
  showSplash.value = false
  splashLeaving.value = false
  showWeChatGuideWhenReady()
}
let autoSaveTimer: number | undefined
let stopAutoSave: (() => void) | undefined

function describeSaveError(reason: unknown): string {
  if (reason instanceof DOMException) {
    if (reason.name === 'QuotaExceededError') return '浏览器本地存储空间不足，无法保存当前图纸。'
    if (reason.name === 'SecurityError') return '浏览器禁止使用本地存储，请检查隐私或站点权限设置。'
  }
  if (reason instanceof Error && reason.message) return reason.message
  return '未知错误，浏览器未能写入本地草稿。'
}

function performAutoSave(showSuccessToast = false): boolean {
  autoSaveState.value = 'saving'
  autoSaveStatus.value = '实时保存中'

  try {
    store.saveLocal()
    autoSaveState.value = 'saved'
    autoSaveStatus.value = '已实时保存'
    // 未登录时显示本地保存完成；登录后由云端保存状态接管显示。
    if (!licensedKey.value) {
      cloudSaveState.value = 'saved'
      cloudSaveStatus.value = '已保存到本地'
    }
    autoSaveError.value = ''
    if (saveErrorDialog.value?.open) saveErrorDialog.value.close()
    if (showSuccessToast) notify('图纸已保存')
    return true
  } catch (reason) {
    autoSaveState.value = 'error'
    autoSaveStatus.value = '保存失败'
    autoSaveError.value = describeSaveError(reason)
    if (!saveErrorDialog.value?.open) saveErrorDialog.value?.showModal()
    return false
  }
}

function scheduleAutoSave(): void {
  if (!hasPattern.value) return
  // 联机成员不保存豆板数据：共享画布仅由房主端自动保存，成员退出后恢复本人数据。
  if (collabIsMember.value) return
  autoSaveState.value = 'saving'
  autoSaveStatus.value = '实时保存中'
  cloudSaveState.value = 'saving'
  cloudSaveStatus.value = '实时保存中'
  if (autoSaveTimer) window.clearTimeout(autoSaveTimer)
  autoSaveTimer = window.setTimeout(() => {
    performAutoSave()
    autoSaveTimer = undefined
  }, 1000)
}

function closeToolsOnOutsidePointer(event: PointerEvent): void {
  if (toolsCollapsed.value) return
  const target = event.target as HTMLElement | null
  if (!target || stageTools.value?.contains(target)) return
  if (target.closest('dialog[open], .ironed-preview-host')) return
  if (target.closest('.nav-function-toggle, .floating-toolbar-toggle, .studio-mobile-nav')) return
  toolsCollapsed.value = true
}

function selectPanOutsideEditingArea(event: PointerEvent): void {
  const target = event.target instanceof Element ? event.target : null
  if (!target) return
  // 仅在画布本身与编辑工具内保留当前工具；点击画板外其余区域（含缩放进度条、画板容器空白、页面背景等）立即切回拖拽，防止误落豆。
  if (target.closest('.pattern-canvas, .quick-edit-toolbar, .studio-mobile-nav, .color-popover-panel')) return
  interactionMode.value = 'pan'
}

function syncResponsiveLayout(): void {
  const nextMobileLayout = window.matchMedia(mobileLayoutMedia).matches
  const layoutChanged = nextMobileLayout !== isMobileLayout.value
  isMobileLayout.value = nextMobileLayout
  remountGenerationPanelIfNeeded()

  if (layoutChanged && hasPattern.value) {
    void nextTick(() => patternCanvas.value?.fitPatternInViewport())
  }
}

function isMobileDevice(): boolean {
  return window.matchMedia('(pointer: coarse)').matches || /Android|iPhone|iPad|iPod|Mobile/i.test(navigator.userAgent)
}

function isPortraitViewport(): boolean {
  // 微信的 orientation 媒体查询偶尔会沿用进入全屏前的值，实际可视区域宽高更可靠。
  const viewportWidth = window.visualViewport?.width || window.innerWidth
  const viewportHeight = window.visualViewport?.height || window.innerHeight
  if (Math.abs(viewportHeight - viewportWidth) > 2) return viewportHeight > viewportWidth
  return window.matchMedia('(orientation: portrait)').matches
}

function syncLandscapeFallback(): void {
  // 部分移动浏览器会让方向锁定 Promise 成功，但屏幕仍然保持竖屏。
  // 因此以实际媒体方向为准：只要仍是竖屏，就启用 CSS 横屏兜底。
  const needsLayoutRotation = isMobileDevice()
    && isPortraitViewport()
  forceLandscape.value = isFullscreen.value && needsLayoutRotation
  // 熨烫预览位于已旋转的画板全屏内时无需再次旋转，否则会恢复成竖向。
  ironedForceLandscape.value = showIronedPreview.value && !isFullscreen.value && needsLayoutRotation
}

function resetFullscreenOrientation(): void {
  forceLandscape.value = false
  ironedForceLandscape.value = false
  try {
    ;(screen.orientation as ScreenOrientationControl | undefined)?.unlock?.()
  } catch {
    // 部分浏览器暴露了方向接口，但不允许主动解锁。
  }
}

function syncFullscreenState(): void {
  const wasFullscreen = isFullscreen.value
  isFullscreen.value = document.fullscreenElement === centerStage.value || usesFullscreenFallback.value
  ironedPreviewOwnsFullscreen.value = document.fullscreenElement === ironedPreview.value
  if (!isFullscreen.value && !showIronedPreview.value) {
    resetFullscreenOrientation()
  } else {
    syncLandscapeFallback()
  }
  // 全屏会改变生成面板的承载方式：桌面停靠栏在全屏内需重新挂载为模态面板。
  if (wasFullscreen !== isFullscreen.value) void nextTick(syncResponsiveLayout)
}

async function lockLandscape(): Promise<void> {
  if (!isMobileDevice()) return
  // 先执行布局兜底，不能等待微信中可能长期不返回的方向锁定 Promise。
  syncLandscapeFallback()
  try {
    const lock = (screen.orientation as ScreenOrientationControl | undefined)?.lock
    if (!lock) throw new Error('当前浏览器不支持屏幕方向锁定')
    await Promise.race([
      lock.call(screen.orientation, 'landscape'),
      new Promise<void>(resolve => window.setTimeout(resolve, 350)),
    ])
  } catch {
    // 微信和 iOS 常不开放方向锁定，竖屏时由布局旋转作为兜底。
  } finally {
    syncLandscapeFallback()
    // 全屏和方向切换可能晚于 Promise 完成，再校验一次真实方向。
    window.setTimeout(syncLandscapeFallback, 250)
  }
}

function enterFullscreenFallback(): void {
  usesFullscreenFallback.value = true
  isFullscreen.value = true
  document.documentElement.classList.add('fullscreen-fallback-active')
}

function exitFullscreenFallback(): void {
  usesFullscreenFallback.value = false
  isFullscreen.value = false
  document.documentElement.classList.remove('fullscreen-fallback-active')
  resetFullscreenOrientation()
}

// fullscreenchange 也覆盖用户按 Esc 退出的情况，按钮状态不会滞后。
onMounted(async () => {
  streamingActive = true
  const contactRequest = loadDeveloperContact()
    .then(contact => Object.assign(developerContact, contact))
    .catch(() => notify('开发者联系方式配置加载失败'))
  await Promise.all([store.initialize(), contactRequest])
  await restoreBrowserHandoffFromUrl()
  workstationReady.value = true
  // 恢复草稿后按当前视口完整展示画板；仅改变视图，不触发内容自动保存。
  await nextTick()
  if (hasPattern.value) await patternCanvas.value?.fitPatternInViewport()
  showWeChatGuideWhenReady()
  setupHeartbeatChannel()
  void refreshLicenseState()
  startSwitchPolling()
  void connectSwitchEvents()
  // 只监听画布内容（豆子）变化时才自动保存；拖拽平移、缩放等视图操作不触发保存。
  stopAutoSave = watch(
    contentRevision,
    () => { scheduleAutoSave(); scheduleCloudSave() },
    { flush: 'post' },
  )
  // 任意弹窗打开时锁定背景滚动，关闭时恢复。
  document.addEventListener('toggle', onDialogToggle, true)
  document.addEventListener('fullscreenchange', syncFullscreenState)
  document.addEventListener('pointerdown', closeToolsOnOutsidePointer)
  document.addEventListener('pointerdown', closeCollabPopupsOnOutside)
  document.addEventListener('pointerdown', selectPanOutsideEditingArea)
  document.addEventListener('pointerdown', guardAccessOnPointerDown, true)
  document.addEventListener('visibilitychange', onVisibilityChange)
  window.addEventListener('resize', syncLandscapeFallback)
  window.addEventListener('resize', syncResponsiveLayout)
  window.addEventListener('orientationchange', syncLandscapeFallback)
  window.visualViewport?.addEventListener('resize', syncLandscapeFallback)
  screen.orientation?.addEventListener?.('change', syncLandscapeFallback)
  // 刷新/关闭页面时主动关闭 SSE 长连接，避免浏览器强制中断产生 net::ERR_ABORTED 报错。
  window.addEventListener('pagehide', closeStreamingOnPageHide)
  window.addEventListener('pageshow', resumeStreamingOnPageShow)
  // 刷新页面后恢复联机状态：读取 sessionStorage 中的联机会话，重建阶段并重连 SSE。
  // 同步部分会立即恢复 phase，故放在邀请链接处理之前，避免两者冲突。
  void collab.restoreSession()
  // 从好友分享的邀请链接进入时，自动读取邀请码并发起联机申请。
  handleCollabInviteFromUrl()
  // 联机倒计时基准：每秒刷新（房主申请列表 / 成员申请等待区的倒计时显示）。
  collabTickTimer = window.setInterval(() => { collabNow.value = Date.now() }, 1000)
})

onBeforeUnmount(() => {
  streamingActive = false
  abortActiveApiRequests()
  collab.suspendConnection()
  if (collabTickTimer) { window.clearInterval(collabTickTimer); collabTickTimer = 0 }
  releaseHeartbeatLeader()
  if (heartbeatLeaderTimer) { window.clearTimeout(heartbeatLeaderTimer); heartbeatLeaderTimer = 0 }
  heartbeatChannel?.close()
  stopSwitchPolling()
  disconnectSwitchEvents()
  stopTrialCountdown()
  stopTrialHeartbeat()
  stopLicenseCountdown()
  closeKickEvents()
  stopLicenseHeartbeat()
  if (cloudSaveTimer) {
    window.clearTimeout(cloudSaveTimer)
    cloudSaveTimer = 0
  }
  if (toastTimer) window.clearTimeout(toastTimer)
  if (autoSaveTimer) {
    window.clearTimeout(autoSaveTimer)
    try {
      store.saveLocal()
    } catch {
      // 页面即将关闭时无法继续交互，保留已导出的文件和上一次成功保存的草稿。
    }
  }
  stopAutoSave?.()
  if (previewUrl.value) URL.revokeObjectURL(previewUrl.value)
  releaseCropSource()
  if (weChatPreviewUrl.value) URL.revokeObjectURL(weChatPreviewUrl.value)
  document.documentElement.classList.remove('fullscreen-fallback-active')
  document.removeEventListener('toggle', onDialogToggle, true)
  dialogOpenCount = 0
  document.body.style.overflow = ''
  document.removeEventListener('fullscreenchange', syncFullscreenState)
  document.removeEventListener('pointerdown', closeToolsOnOutsidePointer)
  document.removeEventListener('pointerdown', closeCollabPopupsOnOutside)
  document.removeEventListener('pointerdown', selectPanOutsideEditingArea)
  document.removeEventListener('visibilitychange', onVisibilityChange)
  window.removeEventListener('pagehide', closeStreamingOnPageHide)
  window.removeEventListener('pageshow', resumeStreamingOnPageShow)
  document.removeEventListener('pointerdown', guardAccessOnPointerDown, true)
  window.removeEventListener('resize', syncLandscapeFallback)
  window.removeEventListener('resize', syncResponsiveLayout)
  window.removeEventListener('orientationchange', syncLandscapeFallback)
  window.visualViewport?.removeEventListener('resize', syncLandscapeFallback)
  screen.orientation?.removeEventListener?.('change', syncLandscapeFallback)
})

async function toggleFullscreen(): Promise<void> {
  try {
    if (isFullscreen.value) {
      if (document.fullscreenElement === centerStage.value) await document.exitFullscreen()
      else exitFullscreenFallback()
      return
    }
    toolsCollapsed.value = true
    const stage = centerStage.value
    if (!stage) return
    if (stage.requestFullscreen) {
      try {
        await stage.requestFullscreen()
      } catch (reason) {
        if (!isMobileDevice()) throw reason
        enterFullscreenFallback()
      }
    } else if (isMobileDevice()) {
      enterFullscreenFallback()
    } else {
      throw new Error('当前浏览器不支持全屏模式')
    }
    // 某些浏览器的 fullscreenchange 会延迟，先同步一次状态再判断是否需要旋转。
    syncFullscreenState()
    await lockLandscape()
  } catch {
    notify('当前浏览器无法进入全屏模式')
  }
}


function formatDuration(totalSeconds: number): string {
  const total = Math.max(0, Math.floor(totalSeconds))
  const days = Math.floor(total / 86400)
  const hours = Math.floor((total % 86400) / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  const rest = total % 60
  const parts: string[] = []
  if (days > 0) parts.push(`${days}天`)
  if (hours > 0) parts.push(`${hours}小时`)
  if (minutes > 0) parts.push(`${minutes}分`)
  parts.push(`${rest}秒`)
  return parts.join('')
}

// ---------- 图纸库（保存 / 我的图纸 / 拼接豆板） ----------
const LOCAL_LIBRARY_KEY = 'pindou-studio-saves'

function readLocalLibrary(): SavedProject[] {
  try {
    const raw = localStorage.getItem(LOCAL_LIBRARY_KEY)
    const list = raw ? JSON.parse(raw) : []
    return Array.isArray(list) ? list as SavedProject[] : []
  } catch {
    return []
  }
}

function writeLocalLibrary(list: SavedProject[]): void {
  try {
    localStorage.setItem(LOCAL_LIBRARY_KEY, JSON.stringify(list))
  } catch {
    throw new Error('本地存储空间不足，无法保存更多图纸。')
  }
}

// 命名图纸数量（自动同步的「正在编辑」不占编号）。
function namedSavesCount(items: SavedProject[]): number {
  return items.filter(item => item.id !== 'current').length
}

async function openSaveDialog(): Promise<void> {
  if (!hasPattern.value || beadCount.value <= 0) { notify('豆板还没有豆子，无法保存'); return }
  try {
    const items = licensedKey.value ? await getSaveLibrary(licensedKey.value) ?? [] : readLocalLibrary()
    saveName.value = `图纸${namedSavesCount(items) + 1}`
  } catch {
    saveName.value = `图纸${readLocalLibrary().length + 1}`
  }
  saveDialog.value?.showModal()
}

async function confirmSave(): Promise<void> {
  if (!hasPattern.value || beadCount.value <= 0) { notify('豆板还没有豆子，无法保存'); return }
  const name = saveName.value.trim() || `图纸${Date.now().toString().slice(-4)}`
  const item: SavedProject = {
    id: `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 8)}`,
    name,
    savedAt: new Date().toISOString(),
    project: store.createProjectSnapshot(),
  }
  try {
    if (licensedKey.value) {
      const items = await getSaveLibrary(licensedKey.value) ?? []
      items.push(item)
      await saveSaveLibrary(licensedKey.value, items)
      notify('图纸已保存到云端')
    } else {
      const items = readLocalLibrary()
      items.push(item)
      writeLocalLibrary(items)
      notify('图纸已保存到本地')
    }
    saveDialog.value?.close()
    toolsCollapsed.value = true
  } catch (reason) {
    if (reason instanceof ApiError && reason.code === 'DEVICE_CONFLICT') { handleDeviceConflict(); return }
    notify(reason instanceof Error ? `保存失败：${reason.message}` : '保存失败，请稍后重试')
  }
}

function openBoardExpandDialog(): void {
  if (!hasPattern.value) { notify('豆板还没有图纸，请先生成或绘制'); return }
  expandWidth.value = width.value
  expandHeight.value = height.value
  boardExpandDialog.value?.showModal()
}

function confirmBoardExpand(): void {
  const nextWidth = Math.min(160, Math.max(width.value, Math.round(expandWidth.value) || width.value))
  const nextHeight = Math.min(160, Math.max(height.value, Math.round(expandHeight.value) || height.value))
  if (nextWidth === width.value && nextHeight === height.value) {
    boardExpandDialog.value?.close()
    notify('豆板尺寸未变化')
    return
  }
  store.expandBoard(nextWidth, nextHeight)
  toolsCollapsed.value = true
  notify(`豆板已拼接为 ${nextWidth}×${nextHeight}`)
  boardExpandDialog.value?.close()
}

async function openLibraryDialog(): Promise<void> {
  libraryLoading.value = true
  selectedLibraryIds.value = []
  try {
    const items = licensedKey.value
      ? await getSaveLibrary(licensedKey.value) ?? []
      : readLocalLibrary()
    // 自动同步的「正在编辑」条目不展示在列表，避免误操作与重复。
    libraryList.value = items.filter(item => item.id !== 'current')
  } catch (reason) {
    if (reason instanceof ApiError && reason.code === 'DEVICE_CONFLICT') { handleDeviceConflict(); libraryList.value = []; return }
    notify(reason instanceof Error ? `读取图纸失败：${reason.message}` : '读取图纸失败')
    libraryList.value = []
  } finally {
    libraryLoading.value = false
  }
  libraryDialog.value?.showModal()
}

function formatSavedAt(iso: string): string {
  if (!iso) return ''
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`
}

function requestOpenSavedProject(item: SavedProject): void {
  if (beadCount.value > 0) {
    pendingOpenProject.value = item
    confirmOpenDialog.value?.showModal()
    return
  }
  void applySavedProject(item)
}

function confirmOpenSavedProject(): void {
  const item = pendingOpenProject.value
  pendingOpenProject.value = null
  confirmOpenDialog.value?.close()
  if (item) void applySavedProject(item)
}

async function applySavedProject(item: SavedProject): Promise<void> {
  try {
    await store.loadSavedProject(item.project)
    interactionMode.value = 'pan'
    await nextTick()
    await patternCanvas.value?.fitPatternInViewport()
    libraryDialog.value?.close()
    toolsCollapsed.value = true
    // 联机数据同步：
    // - 房主：打开我的图纸（整块替换画布）后同步给成员。
    // - 成员：打开自家图纸需申请替换整张联机图纸，经房主同意后全房间同步。
    if (collabActive.value && collabIsHost.value) {
      notify(`已打开「${item.name}」`)
      void collab.resyncSnapshot()
    } else if (collabActive.value && collabIsMember.value) {
      requestCanvasReplace(captureCollabSnapshot())
    } else {
      notify(`已打开「${item.name}」`)
    }
  } catch (reason) {
    notify(reason instanceof Error ? `打开图纸失败：${reason.message}` : '打开图纸失败')
  }
}

// 打开导入弹窗：重置上一轮选择与校验结果。
function openImportDialog(): void {
  pendingImportProject.value = null
  importFileName.value = ''
  importFileError.value = ''
  importDragging.value = false
  if (importFileInput.value) importFileInput.value.value = ''
  importDialog.value?.showModal()
}

// 选择文件后读取并做 JSON 结构预检，校验通过才允许确认导入。
async function onImportFileChange(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (input.value) input.value = ''
  await validateImportFile(file)
}

// 拖拽上传：与点击选择走同一套校验流程。
async function onImportDrop(event: DragEvent): Promise<void> {
  const file = event.dataTransfer?.files?.[0]
  await validateImportFile(file)
}

// 读取并校验导入文件：JSON 结构预检通过后才允许确认导入。
async function validateImportFile(file: File | undefined): Promise<void> {
  if (!file) {
    pendingImportProject.value = null
    importFileName.value = ''
    importFileError.value = ''
    return
  }
  try {
    const text = await file.text()
    let parsed: unknown
    try {
      parsed = JSON.parse(text)
    } catch {
      pendingImportProject.value = null
      importFileName.value = ''
      importFileError.value = '文件已损坏，不是有效的 JSON 格式。'
      return
    }
    const problem = store.validateProject(parsed)
    if (problem) {
      pendingImportProject.value = null
      importFileName.value = ''
      importFileError.value = '文件已损坏或数据缺失。'
      return
    }
    pendingImportProject.value = parsed as PortableProject
    importFileName.value = file.name
    importFileError.value = ''
    notify('文件校验通过，可确认导入')
  } catch {
    pendingImportProject.value = null
    importFileName.value = ''
    importFileError.value = '文件读取失败，可能已损坏。'
  }
}

// 请求导入：当前豆板已有豆子时二次确认覆盖；无豆子则直接恢复文件数据。
function requestImportProject(): void {
  if (!pendingImportProject.value) return
  if (beadCount.value > 0) {
    confirmImportDialog.value?.showModal()
    return
  }
  void applyImportedProject()
}

function confirmImportProject(): void {
  confirmImportDialog.value?.close()
  void applyImportedProject()
}

async function applyImportedProject(): Promise<void> {
  const project = pendingImportProject.value
  if (!project) return
  importBusy.value = true
  try {
    await store.loadSavedProject(project)
    interactionMode.value = 'pan'
    await nextTick()
    await patternCanvas.value?.fitPatternInViewport()
    importDialog.value?.close()
    toolsCollapsed.value = true
    notify('导入成功')
    // 联机数据同步：房主导入图纸（整块替换画布）后同步给成员。
    if (collabActive.value && collabIsHost.value) void collab.resyncSnapshot()
  } catch (reason) {
    notify(reason instanceof Error ? `导入失败：${reason.message}` : '导入失败')
  } finally {
    importBusy.value = false
    pendingImportProject.value = null
  }
}

function toggleLibrarySelect(id: string): void {
  const index = selectedLibraryIds.value.indexOf(id)
  if (index >= 0) selectedLibraryIds.value.splice(index, 1)
  else selectedLibraryIds.value.push(id)
}

function requestDeleteSavedProject(item: SavedProject): void {
  pendingDeleteItems.value = [item]
  confirmDeleteDialog.value?.showModal()
}

function requestDeleteSavedProjects(items: SavedProject[]): void {
  if (!items.length) return
  pendingDeleteItems.value = items
  confirmDeleteDialog.value?.showModal()
}

function requestDeleteSelected(): void {
  const items = libraryList.value.filter(item => selectedLibraryIds.value.includes(item.id))
  requestDeleteSavedProjects(items)
}

async function confirmDeleteSavedProjects(): Promise<void> {
  const items = pendingDeleteItems.value
  pendingDeleteItems.value = []
  confirmDeleteDialog.value?.close()
  if (!items.length) return
  const ids = new Set(items.map(item => item.id))
  try {
    if (licensedKey.value) {
      // 云端删除时保留自动同步的「正在编辑」条目。
      const lib = await getSaveLibrary(licensedKey.value) ?? []
      await saveSaveLibrary(licensedKey.value, lib.filter(item => item.id === 'current' || !ids.has(item.id)))
    } else {
      writeLocalLibrary(readLocalLibrary().filter(item => !ids.has(item.id)))
    }
    libraryList.value = libraryList.value.filter(item => !ids.has(item.id))
    selectedLibraryIds.value = selectedLibraryIds.value.filter(id => !ids.has(id))
    notify(`已删除 ${items.length} 份图纸`)
  } catch (reason) {
    if (reason instanceof ApiError && reason.code === 'DEVICE_CONFLICT') { handleDeviceConflict(); return }
    notify(reason instanceof Error ? `删除失败：${reason.message}` : '删除失败，请稍后重试')
  }
}

// 打开图纸重命名对话框：预填当前名称。
function requestRenameSavedProject(item: SavedProject): void {
  renameTarget.value = item
  renameInput.value = item.name
  renameDialog.value?.showModal()
}

// 确认重命名：校验名称非空且不与其他图纸重名（排除自身），本地/云端同步更新。
async function confirmRenameSavedProject(): Promise<void> {
  const target = renameTarget.value
  if (!target) return
  const name = renameInput.value.trim()
  if (!name) { notify('图纸名称不能为空'); return }
  if (name === target.name) { renameDialog.value?.close(); return }
  try {
    const items = licensedKey.value ? await getSaveLibrary(licensedKey.value) ?? [] : readLocalLibrary()
    const duplicate = items.find(item => item.id !== target.id && item.name === name)
    if (duplicate) { notify('已有同名图纸，请更换名称'); return }
    const updated = items.map(item => item.id === target.id ? { ...item, name } : item)
    if (licensedKey.value) await saveSaveLibrary(licensedKey.value, updated)
    else writeLocalLibrary(updated)
    libraryList.value = libraryList.value.map(item => item.id === target.id ? { ...item, name } : item)
    renameDialog.value?.close()
    notify('图纸已重命名')
  } catch (reason) {
    if (reason instanceof ApiError && reason.code === 'DEVICE_CONFLICT') { handleDeviceConflict(); return }
    notify(reason instanceof Error ? `重命名失败：${reason.message}` : '重命名失败，请稍后重试')
  }
}

async function restoreCloudSave(): Promise<boolean> {
  try {
    const items = await getSaveLibrary(licensedKey.value)
    if (!items || items.length === 0) {
      notify('云端没有可恢复的豆板存档')
      return false
    }
    const current = items.find(item => item.id === 'current') || items[0]
    await store.restoreSharedProject(current.project)
    interactionMode.value = 'pan'
    await nextTick()
    await patternCanvas.value?.fitPatternInViewport()
    notify('已恢复云端豆板存档')
    // 联机数据同步：房主恢复云端存档（整块替换画布）后同步给成员。
    if (collabActive.value && collabIsHost.value) void collab.resyncSnapshot()
    return true
  } catch (reason) {
    // 会话被其他设备替换后，操作立即被拒绝并提示下线。
    if (reason instanceof ApiError && reason.code === 'DEVICE_CONFLICT') {
      handleDeviceConflict()
      return false
    }
    notify(reason instanceof Error ? `云端存档恢复失败：${reason.message}` : '云端存档恢复失败，请稍后重试')
    return false
  }
}

// 画板数据变化后防抖写入云端；状态单独显示，避免把本地保存误报成云端已保存。
function scheduleCloudSave(): void {
  if (!hasPattern.value || beadCount.value <= 0) return
  // 联机成员不写云端：共享画布仅由房主端保存。
  if (collabIsMember.value) return
  if (!licensedKey.value) {
    // 未登录时底部状态由本地实时保存流程更新（实时保存中 / 已保存到本地）。
    return
  }
  cloudSaveState.value = 'saving'
  cloudSaveStatus.value = '实时保存中'
  if (cloudSaveTimer) window.clearTimeout(cloudSaveTimer)
  cloudSaveTimer = window.setTimeout(() => {
    cloudSaveTimer = 0
    void persistCloudSave()
  }, 1500)
}

async function persistCloudSave(): Promise<void> {
  if (cloudSaveInFlight) {
    cloudSavePending = true
    return
  }
  cloudSaveInFlight = true
  cloudSavePending = false
  cloudSaveState.value = 'saving'
  cloudSaveStatus.value = '实时保存中'
  try {
    // 云端图纸库：把当前画布 upsert 为「正在编辑」条目，其余命名图纸保留。
    const items = await getSaveLibrary(licensedKey.value) ?? []
    const snapshot = store.createProjectSnapshot()
    const current = items.find(item => item.id === 'current')
    if (current) {
      current.name = snapshot.title || '正在编辑'
      current.savedAt = new Date().toISOString()
      current.project = snapshot
    } else {
      items.unshift({ id: 'current', name: snapshot.title || '正在编辑', savedAt: new Date().toISOString(), project: snapshot })
    }
    await saveSaveLibrary(licensedKey.value, items)
    cloudSaveState.value = 'saved'
    cloudSaveStatus.value = '已保存到云端'
  } catch (reason) {
    // 会话被其他设备替换后，操作立即被拒绝并提示下线。
    if (reason instanceof ApiError && reason.code === 'DEVICE_CONFLICT') {
      handleDeviceConflict()
      return
    }
    cloudSaveState.value = 'error'
    cloudSaveStatus.value = '云端保存失败'
    notify(reason instanceof Error ? `云端保存失败：${reason.message}` : '云端保存失败，请稍后重试')
  } finally {
    cloudSaveInFlight = false
    if (cloudSavePending && streamingActive && licensedKey.value && hasPattern.value) {
      cloudSavePending = false
      scheduleCloudSave()
    }
  }
}

// 密钥会话与心跳：开始会话重置计时起点；正常每 5 秒发送一次，连续失败 3 次后降频到 15 秒。
// 多标签页下仅「主标签」实际发送心跳，避免多个标签重复请求触发全局限流。
function startLicenseHeartbeat(key: string): void {
  const identity = `${key}:${getSessionToken()}`
  if ((heartbeatTimer || licenseHeartbeatInFlight) && activeHeartbeatIdentity === identity) return
  stopLicenseHeartbeat()
  const generation = licenseHeartbeatGeneration
  activeHeartbeatIdentity = identity
  licenseHeartbeatFailures = 0
  acquireHeartbeatLeader()
  void startLicenseSession(key, getSessionToken())
    .then(status => { if (generation === licenseHeartbeatGeneration) handleSessionStatus(status) })
    .catch(() => undefined)
  scheduleLicenseHeartbeat(key, generation)
}

function scheduleLicenseHeartbeat(key: string, generation: number): void {
  if (generation !== licenseHeartbeatGeneration || !activeHeartbeatIdentity) return
  const delay = licenseHeartbeatFailures >= HEARTBEAT_FAILURE_THRESHOLD
    ? HEARTBEAT_DEGRADED_DELAY
    : HEARTBEAT_NORMAL_DELAY
  heartbeatTimer = window.setTimeout(() => {
    heartbeatTimer = 0
    if (generation !== licenseHeartbeatGeneration) return
    if (!isHeartbeatLeader() || licenseHeartbeatInFlight) {
      scheduleLicenseHeartbeat(key, generation)
      return
    }
    licenseHeartbeatInFlight = true
    void heartbeatLicense(key, getSessionToken())
      .then(status => {
        if (generation !== licenseHeartbeatGeneration) return
        licenseHeartbeatFailures = 0
        handleSessionStatus(status)
      })
      .catch(() => {
        if (generation === licenseHeartbeatGeneration) licenseHeartbeatFailures++
      })
      .finally(() => {
        if (generation !== licenseHeartbeatGeneration) return
        licenseHeartbeatInFlight = false
        scheduleLicenseHeartbeat(key, generation)
      })
  }, delay)
}

// 会话/心跳返回处理：账号在其他设备登录时立即下线本设备。
function handleSessionStatus(status: LicenseStatus): void {
  if (status.status === 'device_conflict') {
    handleDeviceConflict()
    return
  }
  // 密钥被停用（revoked）或删除（invalid）时，已登录设备立即失效并引导获取新密钥。
  if (status.status === 'revoked' || status.status === 'invalid') {
    handleLicenseRevoked()
    return
  }
  if (status.remainingSeconds != null) licenseRemainingSeconds.value = status.remainingSeconds
  licenseInfo.value = status
}

// 多标签页单点：当前标签页有密钥但无会话令牌（新标签页/会话已清），主动登录建立新会话，
// 服务端会踢掉旧标签页的会话，保证同一密钥同一时间只有一个在线标签页。
async function ensureFreshSession(): Promise<void> {
  const key = getActiveLicenseKey()
  if (!key || getSessionToken()) return
  try {
    const result = await loginLicense(key, '')
    if (result.status === 'active' || result.status === 'time_expired') {
      setSessionToken(result.sessionToken)
      loginFailCount.value = 0
      sessionStorage.removeItem(SESSION_KICKED_KEY)
    }
  } catch {
    // 网络异常时保持现状，后续心跳/刷新重试。
  }
}

// 立即下线：停止心跳与倒计时，标记本标签页已被踢并显示明确提示。
// 注意：不清除 localStorage 密钥（同浏览器其他标签页可能仍在用），仅清本标签页会话。
function handleDeviceConflict(): void {
  closeKickEvents()
  stopLicenseHeartbeat()
  stopLicenseCountdown()
  licenseRemainingSeconds.value = 0
  sessionStorage.setItem(SESSION_KICKED_KEY, '1')
  setSessionToken(null)
  licensedKey.value = ''
  licenseInfo.value = null
  notify('该账号已在其他设备登录，本设备已下线')
  void refreshLicenseState()
}

// 密钥被管理员吊销：立即失效本设备密钥并引导获取新密钥。
function handleLicenseRevoked(): void {
  closeKickEvents()
  stopLicenseHeartbeat()
  stopLicenseCountdown()
  licenseRemainingSeconds.value = 0
  clearActiveLicenseKey()
  setSessionToken(null)
  licensedKey.value = ''
  licenseInfo.value = null
  notify('密钥已失效，请获取新密钥')
  openLicenseDialog()
  void refreshLicenseState()
}

// 建立「被踢下线」事件连接；登录成功后调用，登出/下线/卸载时关闭。
async function connectKickEvents(): Promise<void> {
  closeKickEvents()
  if (!streamingActive || !licensedKey.value || !getSessionToken() || kickConnecting) return
  kickConnecting = true
  try {
    kickSource = await createKickEventSource(licensedKey.value, getSessionToken())
  } catch {
    kickSource = null
    scheduleKickReconnect()
    return
  } finally {
    kickConnecting = false
  }
  kickSource.addEventListener('session-kicked', () => handleDeviceConflict())
  // 断线后用新票据重建连接，避免 EventSource 对已失效的一次性票据自动重连产生 net::ERR_ABORTED。
  kickSource.onerror = () => {
    kickSource?.close()
    kickSource = null
    scheduleKickReconnect()
  }
}

function scheduleKickReconnect(): void {
  if (!streamingActive || kickReconnectTimer || !licensedKey.value || !getSessionToken()) return
  kickReconnectTimer = window.setTimeout(() => {
    kickReconnectTimer = 0
    if (streamingActive && !kickSource && licensedKey.value && getSessionToken()) void connectKickEvents()
  }, 3000)
}

function closeKickEvents(): void {
  if (kickReconnectTimer) {
    window.clearTimeout(kickReconnectTimer)
    kickReconnectTimer = 0
  }
  if (kickSource) {
    kickSource.onerror = null
    kickSource.close()
    kickSource = null
  }
}

function stopLicenseHeartbeat(): void {
  licenseHeartbeatGeneration++
  if (heartbeatTimer) {
    window.clearTimeout(heartbeatTimer)
    heartbeatTimer = 0
  }
  licenseHeartbeatInFlight = false
  licenseHeartbeatFailures = 0
  activeHeartbeatIdentity = ''
}

// 页面刷新/关闭（pagehide）时主动关闭 SSE 与轮询，浏览器不再强制中断长连接，避免控制台 net::ERR_ABORTED。
function closeStreamingOnPageHide(): void {
  streamingActive = false
  abortActiveApiRequests()
  licenseRefreshQueued = false
  closeKickEvents()
  disconnectSwitchEvents()
  collab.suspendConnection()
  stopSwitchPolling()
  stopLicenseHeartbeat()
  stopTrialHeartbeat()
  releaseHeartbeatLeader()
}

// BFCache 恢复不会重新执行 onMounted；主动恢复被 pagehide 关闭的轮询、SSE 与心跳。
function resumeStreamingOnPageShow(event: PageTransitionEvent): void {
  if (!event.persisted || streamingActive) return
  streamingActive = true
  void refreshLicenseState()
  startSwitchPolling()
  void connectSwitchEvents()
  collab.resumeConnection()
}

// 主标签心跳选举：广播「我是主标签」，其余标签放弃发送；主标签离开/后台时让出，由存活的标签接管。
function acquireHeartbeatLeader(): void {
  if (!heartbeatChannel) {
    heartbeatLeader = claimHeartbeatLease()
    return
  }
  heartbeatChannel.postMessage({ type: heartbeatLeaderPing, id: heartbeatTabId })
  // 若 150ms 内没有其它标签回应「已是主」，则本标签成为主标签。
  if (heartbeatLeaderTimer) window.clearTimeout(heartbeatLeaderTimer)
  heartbeatLeaderTimer = window.setTimeout(() => {
    heartbeatLeader = true
    heartbeatLeaderTimer = 0
    heartbeatChannel?.postMessage({ type: 'pindou-heartbeat-leader-claim', id: heartbeatTabId })
  }, 150)
}

function releaseHeartbeatLeader(): void {
  if (heartbeatLeader) {
    heartbeatLeader = false
    try {
      const lease = JSON.parse(localStorage.getItem(HEARTBEAT_LEASE_KEY) || 'null') as { id?: string } | null
      if (lease?.id === heartbeatTabId) localStorage.removeItem(HEARTBEAT_LEASE_KEY)
    } catch { /* 存储不可用时等待租约自然过期。 */ }
    heartbeatChannel?.postMessage({ type: 'pindou-heartbeat-leader-release', id: heartbeatTabId })
  }
}

function setupHeartbeatChannel(): void {
  if (!heartbeatChannel) return
  heartbeatChannel.onmessage = (event: MessageEvent) => {
    if (event.data?.type === heartbeatLeaderPing) {
      // 已有主标签在运行，回应「占用」，让新标签不做心跳。
      if (heartbeatLeader) heartbeatChannel.postMessage({ type: 'pindou-heartbeat-leader-occupied', id: heartbeatTabId })
    } else if (event.data?.type === 'pindou-heartbeat-leader-occupied') {
      heartbeatLeader = false
      if (heartbeatLeaderTimer) { window.clearTimeout(heartbeatLeaderTimer); heartbeatLeaderTimer = 0 }
    } else if (event.data?.type === 'pindou-heartbeat-leader-claim') {
      // 多个标签同时打开时可能都在 150ms 后自荐；使用稳定 tabId 决胜，确保最终只保留一个主标签。
      const claimantId = String(event.data?.id || '')
      if (claimantId && claimantId < heartbeatTabId) {
        heartbeatLeader = false
        if (heartbeatLeaderTimer) { window.clearTimeout(heartbeatLeaderTimer); heartbeatLeaderTimer = 0 }
      } else if (heartbeatLeader && claimantId && claimantId > heartbeatTabId) {
        heartbeatChannel.postMessage({ type: 'pindou-heartbeat-leader-claim', id: heartbeatTabId })
      }
    } else if (event.data?.type === 'pindou-heartbeat-leader-release') {
      // 主标签离开，重新选举。
      acquireHeartbeatLeader()
    }
  }
}

function isHeartbeatLeader(): boolean {
  return heartbeatChannel ? heartbeatLeader : claimHeartbeatLease()
}

// BroadcastChannel 不可用时以短租约兜底；每次真正发送心跳前续租，异常关闭最多 8 秒自动释放。
function claimHeartbeatLease(): boolean {
  const now = Date.now()
  try {
    const lease = JSON.parse(localStorage.getItem(HEARTBEAT_LEASE_KEY) || 'null') as { id?: string; expiresAt?: number } | null
    if (lease?.id && lease.id !== heartbeatTabId && Number(lease.expiresAt) > now) return false
    localStorage.setItem(HEARTBEAT_LEASE_KEY, JSON.stringify({ id: heartbeatTabId, expiresAt: now + 8000 }))
    const confirmed = JSON.parse(localStorage.getItem(HEARTBEAT_LEASE_KEY) || 'null') as { id?: string } | null
    return confirmed?.id === heartbeatTabId
  } catch {
    // 隐私模式完全禁用存储时无法跨标签协调，只能保证当前标签内部不并发。
    return true
  }
}

// 页面切后台/锁屏（hidden）时暂停「试用」计时（关闭/后台/锁屏/断网期间试用不消耗）；
// 密钥在线时长不受影响（按在线心跳累计，断网/关闭由后端 60 秒间隔兜底）。
function onVisibilityChange(): void {
  if (document.hidden) {
    // 试用只计算前台活跃时间；隐藏标签停止请求并让出主标签，避免后台页面占用连接。
    if (!licensedKey.value) {
      stopTrialHeartbeat()
      stopTrialCountdown()
      releaseHeartbeatLeader()
    }
    return
  }
  if (!licensingDisabled.value && !licensedKey.value && !trialExpired.value) {
    acquireHeartbeatLeader()
    startTrialHeartbeat()
    startTrialCountdown()
  }
}

// 弹窗（dialog）打开时锁定背景滚动，关闭时恢复；支持多个弹窗叠加。
let dialogOpenCount = 0
function syncBodyScrollLock(): void {
  document.body.style.overflow = dialogOpenCount > 0 ? 'hidden' : ''
}
function onDialogToggle(event: Event): void {
  const target = event.target
  if (!(target instanceof HTMLDialogElement)) return
  dialogOpenCount = Math.max(0, dialogOpenCount + (target.open ? 1 : -1))
  syncBodyScrollLock()
}

// 剩余时长本地每秒递减，配合心跳校准实现实时倒计时显示。
function startLicenseCountdown(): void {
  stopLicenseCountdown()
  if (licenseRemainingSeconds.value <= 0) return
  licenseCountdownTimer = window.setInterval(() => {
    if (licenseRemainingSeconds.value > 0) {
      licenseRemainingSeconds.value--
    } else {
      stopLicenseCountdown()
    }
  }, 1000)
}

function stopLicenseCountdown(): void {
  if (licenseCountdownTimer) {
    window.clearInterval(licenseCountdownTimer)
    licenseCountdownTimer = 0
  }
}

// 取色器选择颜色：期限到期锁定编辑时不切换到绘制模式；选中未使用候选色号时先追加为新色号。
function handleColorPick(index: number): void {
  if (editingLocked.value) {
    notify('使用期限已到，豆板编辑已锁定')
    return
  }
  if (index >= colors.value.length) {
    const pick = palette.value?.colors[index - colors.value.length]
    if (pick) index = store.addColor(pick)
  }
  selectedColorIndex.value = index
  interactionMode.value = 'paint'
}

// 快捷色栏只负责快速切换画笔颜色，不提升排序，避免点击后按钮突然换位。
function handleQuickPalettePick(index: number): void {
  if (editingLocked.value) {
    notify('使用期限已到，豆板编辑已锁定')
    return
  }
  selectedColorIndex.value = index
  interactionMode.value = 'paint'
}

function openLicenseDialog(): void {
  // 授权开关关闭（免授权）时不打开密钥/试用弹窗。
  if (licensingDisabled.value) return
  // 已打开时不再重复 showModal，避免多次点击屏幕触发 InvalidStateError。
  if (!licenseDialog.value?.open) licenseDialog.value?.showModal()
}

// 合并首屏、SSE 与轮询的并发刷新请求：同一时刻只访问一次后端，期间新增触发最多补刷一次。
function refreshLicenseState(): Promise<void> {
  if (licenseRefreshPromise) {
    licenseRefreshQueued = true
    return licenseRefreshPromise
  }
  licenseRefreshPromise = runRefreshLicenseState().finally(() => {
    licenseRefreshPromise = null
    if (licenseRefreshQueued && streamingActive) {
      licenseRefreshQueued = false
      void refreshLicenseState()
    }
  })
  return licenseRefreshPromise
}

// 页面加载时查询授权/试用状态：有密钥则展示有效期与剩余次数，无密钥则启动试用倒计时。
async function runRefreshLicenseState(): Promise<void> {
  try {
    const info = await getTrialStatus()
    // 后端配置关闭授权/试用：隐藏入口、停止所有心跳/倒计时并宽松放行。
    if (info.licensingDisabled) {
      licensingDisabled.value = true
      licensedKey.value = ''
      licenseInfo.value = null
      closeKickEvents()
      stopLicenseHeartbeat()
      stopLicenseCountdown()
      stopTrialHeartbeat()
      stopTrialCountdown()
      // 授权关闭后关闭已打开的密钥/试用弹窗。
      licenseDialog.value?.close()
      trialResolved.value = true
      return
    }
    licensingDisabled.value = false
    if (info.licensed && info.license) {
      if (info.license.status === 'revoked' || info.license.status === 'invalid') {
        handleLicenseRevoked()
        return
      }
      // 本标签页曾被踢下线（被其他标签页/设备接管）：保持下线，不自动重登，避免循环踢。
      if (sessionStorage.getItem(SESSION_KICKED_KEY) === '1') {
        licensedKey.value = ''
        licenseInfo.value = null
        closeKickEvents()
        stopLicenseHeartbeat()
        stopLicenseCountdown()
        licenseRemainingSeconds.value = 0
        return
      }
      licensedKey.value = getActiveLicenseKey()
      licenseInfo.value = info.license
      if (info.license.remainingSeconds != null) licenseRemainingSeconds.value = info.license.remainingSeconds
      stopTrialCountdown()
      trialExpired.value = false
      trialResolved.value = true
      // 多标签页单点：新标签页有密钥但无本地会话令牌时，主动登录建立新会话（服务端踢旧标签页）。
      if (!getSessionToken()) {
        await ensureFreshSession()
      }
      startLicenseHeartbeat(getActiveLicenseKey())
      void connectKickEvents()
      return
    }
    licensedKey.value = ''
    licenseInfo.value = null
    closeKickEvents()
    stopLicenseHeartbeat()
    stopLicenseCountdown()
    licenseRemainingSeconds.value = 0
    if (info.trial) {
      trialRemainingSeconds.value = info.trial.remainingSeconds
      trialRemainingGenerations.value = info.trial.remainingGenerations
      trialTotalMinutes.value = info.trial.totalMinutes
      trialExpired.value = info.trial.expired
      trialResolved.value = true
      startTrialCountdown()
      startTrialHeartbeat()
    }
  } catch {
    // 授权接口不可用时保持宽松，不误拦截正常试用。
    trialResolved.value = false
  }
}

// 周期轮询后端开关：仅当授权/试用启用状态发生变化时才重新同步整个授权 UI（按钮显隐、心跳启停等）。
function startSwitchPolling(): void {
  stopSwitchPolling()
  if (!streamingActive) return
  const interval = switchEventSource ? switchPollInterval : switchPollFallbackInterval
  switchPollTimer = window.setInterval(async () => {
    if (switchPollInFlight) return
    switchPollInFlight = true
    try {
      const info = await getTrialStatus()
      const nowDisabled = Boolean(info.licensingDisabled)
      if (nowDisabled !== licensingDisabled.value) {
        await refreshLicenseState()
      }
    } catch {
      // 网络异常时保持当前状态，下一轮再试。
    } finally {
      switchPollInFlight = false
    }
  }, interval)
}

function stopSwitchPolling(): void {
  if (switchPollTimer) {
    window.clearInterval(switchPollTimer)
    switchPollTimer = 0
  }
}

// 订阅开关状态 SSE：收到 switch-changed 立即同步授权/试用 UI；连接断开时按指数退避重连。
async function connectSwitchEvents(): Promise<void> {
  if (!streamingActive || switchEventSource || switchConnecting) return
  if (switchReconnectTimer) {
    window.clearTimeout(switchReconnectTimer)
    switchReconnectTimer = 0
  }
  const connectionVersion = ++switchConnectionVersion
  switchConnecting = true
  try {
    const source = await createSwitchEventSource()
    if (!streamingActive || connectionVersion !== switchConnectionVersion) {
      source.close()
      return
    }
    source.addEventListener('switch-changed', () => {
      // 收到推送说明连接健康，重置退避与轮询节奏。
      switchReconnectDelay = 3000
      void refreshLicenseState()
      startSwitchPolling()
    })
    source.onopen = () => {
      switchReconnectDelay = 3000
      startSwitchPolling()
    }
    source.onerror = () => {
      if (switchEventSource !== source) return
      source.onerror = null
      source.close()
      switchEventSource = null
      // 指数退避重连（3s → 6s → 12s → … 封顶 30s），避免服务异常时高频重连。
      const delay = Math.min(30000, switchReconnectDelay)
      switchReconnectDelay = Math.min(30000, switchReconnectDelay * 2)
      startSwitchPolling()
      scheduleSwitchReconnect(delay)
    }
    switchEventSource = source
  } catch {
    switchEventSource = null
    startSwitchPolling()
    scheduleSwitchReconnect(Math.min(30000, switchReconnectDelay))
    switchReconnectDelay = Math.min(30000, switchReconnectDelay * 2)
  } finally {
    if (connectionVersion === switchConnectionVersion) switchConnecting = false
  }
}

function scheduleSwitchReconnect(delay: number): void {
  if (!streamingActive || switchReconnectTimer || switchEventSource) return
  switchReconnectTimer = window.setTimeout(() => {
    switchReconnectTimer = 0
    if (streamingActive && !switchEventSource) void connectSwitchEvents()
  }, Math.max(1000, delay))
}

function disconnectSwitchEvents(): void {
  switchConnectionVersion++
  switchConnecting = false
  if (switchReconnectTimer) {
    window.clearTimeout(switchReconnectTimer)
    switchReconnectTimer = 0
  }
  if (switchEventSource) {
    switchEventSource.onerror = null
    switchEventSource.close()
    switchEventSource = null
  }
}

// 生成图纸（上传图片）会扣减一次，成功后立即刷新剩余次数与时长显示，无需等下一次心跳。
async function refreshLicenseInfo(): Promise<void> {
  if (!licensedKey.value) return
  try {
    const status = await getLicenseInfo(licensedKey.value)
    if (status.status === 'revoked' || status.status === 'invalid') {
      handleLicenseRevoked()
      return
    }
    licenseInfo.value = status
    if (status.remainingSeconds != null) licenseRemainingSeconds.value = status.remainingSeconds
  } catch {
    // 刷新失败保持原值，下一次心跳会自动校准。
  }
}

function startTrialCountdown(): void {
  if (licensingDisabled.value || document.hidden) return
  if (trialTimer) return
  trialTimer = window.setInterval(() => {
    if (trialRemainingSeconds.value <= 0) {
      stopTrialCountdown()
      if (!licensingDisabled.value && !trialExpired.value) {
        trialExpired.value = true
        notify('试用时间已到，请获取密钥后继续使用')
        openLicenseDialog()
      }
      return
    }
    trialRemainingSeconds.value--
  }, 1000)
}

function stopTrialCountdown(): void {
  if (trialTimer) {
    window.clearInterval(trialTimer)
    trialTimer = 0
  }
}

// 试用心跳：正常每 5 秒累加活跃时长，连续失败 3 次后降频到 15 秒；后台/关闭自动暂停。
// 多标签页下仅「主标签」实际发送心跳，避免多个标签重复请求触发全局限流。
function startTrialHeartbeat(): void {
  if (licensingDisabled.value || document.hidden) return
  if (licensedKey.value || trialExpired.value) return
  if (trialHeartbeatTimer || trialHeartbeatInFlight) return
  const generation = ++trialHeartbeatGeneration
  trialHeartbeatFailures = 0
  acquireHeartbeatLeader()
  scheduleTrialHeartbeat(generation)
}

function scheduleTrialHeartbeat(generation: number): void {
  if (generation !== trialHeartbeatGeneration || document.hidden || licensedKey.value || trialExpired.value) return
  const delay = trialHeartbeatFailures >= HEARTBEAT_FAILURE_THRESHOLD
    ? HEARTBEAT_DEGRADED_DELAY
    : HEARTBEAT_NORMAL_DELAY
  trialHeartbeatTimer = window.setTimeout(() => {
    trialHeartbeatTimer = 0
    if (generation !== trialHeartbeatGeneration || document.hidden) return
    if (!isHeartbeatLeader() || trialHeartbeatInFlight) {
      scheduleTrialHeartbeat(generation)
      return
    }
    trialHeartbeatInFlight = true
    void trialHeartbeat()
      .then(status => {
        if (generation !== trialHeartbeatGeneration) return
        trialHeartbeatFailures = 0
        if (trialExpired.value) return
        trialRemainingSeconds.value = status.remainingSeconds
        if (status.expired) {
          if (licensingDisabled.value) return
          trialExpired.value = true
          stopTrialHeartbeat()
          stopTrialCountdown()
          notify('试用时间已到，请获取密钥后继续使用')
          openLicenseDialog()
        }
      })
      .catch(() => {
        if (generation === trialHeartbeatGeneration) trialHeartbeatFailures++
      })
      .finally(() => {
        if (generation !== trialHeartbeatGeneration) return
        trialHeartbeatInFlight = false
        scheduleTrialHeartbeat(generation)
      })
  }, delay)
}

function stopTrialHeartbeat(): void {
  trialHeartbeatGeneration++
  if (trialHeartbeatTimer) {
    window.clearTimeout(trialHeartbeatTimer)
    trialHeartbeatTimer = 0
  }
  trialHeartbeatInFlight = false
  trialHeartbeatFailures = 0
}

// 密钥输入：仅允许字母与数字，自动过滤中文与符号。
function onLicenseInput(event: Event): void {
  const el = event.target as HTMLInputElement
  const filtered = el.value.replace(/[^A-Za-z0-9]/g, '')
  if (el.value !== filtered) el.value = filtered
  licenseInput.value = filtered
}

// 密钥登录防过载：锁定剩余秒数、倒计时启停。
function loginLockedSeconds(): number {
  return Math.max(0, Math.ceil((loginLockUntil.value - Date.now()) / 1000))
}

function startLoginLockCountdown(): void {
  stopLoginLockCountdown()
  const tick = (): void => {
    loginLockRemaining.value = loginLockedSeconds()
    if (loginLockRemaining.value <= 0) {
      stopLoginLockCountdown()
      window.localStorage.removeItem('pindou-login-lock-until')
    }
  }
  tick()
  loginLockTimer = window.setInterval(tick, 1000)
}

function stopLoginLockCountdown(): void {
  if (loginLockTimer) {
    window.clearInterval(loginLockTimer)
    loginLockTimer = 0
  }
}

// 刷新后恢复未过期的锁定状态，避免刷新绕过锁定。
const savedLock = Number(window.localStorage.getItem('pindou-login-lock-until') || 0)
if (savedLock > Date.now()) {
  loginLockUntil.value = savedLock
  loginLockRemaining.value = Math.ceil((savedLock - Date.now()) / 1000)
  startLoginLockCountdown()
} else {
  window.localStorage.removeItem('pindou-login-lock-until')
}

// 登录并激活密钥：按 UUID 区分新用户注册 / 老用户重登，并决定是否同步云端存档。
async function activateLicense(): Promise<void> {
  const key = licenseInput.value.trim()
  if (!key) {
    notify('请输入密钥')
    return
  }
  const locked = loginLockedSeconds()
  if (locked > 0) {
    notify(`尝试次数过多，请 ${locked} 秒后重试`)
    return
  }
  licenseVerifying.value = true
  try {
    const result = await loginLicense(key, getSessionToken())
    if (result.status === 'device_active') {
      notify(result.message || '该账号正在其他设备使用中，请先让该设备下线后再登录')
      return
    }
    if (result.status !== 'active' && result.status !== 'time_expired') {
      loginFailCount.value++
      if (loginFailCount.value >= 5) {
        loginLockUntil.value = Date.now() + 30000
        loginFailCount.value = 0
        window.localStorage.setItem('pindou-login-lock-until', String(loginLockUntil.value))
        startLoginLockCountdown()
        notify('尝试过于频繁，请 30 秒后再试')
      } else {
        notify(result.message || '用户名已存在或密钥错误，请核对后重试')
      }
      if (result.status === 'revoked') {
        openLicenseDialog()
        void refreshLicenseState()
      }
      return
    }
    // 登录成功：记录密钥并启动心跳计时，此后自动保存同步到云端。
    loginFailCount.value = 0
    stopLoginLockCountdown()
    loginLockRemaining.value = 0
    setActiveLicenseKey(key)
    setSessionToken(result.sessionToken)
    sessionStorage.removeItem(SESSION_KICKED_KEY)
    licensedKey.value = key
    licenseInfo.value = {
      status: result.status,
      remainingSeconds: result.remainingSeconds,
      remainingCount: result.remainingCount,
      message: result.message,
      kickInSeconds: null,
    }
    if (result.remainingSeconds != null) licenseRemainingSeconds.value = result.remainingSeconds
    stopTrialCountdown()
    trialExpired.value = false
    trialResolved.value = true
    licenseDialog.value?.close()
    startLicenseHeartbeat(key)
    void connectKickEvents()

    if (result.isNewUser) {
      // 匹配不到该 UUID，注册为新用户：保留本地画布并立即上传一次。
      notify('注册成功，豆板已开始同步到云端')
      scheduleCloudSave()
    } else if (result.hasSave) {
      // 空白画布无需保护本地内容，直接恢复云端；只有实际含豆子的本地图纸才需要用户明确选择保留哪一份。
      if (beadCount.value > 0) openSyncConfirm()
      else await restoreCloudSave()
    } else {
      notify('登录成功，豆板已开始同步到云端')
      scheduleCloudSave()
    }
  } catch (reason) {
    notify(reason instanceof Error ? reason.message : '登录失败，请重试')
  } finally {
    licenseVerifying.value = false
  }
}

// 弹出「是否同步云端数据」确认框：仅在老用户重登且有云端存档时调用。
function openSyncConfirm(): void {
  if (!syncCloudDialog.value?.open) syncCloudDialog.value?.showModal()
}

// 用户选择「同步云端」：用云端存档覆盖当前本地画布（当前拼豆数据清空）。
async function confirmSyncCloud(): Promise<void> {
  syncCloudDialog.value?.close()
  await restoreCloudSave()
}

// 用户选择「保留本地」：不下载云端存档，保留当前画布并立即上传覆盖云端。
function declineSyncCloud(): void {
  syncCloudDialog.value?.close()
  notify('已保留本地豆板，稍后将同步到云端')
  scheduleCloudSave()
}

async function deactivateLicense(): Promise<void> {
  // 移除密钥相当于退出登录：通知后端清除会话并标记离线。
  const key = licensedKey.value
  const token = getSessionToken()
  try {
    if (key) await logoutLicense(key, token)
  } catch {
    // 登出失败不影响本地移除。
  }
  closeKickEvents()
  stopLicenseHeartbeat()
  stopLicenseCountdown()
  licenseRemainingSeconds.value = 0
  clearActiveLicenseKey()
  setSessionToken(null)
  sessionStorage.removeItem(SESSION_KICKED_KEY)
  licensedKey.value = ''
  licenseInfo.value = null
  notify('已移除本地密钥')
  void refreshLicenseState()
}

// 获取密钥按钮暂不开放购买流程，先引导联系开发者。
function requestLicense(): void {
  notify('获取密钥功能即将上线，请先联系开发者')
  developerDialog.value?.showModal()
}

// 无密钥且试用达到上限时，在进入生成流程前直接拦截并引导获取密钥。
function ensureGenerationAllowed(): boolean {
  if (licensingDisabled.value) return true
  if (licensedKey.value) return true
  if (!trialResolved.value) return true
  if (trialExpired.value || trialRemainingGenerations.value <= 0) {
    notify('免费试用已达上限，请获取密钥后继续使用')
    openLicenseDialog()
    return false
  }
  return true
}

// 试用到期后，除弹窗内部、授权按钮与提示气泡外，任何点击（含点击画布或空白）都弹出获取密钥提示。
function guardAccessOnPointerDown(event: PointerEvent): void {
  if (licensingDisabled.value) return
  if (licensedKey.value) return
  if (!trialResolved.value) return
  if (!trialExpired.value && trialRemainingGenerations.value > 0) return
  const target = event.target instanceof Element ? event.target : null
  if (!target) return
  // 开屏动画属于启动导航，不是受限业务操作；点击跳过时不得触发试用到期授权弹窗。
  if (target.closest('dialog, .license-entry-button, .toast, .splash-screen')) return
  event.preventDefault()
  event.stopPropagation()
  notify('免费试用已达上限，请获取密钥后继续使用')
  openLicenseDialog()
}

function openExportDialog(): void {
  if (!hasPattern.value) return
  exportDialog.value?.showModal()
}

function toggleTools(): void {
  toolsCollapsed.value = !toolsCollapsed.value
}

function clampGenerationMaxColors(): void {
  const cellCount = Number.isInteger(generationDraft.width) && Number.isInteger(generationDraft.height)
    ? Math.max(2, generationDraft.width * generationDraft.height)
    : 96
  const limit = Math.min(96, generationPaletteSummary.value?.colorCount || 96, cellCount)
  generationDraft.maxColors = Math.max(2, Math.min(limit, Math.round(Number(generationDraft.maxColors) || 2)))
}

function ensureGenerationBoardSelection(): void {
  if (generationCompatibleBoards.value.some(board => board.id === generationDraft.boardId)) return
  generationDraft.boardId = generationCompatibleBoards.value[0]?.id || ''
}

function resetGenerationDraftFromStore(): void {
  Object.assign(generationDraft, {
    brandId: selectedBrandId.value,
    paletteId: selectedPaletteId.value,
    boardId: selectedBoardId.value,
    width: width.value,
    height: height.value,
    maxColors: maxColors.value,
    dither: dither.value,
    removeBackground: removeBackground.value,
    backgroundThreshold: backgroundThreshold.value,
    noiseSuppression: noiseSuppression.value,
  } satisfies GenerationDraft)
  ensureGenerationBoardSelection()
  clampGenerationMaxColors()
}

function isDockedGenerationViewport(): boolean {
  // 宽而矮的触屏横屏仍使用移动布局，不能只根据宽度误判为桌面停靠栏。
  return window.matchMedia('(min-width: 1024px)').matches
    && !window.matchMedia(mobileLayoutMedia).matches
    && !isFullscreen.value
}

function remountGenerationPanelIfNeeded(): void {
  if (!generationPanelOpen.value) return
  const nextDocked = isDockedGenerationViewport()
  if (nextDocked === generationPanelDocked.value) return
  // 裁剪或最终确认属于二级模态层，关闭后再切换底层生成面板，避免抢占 top layer。
  if (cropDialog.value?.open || generateConfirmDialog.value?.open) return
  const currentStep = generationStep.value
  settingsDialog.value?.close()
  generationStep.value = currentStep
  void nextTick(() => presentGenerationPanel(false))
}

/**
 * 同一个原生 dialog 根据视口选择两种承载形态：桌面端用非模态右侧面板，
 * 窄屏和全屏用 top-layer 模态抽屉。这样既保留原生可访问性，也不会在桌面遮挡豆板。
 */
function presentGenerationPanel(resetStep = false): void {
  const dialog = settingsDialog.value
  if (!dialog) return
  toolsCollapsed.value = true
  if (resetStep) {
    resetGenerationDraftFromStore()
    generationStep.value = 1
  }
  const nextDocked = isDockedGenerationViewport()
  if (dialog.open && nextDocked !== generationPanelDocked.value) {
    const currentStep = generationStep.value
    dialog.close()
    generationStep.value = currentStep
    void nextTick(() => presentGenerationPanel(false))
    return
  }
  generationPanelDocked.value = nextDocked
  generationPanelOpen.value = true
  if (dialog.open) return
  if (generationPanelDocked.value) dialog.show()
  else dialog.showModal()
  if (generationPanelDocked.value && hasPattern.value) {
    // 等待 180ms 栅格过渡完成后重新适配，确保右侧面板出现时豆板仍在剩余舞台居中完整显示。
    window.setTimeout(() => { void patternCanvas.value?.fitPatternInViewport() }, 200)
  }
}

function openSettingsDialog(): void {
  presentGenerationPanel(true)
}

function closeGenerationPanel(): void {
  settingsDialog.value?.close()
}

function handleGenerationPanelClose(): void {
  // 响应式切换会先关闭再立即重开同一个 dialog；忽略重开后到达的旧 close 事件。
  if (settingsDialog.value?.open) return
  const wasDocked = generationPanelDocked.value
  generationPanelOpen.value = false
  generationPanelDocked.value = false
  if (wasDocked && hasPattern.value) {
    window.setTimeout(() => { void patternCanvas.value?.fitPatternInViewport() }, 200)
  }
}

function handleCropDialogClose(): void {
  releaseCropSource()
  remountGenerationPanelIfNeeded()
}

function handleGenerateConfirmClose(): void {
  remountGenerationPanelIfNeeded()
}

function setGenerationStep(step: GenerationStep): void {
  generationStep.value = step
  void nextTick(() => {
    settingsDialog.value?.querySelector<HTMLElement>('.generation-pages')?.scrollTo({ top: 0 })
  })
}

function generationIssueStep(key: string): GenerationStep {
  return ['brand', 'palette', 'board'].includes(key) ? 2 : 3
}

async function advanceGenerationStep(): Promise<void> {
  if (generationStep.value >= 4) return
  // 在离开参数步骤前先校验，错误直接定位，避免把无效配置带到最终创建确认页。
  if (generationStep.value === 2) {
    const issue = generationValidationIssues.value.find(item => generationIssueStep(item.key) === 2)
    if (issue) { await focusGenerationIssue(issue.key); return }
  }
  if (generationStep.value === 3 && generationValidationIssues.value.length) {
    await focusGenerationIssue(generationValidationIssues.value[0].key)
    return
  }
  setGenerationStep((generationStep.value + 1) as GenerationStep)
}

function previousGenerationStep(): void {
  if (generationStep.value > 1) setGenerationStep((generationStep.value - 1) as GenerationStep)
}

function openGenerateConfirmDialog(target: GenerationTarget): void {
  if (loading.value || generationSubmitting.value) return
  if (target === 'image' && !imageFile.value) {
    notify('请先上传并确认裁剪图片')
    setGenerationStep(1)
    return
  }
  if (!ensureGenerationAllowed()) return
  if (generationValidationIssues.value.length) {
    notify('参数检查未通过，请先修正标记的问题')
    void focusGenerationIssue(generationValidationIssues.value[0].key)
    return
  }
  generationTarget.value = target
  generateConfirmDialog.value?.showModal()
}

async function focusGenerationIssue(key: string): Promise<void> {
  generateConfirmDialog.value?.close()
  presentGenerationPanel(false)
  setGenerationStep(generationIssueStep(key))
  await nextTick()

  const field = settingsDialog.value?.querySelector<HTMLElement>(`[data-validation-key="${key}"]`)
  if (!field) return
  field.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'nearest' })
  window.setTimeout(() => field.focus({ preventScroll: true }), 220)
}

function returnToGenerationIssue(): void {
  const firstIssue = generationValidationIssues.value[0]
  if (firstIssue) {
    void focusGenerationIssue(firstIssue.key)
    return
  }
  generateConfirmDialog.value?.close()
}

function openMaterialsDialog(): void {
  materialsDialog.value?.showModal()
}

function openReplacementDialog(): void {
  const firstUsed = replacementSourceOptions.value[0]
  if (!firstUsed) return
  replacementFromIndex.value = firstUsed.index
  const preferredTarget = selectedColorIndex.value >= 0 && selectedColorIndex.value !== firstUsed.index
    ? selectedColorIndex.value
    : colors.value.findIndex((_, index) => index !== firstUsed.index)
  replacementToIndex.value = Math.max(0, preferredTarget)
  replacementDialog.value?.showModal()
}

function confirmColorReplacement(): void {
  const from = replacementFromIndex.value
  const to = replacementToIndex.value
  const sourceColor = colors.value[from]
  const targetColor = colors.value[to]
  if (!sourceColor || !targetColor || from === to) return
  store.replaceColor(from, to)
  selectedColorIndex.value = to
  interactionMode.value = 'pan'
  replacementDialog.value?.close()
  toolsCollapsed.value = true
  notify(`已将 ${sourceColor.code} 替换为 ${targetColor.code}，可使用撤销恢复`)
}

async function openIronedPreview(): Promise<void> {
  if (!hasPattern.value) return
  showIronedPreview.value = true
  await nextTick()
  syncLandscapeFallback()

  const preview = ironedPreview.value
  if (preview && document.fullscreenElement !== centerStage.value) {
    try {
      await preview.requestFullscreen()
      ironedPreviewOwnsFullscreen.value = true
    } catch {
      // 不支持全屏 API 的微信和 iOS 浏览器仍使用固定定位铺满可视区域。
    }
  }
  await lockLandscape()
  syncLandscapeFallback()
}

async function closeIronedPreview(): Promise<void> {
  if (ironedPreviewOwnsFullscreen.value && document.fullscreenElement === ironedPreview.value) {
    await document.exitFullscreen().catch(() => undefined)
  }
  ironedPreviewOwnsFullscreen.value = false
  showIronedPreview.value = false
  if (isFullscreen.value) await lockLandscape()
  else resetFullscreenOrientation()
}

function retryAutoSave(): void {
  performAutoSave(true)
}

function exportAfterSaveFailure(): void {
  saveErrorDialog.value?.close()
  openExportDialog()
}

function closeExportDialogFromBackdrop(event: MouseEvent): void {
  // 所有弹窗点击遮罩不再关闭，避免误触关闭导出窗口。
  void event
}

function closeDialogFromBackdrop(event: MouseEvent, dialog: HTMLDialogElement | null): void {
  // 所有弹窗点击遮罩不再关闭，避免误触关闭确认/信息窗口。
  void event
  void dialog
}

function copyTextWithSelection(value: string): boolean {
  const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
  const input = document.createElement('textarea')
  input.value = value
  input.setAttribute('aria-hidden', 'true')
  input.style.position = 'fixed'
  input.style.top = '0'
  input.style.left = '0'
  input.style.width = '2px'
  input.style.height = '2px'
  input.style.padding = '0'
  input.style.border = '0'
  input.style.opacity = '0.01'
  input.style.fontSize = '16px'
  input.style.pointerEvents = 'none'
  document.body.appendChild(input)

  // iOS/微信 WebView 对 readonly、完全透明或移出屏幕的输入框可能拒绝复制，需真实聚焦并设置选区。
  input.focus({ preventScroll: true })
  input.select()
  input.setSelectionRange(0, value.length)
  let copied = false
  try {
    copied = document.execCommand('copy')
  } catch {
    copied = false
  }
  input.remove()
  previousFocus?.focus({ preventScroll: true })
  return copied
}

async function copyDeveloperWeChat(openWeChat = false): Promise<void> {
  const weChatId = developerContact.weChatId
  if (!weChatId) {
    notify('暂未配置开发者微信号')
    return
  }

  let copied = false
  try {
    await navigator.clipboard.writeText(weChatId)
    copied = true
  } catch {
    // 局域网 HTTP 页面可能没有 Clipboard API 权限，使用选区复制兼容旧浏览器和微信内置浏览器。
    copied = copyTextWithSelection(weChatId)
  }

  if (!copied) {
    notify(`复制失败，请手动搜索微信号 ${weChatId}`)
    return
  }

  if (!openWeChat) {
    notify(`微信号 ${weChatId} 已复制`)
    return
  }

  notify(`微信号 ${weChatId} 已复制，正在打开微信`)
  // 微信未提供按个人微信号直达资料页的网页接口，只能唤起客户端，再由用户粘贴搜索。
  window.setTimeout(() => {
    window.location.href = 'weixin://'
  }, 160)
}

function showWeChatExportResult(pngBlob: Blob | null, hasOtherFiles: boolean): void {
  if (weChatPreviewUrl.value) URL.revokeObjectURL(weChatPreviewUrl.value)
  weChatPreviewUrl.value = pngBlob ? URL.createObjectURL(pngBlob) : ''
  weChatHasOtherFiles.value = hasOtherFiles
  showSystemBrowserGuide.value = false
  exportDialog.value?.close()
  weChatExportDialog.value?.showModal()
}

function releaseWeChatPreview(): void {
  showSystemBrowserGuide.value = false
  systemBrowserAddress.value = ''
  if (!weChatPreviewUrl.value) return
  URL.revokeObjectURL(weChatPreviewUrl.value)
  weChatPreviewUrl.value = ''
}

function openWeChatImage(): void {
  if (!weChatPreviewUrl.value) return
  const preview = window.open(weChatPreviewUrl.value, '_blank')
  if (preview) preview.opener = null
  else notify('请长按预览图保存到手机相册')
}

async function copyPageAddress(pageAddress: string): Promise<boolean> {
  try {
    if (!navigator.clipboard?.writeText) throw new Error('当前环境不支持 Clipboard API')
    await navigator.clipboard.writeText(pageAddress)
    return true
  } catch {
    return copyTextWithSelection(pageAddress)
  }
}

async function copySystemBrowserAddress(): Promise<void> {
  const copied = await copyPageAddress(systemBrowserAddress.value || window.location.href)
  notify(copied ? '页面地址已复制' : '自动复制受限，请长按下方接力地址手动复制')
}

function selectSystemBrowserAddress(event: Event): void {
  const input = event.currentTarget as HTMLTextAreaElement
  input.focus({ preventScroll: true })
  input.select()
  input.setSelectionRange(0, input.value.length)
}

async function openInSystemBrowser(): Promise<void> {
  if (systemBrowserPreparing.value) return
  systemBrowserPreparing.value = true
  showSystemBrowserGuide.value = true
  notify('正在准备图纸接力数据…')

  let currentUrl: URL
  try {
    const handoff = await createBrowserHandoff(store.createProjectSnapshot())
    currentUrl = new URL(window.location.href)
    currentUrl.searchParams.set('handoff', handoff.token)
    systemBrowserAddress.value = currentUrl.toString()
    // 微信右上角“在浏览器打开”使用当前地址，因此同步替换地址栏但不刷新当前工作区。
    window.history.replaceState(window.history.state, '', currentUrl)
  } catch (reason) {
    notify(reason instanceof Error ? `图纸接力准备失败：${reason.message}` : '图纸接力准备失败，请检查网络后重试')
    systemBrowserPreparing.value = false
    return
  }

  const copied = await copyPageAddress(systemBrowserAddress.value)
  notify(copied ? '地址和图纸数据已准备，正在尝试打开系统浏览器' : '接力地址已准备，未跳转时可长按地址复制')

  if (!/Android/i.test(navigator.userAgent)) {
    systemBrowserPreparing.value = false
    return
  }

  // 微信可能拦截 Android Intent；先显示操作指引，拦截后用户仍可通过右上角菜单继续。
  const scheme = currentUrl.protocol.replace(':', '')
  const target = `${currentUrl.host}${currentUrl.pathname}${currentUrl.search}`
  const fallback = encodeURIComponent(currentUrl.toString())
  window.setTimeout(() => {
    window.location.href = `intent://${target}#Intent;scheme=${scheme};action=android.intent.action.VIEW;category=android.intent.category.BROWSABLE;S.browser_fallback_url=${fallback};end`
    systemBrowserPreparing.value = false
  }, 80)
}

async function restoreBrowserHandoffFromUrl(): Promise<void> {
  const currentUrl = new URL(window.location.href)
  const token = currentUrl.searchParams.get('handoff')
  if (!token) return

  // 先清理地址，避免刷新页面重复请求已经消费的一次性令牌。
  currentUrl.searchParams.delete('handoff')
  window.history.replaceState(window.history.state, '', currentUrl)
  try {
    const project = await consumeBrowserHandoff(token)
    await store.restoreSharedProject(project)
    interactionMode.value = 'pan'
    await nextTick()
    await patternCanvas.value?.fitPatternInViewport()
    notify('已从微信恢复当前图纸和参数')
    // 联机数据同步：房主从微信接力恢复图纸（整块替换画布）后同步给成员。
    if (collabActive.value && collabIsHost.value) void collab.resyncSnapshot()
  } catch (reason) {
    notify(reason instanceof Error ? reason.message : '图纸接力恢复失败，请返回微信重新打开')
  }
}

function releaseCropSource(): void {
  // 用请求序号使尚未完成的图片分析失效，防止旧图结果覆盖新一次选择。
  cropDetectionRequestId++
  pendingDetectedSpecs.value = null
  if (cropSourceUrl.value) URL.revokeObjectURL(cropSourceUrl.value)
  cropSourceUrl.value = ''
  cropSourceFile.value = null
  cropNaturalWidth.value = 0
  cropNaturalHeight.value = 0
  cropDrag = null
}

// 删除已上传的图片，回到未上传状态；已识别的参数保留，重新上传会再次自动识别。
function clearUploadedImage(): void {
  releaseCropSource()
  if (previewUrl.value) URL.revokeObjectURL(previewUrl.value)
  previewUrl.value = ''
  imageFile.value = null
  pendingGenerationTitle.value = ''
  notify('已移除图片')
}

function clampCropOffset(): void {
  const viewport = cropViewport.value
  if (!viewport || !cropNaturalWidth.value || !cropNaturalHeight.value) return
  const scale = cropBaseScale.value * cropZoom.value
  const overflowX = Math.max(0, (cropNaturalWidth.value * scale - viewport.clientWidth) / 2)
  const overflowY = Math.max(0, (cropNaturalHeight.value * scale - viewport.clientHeight) / 2)
  cropOffsetX.value = Math.min(overflowX, Math.max(-overflowX, cropOffsetX.value))
  cropOffsetY.value = Math.min(overflowY, Math.max(-overflowY, cropOffsetY.value))
}

function fitCropImage(resetPosition = false): void {
  const viewport = cropViewport.value
  if (!viewport || !cropNaturalWidth.value || !cropNaturalHeight.value) return
  const oldScale = cropBaseScale.value * cropZoom.value
  const sourceOffsetX = oldScale > 0 ? cropOffsetX.value / oldScale : 0
  const sourceOffsetY = oldScale > 0 ? cropOffsetY.value / oldScale : 0

  // Contain 缩放取宽、高适配倍数中的较小值，保证不规则比例的原图可以完整进入图纸。
  // 图片未覆盖的画布区域保持透明，量化后对应位置不会生成豆子。
  cropBaseScale.value = Math.min(
    viewport.clientWidth / cropNaturalWidth.value,
    viewport.clientHeight / cropNaturalHeight.value,
  )
  if (resetPosition) {
    cropZoom.value = 1
    cropOffsetX.value = 0
    cropOffsetY.value = 0
  } else {
    const nextScale = cropBaseScale.value * cropZoom.value
    cropOffsetX.value = sourceOffsetX * nextScale
    cropOffsetY.value = sourceOffsetY * nextScale
  }
  clampCropOffset()
}

function updateCropZoom(value: number): void {
  const nextZoom = Math.min(4, Math.max(1, value))
  const ratio = nextZoom / cropZoom.value
  cropOffsetX.value *= ratio
  cropOffsetY.value *= ratio
  cropZoom.value = nextZoom
  clampCropOffset()
}

function zoomCropFromWheel(event: WheelEvent): void {
  updateCropZoom(cropZoom.value * (event.deltaY < 0 ? 1.08 : 0.92))
}

function beginCropDrag(event: PointerEvent): void {
  if (cropBusy.value || event.button > 0) return
  cropDrag = {
    pointerId: event.pointerId,
    startX: event.clientX,
    startY: event.clientY,
    offsetX: cropOffsetX.value,
    offsetY: cropOffsetY.value,
  }
  cropViewport.value?.setPointerCapture(event.pointerId)
}

function moveCropImage(event: PointerEvent): void {
  if (!cropDrag || cropDrag.pointerId !== event.pointerId) return
  cropOffsetX.value = cropDrag.offsetX + event.clientX - cropDrag.startX
  cropOffsetY.value = cropDrag.offsetY + event.clientY - cropDrag.startY
  clampCropOffset()
}

function endCropDrag(event: PointerEvent): void {
  if (!cropDrag || cropDrag.pointerId !== event.pointerId) return
  cropViewport.value?.releasePointerCapture(event.pointerId)
  cropDrag = null
}

async function chooseFile(file?: File): Promise<void> {
  if (!file) return
  if (!file.type.startsWith('image/')) {
    notify('请选择JPG、PNG或WebP图片')
    return
  }
  if (file.size > 15 * 1024 * 1024) {
    notify('图片不能超过15MB')
    return
  }

  releaseCropSource()
  cropSourceFile.value = file
  cropSourceUrl.value = URL.createObjectURL(file)

  const source = new Image()
  source.src = cropSourceUrl.value
  try {
    await source.decode()
  } catch {
    releaseCropSource()
    notify('图片无法读取，请换一张图片重试')
    return
  }

  cropNaturalWidth.value = source.naturalWidth
  cropNaturalHeight.value = source.naturalHeight
  // 先将自动识别结果保存为本次裁剪的待确认建议；取消裁剪时不写入生成草稿。
  const detectionRequestId = ++cropDetectionRequestId
  try {
    const detected = await detectImageSpecs(file)
    if (detectionRequestId !== cropDetectionRequestId || cropSourceFile.value !== file) return
    pendingDetectedSpecs.value = detected
  } catch {
    if (detectionRequestId !== cropDetectionRequestId || cropSourceFile.value !== file) return
    pendingDetectedSpecs.value = null
    notify('自动识别失败，将沿用当前图纸参数')
  }
  cropDialog.value?.showModal()
  await nextTick()
  window.requestAnimationFrame(() => fitCropImage(true))
}

function onFileInputChange(event: Event): void {
  const input = event.target as HTMLInputElement
  void chooseFile(input.files?.[0])
  // 清空 input，取消后仍可再次选择同一张图片。
  input.value = ''
}

async function confirmCrop(): Promise<void> {
  const source = cropImage.value
  const viewport = cropViewport.value
  const original = cropSourceFile.value
  if (!source || !viewport || !original || cropBusy.value) return

  cropBusy.value = true
  try {
    const displayScale = cropBaseScale.value * cropZoom.value

    // 目标尺寸使用每颗豆 8 个像素，足够后端 4×4 多点采样，同时限制上传体积和移动端内存占用。
    const canvas = document.createElement('canvas')
    const outputWidth = pendingDetectedSpecs.value?.width ?? generationDraft.width
    const outputHeight = pendingDetectedSpecs.value?.height ?? generationDraft.height
    canvas.width = Math.max(1, Math.round(outputWidth * 8))
    canvas.height = Math.max(1, Math.round(outputHeight * 8))
    const context = canvas.getContext('2d', { alpha: true })
    if (!context) throw new Error('当前浏览器不支持图片裁剪')
    context.clearRect(0, 0, canvas.width, canvas.height)
    context.imageSmoothingEnabled = true
    context.imageSmoothingQuality = 'high'

    // 将裁剪框内看到的布局原样映射到透明画布：图片小于裁剪框时不拉伸，四周自然保留透明像素；
    // 图片大于裁剪框时由 Canvas 边界完成裁切，避免导出结果和预览位置不一致。
    const renderedWidth = cropNaturalWidth.value * displayScale
    const renderedHeight = cropNaturalHeight.value * displayScale
    const outputScaleX = canvas.width / viewport.clientWidth
    const outputScaleY = canvas.height / viewport.clientHeight
    const imageLeft = viewport.clientWidth / 2 + cropOffsetX.value - renderedWidth / 2
    const imageTop = viewport.clientHeight / 2 + cropOffsetY.value - renderedHeight / 2
    context.drawImage(
      source,
      imageLeft * outputScaleX,
      imageTop * outputScaleY,
      renderedWidth * outputScaleX,
      renderedHeight * outputScaleY,
    )

    const blob = await new Promise<Blob>((resolve, reject) => {
      canvas.toBlob(result => result ? resolve(result) : reject(new Error('裁剪图片生成失败')), 'image/png')
    })
    const baseName = original.name.replace(/\.[^.]+$/, '') || '拼豆原图'
    const croppedFile = new File([blob], `${baseName}-裁剪.png`, { type: 'image/png', lastModified: Date.now() })

    // 只有确认裁剪后才替换正式上传文件，取消裁剪不会影响上一张已经确认的图片。
    if (previewUrl.value) URL.revokeObjectURL(previewUrl.value)
    imageFile.value = croppedFile
    previewUrl.value = URL.createObjectURL(croppedFile)
    if (pendingDetectedSpecs.value) {
      generationDraft.width = pendingDetectedSpecs.value.width
      generationDraft.height = pendingDetectedSpecs.value.height
      generationDraft.maxColors = pendingDetectedSpecs.value.maxColors
    }
    // 标题同样延后到图纸生成成功后提交，取消或生成失败不污染当前项目。
    pendingGenerationTitle.value = baseName
    cropDialog.value?.close()
    setGenerationStep(2)
    notify(`裁剪完成，空白区域将按无豆处理`)
  } catch (reason) {
    notify(reason instanceof Error ? reason.message : '裁剪失败，请重试')
  } finally {
    cropBusy.value = false
  }
}

function onDrop(event: DragEvent): void {
  dragging.value = false
  chooseFile(event.dataTransfer?.files?.[0])
}

async function generate(): Promise<void> {
  if (generationSubmitting.value) return
  if (generationValidationIssues.value.length) {
    notify('参数检查未通过，请先修正标记的问题')
    await focusGenerationIssue(generationValidationIssues.value[0].key)
    return
  }
  if (generationTarget.value === 'blank') {
    generationSubmitting.value = true
    try {
      await newBlank({ ...generationDraft })
    } catch (reason) {
      notify(reason instanceof Error ? reason.message : '空白画布创建失败，请重试')
    } finally {
      generationSubmitting.value = false
    }
    return
  }
  if (!imageFile.value) {
    generateConfirmDialog.value?.close()
    setGenerationStep(1)
    notify('请先上传并确认裁剪图片')
    return
  }
  generationSubmitting.value = true
  try {
    await store.generate(imageFile.value, { ...generationDraft })
    if (pendingGenerationTitle.value
      && (title.value === '我的拼豆图纸' || title.value === '橘猫头像示例')) {
      title.value = pendingGenerationTitle.value
    }
    // 上传识别生成后自动提示可合并的同色大块（纯统计，不影响图纸数据与其它业务）。
    const mergeInfo = mergeStats.value
    if (mergeInfo.blocks > 0) notify(`检测到同色区域可合并为 ${mergeInfo.blocks} 块，节省 ${mergeInfo.saved} 颗豆子`)
    interactionMode.value = 'pan'
    generateConfirmDialog.value?.close()
    closeGenerationPanel()
    await nextTick()
    await patternCanvas.value?.fitPatternInViewport()
    // 联机数据同步：
    // - 房主：重新生成图纸后把新画布同步给联机成员（服务端更新快照 + 广播 resync）。
    // - 成员：生成自家图纸后需申请替换整张联机图纸，经房主同意后全房间同步。
    if (collabActive.value && collabIsHost.value) {
      notify('图纸已生成，可以继续手动精修')
      void collab.resyncSnapshot()
    } else if (collabActive.value && collabIsMember.value) {
      requestCanvasReplace(captureCollabSnapshot())
    } else {
      notify('图纸已生成，可以继续手动精修')
    }
    void refreshLicenseInfo()
  } catch (reason) {
    if (reason instanceof ApiError && reason.code === 'DEVICE_CONFLICT') {
      handleDeviceConflict()
      return
    }
    if (reason instanceof ApiError && reason.status === 402) {
      if (!licensingDisabled.value) licenseDialog.value?.showModal()
      notify(reason.message || '请获取密钥后继续使用')
    } else {
      notify(error.value || '生成失败')
    }
  } finally {
    generationSubmitting.value = false
  }
}

function changeBrandById(brandId: string): void {
  const brand = brands.value.find(item => item.id === brandId)
  if (!brand?.palettes[0]) return
  // 面板内只改生成草稿；色卡网络请求延后到最终确认，避免清空当前画布。
  generationDraft.brandId = brandId
  generationDraft.paletteId = brand.palettes[0].id
  ensureGenerationBoardSelection()
  clampGenerationMaxColors()
  notify(`已切换到${brand.name}`)
}

function changeBrand(event: Event): void {
  changeBrandById((event.target as HTMLSelectElement).value)
}

function changePaletteById(paletteId: string): void {
  generationDraft.paletteId = paletteId
  ensureGenerationBoardSelection()
  clampGenerationMaxColors()
  notify('色卡已切换')
}

function changePalette(event: Event): void {
  changePaletteById((event.target as HTMLSelectElement).value)
}

function applyGridPreset(value: number): void {
  generationDraft.width = value
  generationDraft.height = value
  clampGenerationMaxColors()
}

// 纯分析函数：返回图片建议规格，不写入编辑器或生成草稿。
async function detectImageSpecs(sourceFile: File): Promise<DetectedGenerationSpecs> {
    const bitmap = await createImageBitmap(sourceFile)
    try {
    const imgW = bitmap.width
    const imgH = bitmap.height
    const long = Math.max(imgW, imgH)

    // 像素聚合：识别整个图片，相邻同色像素标记为一格；面积小于固定阈值的孤立杂点忽略（去除杂色）。
    const { blockCount } = analyzeImageClusters(bitmap)
    const colorCount = await estimateColorCount(bitmap)
    // 根据内容块数生成规格：块越多细节越复杂，图纸应越大（每格约对应一个内容块）。
    let targetMax: number
    if (blockCount <= 20) targetMax = 40
    else if (blockCount <= 60) targetMax = 56
    else if (blockCount <= 120) targetMax = 72
    else targetMax = 96
    // 小像素图不放大超过 4 倍，避免规格虚大。
    if (long < targetMax) targetMax = Math.max(8, Math.min(targetMax, long * 4))

    const w = Math.max(8, Math.min(160, Math.round(targetMax * imgW / long)))
    const h = Math.max(8, Math.min(160, Math.round(targetMax * imgH / long)))

    const maxLimit = Math.min(96, generationPaletteSummary.value?.colorCount || 96, Math.max(2, w * h))
    const suggestedMaxColors = Math.max(2, Math.min(maxLimit, colorCount))

    return { width: w, height: h, maxColors: suggestedMaxColors, blockCount }
  } finally {
    bitmap.close()
  }
}

// 像素聚合：把图片缩到小图，量化颜色后对相邻同色像素做连通聚类；去除面积小于固定阈值的孤立杂点。
function analyzeImageClusters(bitmap: ImageBitmap): { blockCount: number; largestBlock: number } {
  const size = 96
  const canvas = document.createElement('canvas')
  canvas.width = size
  canvas.height = size
  const ctx = canvas.getContext('2d', { willReadFrequently: true })
  if (!ctx) return { blockCount: 0, largestBlock: 0 }
  const scale = Math.min(size / bitmap.width, size / bitmap.height)
  const dw = Math.max(1, Math.round(bitmap.width * scale))
  const dh = Math.max(1, Math.round(bitmap.height * scale))
  ctx.drawImage(bitmap, 0, 0, dw, dh)
  const data = ctx.getImageData(0, 0, dw, dh).data

  const visited = new Uint8Array(dw * dh)
  const queue: number[] = []
  const areas: number[] = []
  for (let i = 0; i < dw * dh; i++) {
    if (visited[i]) continue
    const alpha = data[i * 4 + 3]
    if (alpha < 32) { visited[i] = 1; continue }
    const q = ((data[i * 4] >> 4) << 8) | ((data[i * 4 + 1] >> 4) << 4) | (data[i * 4 + 2] >> 4)
    queue.length = 0
    queue.push(i)
    visited[i] = 1
    let area = 0
    while (queue.length) {
      const idx = queue.pop() as number
      area++
      const x = idx % dw, y = (idx / dw) | 0
      const tryPush = (ni: number, nx: number, ny: number): void => {
        if (nx < 0 || nx >= dw || ny < 0 || ny >= dh || visited[ni]) return
        if (data[ni * 4 + 3] < 32) { visited[ni] = 1; return }
        const nq = ((data[ni * 4] >> 4) << 8) | ((data[ni * 4 + 1] >> 4) << 4) | (data[ni * 4 + 2] >> 4)
        if (nq !== q) return
        visited[ni] = 1
        queue.push(ni)
      }
      tryPush(idx - 1, x - 1, y)
      tryPush(idx + 1, x + 1, y)
      tryPush(idx - dw, x, y - 1)
      tryPush(idx + dw, x, y + 1)
    }
    // 去除杂色：面积小于固定阈值的孤立小块不计为内容块。
    if (area >= 4) areas.push(area)
  }
  areas.sort((a, b) => b - a)
  return { blockCount: areas.length, largestBlock: areas[0] || 0 }
}

// 把图片等比缩到小图后统计去重颜色（透明像素忽略）。
async function estimateColorCount(bitmap: ImageBitmap): Promise<number> {
  const size = 64
  const canvas = document.createElement('canvas')
  canvas.width = size
  canvas.height = size
  const ctx = canvas.getContext('2d')
  if (!ctx) return 32
  const scale = Math.min(size / bitmap.width, size / bitmap.height)
  const dw = Math.max(1, Math.round(bitmap.width * scale))
  const dh = Math.max(1, Math.round(bitmap.height * scale))
  ctx.drawImage(bitmap, 0, 0, dw, dh)
  const data = ctx.getImageData(0, 0, dw, dh).data
  const colors = new Set<number>()
  for (let i = 0; i < data.length; i += 4) {
    if (data[i + 3] < 32) continue
    const key = ((data[i] >> 4) << 8) | ((data[i + 1] >> 4) << 4) | (data[i + 2] >> 4)
    colors.add(key)
  }
  return colors.size || 2
}

async function newBlank(settings: GenerationDraft): Promise<void> {
  await store.createBlank(settings)
  interactionMode.value = 'pan'
  generateConfirmDialog.value?.close()
  closeGenerationPanel()
  void nextTick(() => patternCanvas.value?.fitPatternInViewport())
  // 联机数据同步：
  // - 房主：新建空白豆板（整块替换画布）后同步给成员。
  // - 成员：新建空白豆板需申请替换整张联机图纸，经房主同意后全房间同步。
  if (collabActive.value && collabIsHost.value) {
    notify('已新建空白豆板')
    void collab.resyncSnapshot()
  } else if (collabActive.value && collabIsMember.value) {
    requestCanvasReplace(captureCollabSnapshot())
  } else {
    notify('已新建空白豆板')
  }
}

function clearCanvas(): void {
  store.clearCanvas()
  notify('豆板已清空，可使用撤销恢复')
}

function changeCanvasZoom(event: Event): void {
  const nextSize = Number((event.target as HTMLInputElement).value)
  void patternCanvas.value?.setZoomFromCenter(nextSize)
}

function changeCanvasZoomPercent(event: Event): void {
  const input = event.target as HTMLInputElement
  const percent = Math.min(200, Math.max(minimumZoomPercent.value, Number(input.value) || zoomPercent.value))
  input.value = String(percent)
  void patternCanvas.value?.setZoomFromCenter(percent / 5)
}

function openClearCanvasDialog(): void {
  if (!beadCount.value) return
  clearCanvasDialog.value?.showModal()
}

function confirmClearCanvas(): void {
  clearCanvasDialog.value?.close()
  // 联机房主清空：先向后端广播清空（所有成员收到 clear 事件后整体清空共享画布），再清空本地。
  if (collabActive.value && collabIsHost.value) void collab.requestClear()
  clearCanvas()
  toolsCollapsed.value = true
}

async function confirmExport(): Promise<void> {
  if (!hasPattern.value || !selectedExportCount.value) return
  exportBusy.value = true
  try {
    // 导出库只在用户确认导出后加载，避免 ZIP/PNG 编码逻辑占用工作台首屏资源。
    const { createCsvBlob, createJsonBlob, createPngBlob, createZipBlob, downloadBlob } = await import('./exporters')
    const payload = store.exportPayload()
    // 导出文件统一按「图纸_时间戳」命名，方便按导出时间区分多份图纸。
    const now = new Date()
    const pad = (n: number): string => String(n).padStart(2, '0')
    const baseName = `图纸_${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}-${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}`

    if (isWeChat) {
      const pngBlob = exportSelection.png ? await createPngBlob(payload) : null
      const hasOtherFiles = exportSelection.xlsx || exportSelection.csv || exportSelection.json
      showWeChatExportResult(pngBlob, hasOtherFiles)
      toolsCollapsed.value = true
      return
    }

    const files: Array<{ name: string; blob: Blob }> = []

    // Excel 先由后端生成，其余格式在浏览器本地生成，再交给浏览器下载。
    const excelBlob = exportSelection.xlsx ? await exportExcel(payload) : null
    // 导出文件统一命名为「项目名称 + 后缀」。
    if (exportSelection.png) files.push({ name: `${baseName}.png`, blob: await createPngBlob(payload) })
    if (excelBlob) files.push({ name: `${baseName}.xlsx`, blob: excelBlob })
    if (exportSelection.csv) files.push({ name: `${baseName}.csv`, blob: createCsvBlob(payload) })
    // 统一工程 JSON 结构：导入/导出均使用可移植工程数据（PortableProject），来回结构一致。
    if (exportSelection.json) files.push({ name: `${baseName}.json`, blob: createJsonBlob(store.createProjectSnapshot()) })

    if (files.length === 1) {
      downloadBlob(files[0].blob, files[0].name)
    } else {
      // 浏览器会拦截同一次点击触发的多文件下载，合并为一个 ZIP 可确保所有格式都能保存下来。
      const archive = await createZipBlob(files)
      downloadBlob(archive, `${baseName}.zip`)
    }

    exportDialog.value?.close()
    toolsCollapsed.value = true
    notify(files.length === 1 ? '文件已下载到浏览器默认位置' : `已将${files.length}个文件打包下载`)
  } catch (reason) {
    notify(reason instanceof Error ? reason.message : '文件导出失败')
  } finally {
    exportBusy.value = false
  }
}

function notify(message: string, collab = false, mobileBottom = false): void {
  toast.value = message
  // 仅联机相关消息显示好友图标；普通消息无图标，避免图标一直出现。
  toastIcon.value = collab
  toastMobileBottom.value = mobileBottom
  if (toastTimer) window.clearTimeout(toastTimer)

  // Popover 会进入浏览器顶层，不会被原生 dialog 或全屏画板遮挡。
  void nextTick(() => {
    const element = toastElement.value
    if (element && !element.matches(':popover-open')) element.showPopover()
  })

  toastTimer = window.setTimeout(() => {
    const element = toastElement.value
    if (element?.matches(':popover-open')) element.hidePopover()
    toast.value = ''
    toastIcon.value = false
    toastMobileBottom.value = false
    toastTimer = 0
  }, 2600)
}

function notifyQuickTool(message: string): void {
  if (!window.matchMedia('(max-width: 820px)').matches) return
  notify(message, false, true)
}

function selectPanQuickTool(): void {
  interactionMode.value = 'pan'
  notifyQuickTool('拖拽模式：拖动画板，双指可缩放')
}

function selectPaintQuickTool(): void {
  store.selectPaintTool()
  notifyQuickTool('豆笔模式：点击或滑动画板放置豆子')
}

function selectEraserQuickTool(): void {
  store.selectEraserTool()
  notifyQuickTool('镊子模式：点击或滑动取出豆子')
}

function selectCopyColorQuickTool(): void {
  interactionMode.value = 'pick'
  notifyQuickTool('复制颜色：点击画板豆子即可复制色号')
}

// ---------- 好友联机操作 ----------
// 联机授权门槛：授权开启且有有效密钥或试用未结束时才开放联机；授权关闭（免授权模式）或试用到期时联机不可用。
function isCollabEntitled(): boolean {
  if (licensingDisabled.value) return false
  if (licensedKey.value) {
    // 有密钥：状态未加载时宽松放行，避免刚进页面时误拦截；已加载则以有效状态为准。
    if (!licenseInfo.value) return true
    return licenseInfo.value.status === 'active'
  }
  if (!trialResolved.value) return true
  return !trialExpired.value && trialRemainingGenerations.value > 0
}

async function openCollabDialog(): Promise<void> {
  // 已在联机中（查看联机状态与成员）直接打开；仅在入口处拦截授权不可用的情况。
  if (collabActive.value) {
    collabDialog.value?.showModal()
    return
  }
  // 未授权时先刷新一次授权/试用状态：后端试用被重置（如管理员清理）后前端仍可能显示耗尽，刷新后再判断避免误拦截。
  if (!isCollabEntitled()) {
    try {
      await refreshLicenseState()
    } catch {
      // 刷新失败按当前状态继续判断。
    }
  }
  if (!isCollabEntitled()) {
    // 授权关闭：功能不可用直接提示，不引导密钥弹窗。
    if (licensingDisabled.value) {
      notify('好友联机功能已关闭', true)
      return
    }
    notify('联机需要有效授权，请先获取密钥', true)
    openLicenseDialog()
    return
  }
  collabDialog.value?.showModal()
}

function closeCollabDialog(): void {
  collabDialog.value?.close()
}

function describeCollabError(reason: unknown, fallback: string): string {
  return reason instanceof Error && reason.message ? reason.message : fallback
}

async function handleHostCollab(): Promise<void> {
  // 前端授权控制：创建联机房间需要有效授权；未授权时直接拦截提示并引导获取密钥，不向后端发送请求（避免每次 402）。
  if (!isCollabEntitled()) {
    notify('创建联机房间需要有效授权，请先获取密钥', true)
    openLicenseDialog()
    return
  }
  try {
    await collab.startHost()
    notify('联机房间已创建，可分享邀请码邀请好友', true)
  } catch (reason) {
    notify(describeCollabError(reason, '发起联机失败，请稍后重试'), true)
  }
}

async function handleRefreshInvite(): Promise<void> {
  try {
    await collab.refreshInvite()
    notify('邀请码已刷新，原邀请码作废，已加入的好友不受影响', true)
  } catch (reason) {
    notify(describeCollabError(reason, '刷新邀请码失败'), true)
  }
}

async function copyInviteCode(): Promise<void> {
  const code = collabInviteCode.value
  if (!code) return
  try {
    await navigator.clipboard.writeText(code)
    notify('邀请码已复制', true)
  } catch {
    notify('复制失败，请手动复制', true)
  }
}

// 复制邀请链接（含邀请文案）：对方点击后进入网站会自动发起联机申请。
async function copyInviteLink(): Promise<void> {
  const link = collabInviteLink.value
  if (!link) return
  const message = `点开链接加入我的拼豆房间，快来和我一起拼豆吧！\n${link}`
  try {
    await navigator.clipboard.writeText(message)
    notify('邀请链接已复制，快分享给好友吧', true)
  } catch {
    notify('复制失败，请手动复制链接', true)
  }
}

// 从邀请链接进入时自动读取邀请码并发起联机申请；处理一次后清理 URL 参数，避免刷新重复申请。
function handleCollabInviteFromUrl(): void {
  if (collabActive.value) return
  const params = new URLSearchParams(window.location.search)
  const code = (params.get('invite') ?? '').trim().toUpperCase()
  params.delete('invite')
  const query = params.toString()
  const cleanUrl = window.location.pathname + (query ? `?${query}` : '')
  try { window.history.replaceState(null, '', cleanUrl) } catch { /* 忽略清理失败 */ }
  if (!code) return
  // 邀请链接同样受授权门槛约束：授权不可用时引导获取密钥，不发起联机申请。
  if (!isCollabEntitled()) {
    notify('联机需要有效授权，请先获取密钥', true)
    openLicenseDialog()
    return
  }
  collabInviteInput.value = code
  collabDialog.value?.showModal()
  void collab.join(code)
}

async function handleJoinCollab(): Promise<void> {
  await collab.join(collabInviteInput.value)
}

async function handleApprove(applyId: string): Promise<void> {
  try { await collab.approve(applyId) } catch (reason) { notify(describeCollabError(reason, '审批失败'), true) }
}

async function handleReject(applyId: string): Promise<void> {
  try { await collab.reject(applyId) } catch (reason) { notify(describeCollabError(reason, '操作失败'), true) }
}

async function handleKickMember(memberId: string): Promise<void> {
  try { await collab.kick(memberId) } catch (reason) { notify(describeCollabError(reason, '踢出失败'), true) }
}

async function handleTogglePermission(memberId: string, canEdit: boolean): Promise<void> {
  const target = !canEdit
  const me = collabRoom.value?.members.find(x => x.memberId === memberId)
  if (me) me.canEdit = target // 乐观更新；服务端广播会再次确认，失败则回滚。
  try {
    await collab.setPermission(memberId, target)
    notify(target ? '已开启编辑权限' : '已关闭编辑权限', true)
  } catch (reason) {
    if (me) me.canEdit = canEdit
    notify(describeCollabError(reason, '编辑权限设置失败'), true)
  }
}

async function handleToggleSavePermission(memberId: string, canSave: boolean): Promise<void> {
  const target = !canSave
  const me = collabRoom.value?.members.find(x => x.memberId === memberId)
  if (me) me.canSave = target // 乐观更新；服务端广播会再次确认，失败则回滚。
  try {
    await collab.setSavePermission(memberId, target)
    notify(target ? '已开启共享，该成员可保存、导出图纸' : '已关闭共享，该成员不可保存、导出图纸', true)
  } catch (reason) {
    if (me) me.canSave = canSave
    notify(describeCollabError(reason, '共享权限设置失败'), true)
  }
}

// 房主结束联机：关闭房间并恢复本地豆板（保留当前共享画布为本人数据）。
function handleEndCollab(): void {
  collabLeftManually = true
  void collab.leave().then(() => {
    collabDialog.value?.close()
    notify('联机已结束', true)
  })
}

// 联机成员退出：恢复进入联机前的本人豆板。
function handleMemberExit(): void {
  collabLeftManually = true
  void collab.leave().then(() => {
    collabDialog.value?.close()
    notify('已退出联机', true)
  })
}

// 成员点击「退出联机」：先弹出确认提示，确认后才执行退出（避免误触丢失联机数据）。
function requestMemberExit(): void {
  collabExitDialog.value?.showModal()
}

// 确认退出联机：关闭确认框并执行退出。
function confirmMemberExit(): void {
  collabExitDialog.value?.close()
  handleMemberExit()
}

// 成员申请权限（编辑 edit / 共享 save）：冷却拦截 + 提交后轻提示；审批结果提示由联机状态仓库统一处理。
function handlePermApply(perm: 'edit' | 'save'): void {
  const remain = collab.permCooldownRemaining(perm)
  if (remain > 0) {
    // 冷却期间再次点击：给出「x 秒后可再次申请」的轻提示（按钮保持可点，提示剩余冷却时间）。
    notify(perm === 'edit' ? `${remain} 秒后可再次申请编辑权限` : `${remain} 秒后可再次申请共享权限`, true)
    return
  }
  void collab.permApply(perm).then((ok) => {
    // 仅提交成功时提示；失败（服务端冷却/已有待审批等）由 store 通过 permNotice 弹出 Toast。
    if (ok) notify(perm === 'edit' ? '已向房主申请编辑权限' : '已向房主申请共享权限', true)
  })
}

// 房主审批成员权限申请（同意/拒绝）。
function handlePermDecide(applyId: string, accept: boolean): void {
  void collab.permDecide(applyId, accept)
}

// 成员替换图纸申请：确认弹窗与待提交快照（联机中替换整张图纸需房主同意）。
const replaceConfirmDialog = ref<HTMLDialogElement | null>(null)
const pendingReplaceSnapshot = ref<CollabSnapshotDto | null>(null)

// 捕获当前编辑器画布为联机快照（用于成员申请替换整张图纸；空白画布同样可捕获）。
function captureCollabSnapshot(): CollabSnapshotDto | null {
  return {
    width: store.width,
    height: store.height,
    cells: store.cells.slice(),
    colors: store.colors.map(color => ({ ...color })),
    title: store.title,
  }
}

// 成员生成/加载自家图纸后：申请替换整张联机图纸（需房主同意，1 分钟冷却 + 1 分钟有效期）。
function requestCanvasReplace(snapshot: CollabSnapshotDto | null): void {
  if (!collabActive.value || !collabIsMember.value || !snapshot) return
  const remain = collab.replaceCooldownRemaining()
  if (remain > 0) {
    // 冷却期间再次触发：给出「x 秒后可再次申请」的轻提示，并恢复联机画布。
    notify(`${remain} 秒后可再次申请替换图纸`, true)
    collab.restoreRoomCanvas()
    return
  }
  pendingReplaceSnapshot.value = snapshot
  replaceConfirmDialog.value?.showModal()
}

// 确认提交替换申请：提交后本地画布恢复为联机快照，等待房主审批（房主同意后全房间同步新图纸）。
function confirmReplaceSubmit(): void {
  const snapshot = pendingReplaceSnapshot.value
  pendingReplaceSnapshot.value = null
  replaceConfirmDialog.value?.close()
  if (snapshot) void collab.submitReplaceRequest(snapshot)
}

// 取消替换申请：恢复本地画布为联机快照（不改变共享画布）。
function cancelReplaceRequest(): void {
  pendingReplaceSnapshot.value = null
  replaceConfirmDialog.value?.close()
  collab.restoreRoomCanvas()
  notify('已保留联机当前图纸', true)
}

// 房主审批成员替换图纸申请（同意/拒绝）。
function handleReplaceDecide(applyId: string, accept: boolean): void {
  void collab.replaceDecide(applyId, accept)
}
</script>

<template>
  <div class="app-shell" :class="{ 'is-splashing': showSplash, 'is-splash-leaving': splashLeaving }">
    <SplashScreen v-if="showSplash" @leaving="beginSplashExit" @finished="finishSplash" />
    <header class="topbar" :inert="showSplash" :aria-hidden="showSplash ? 'true' : undefined">
      <button
        class="brand"
        type="button"
        aria-label="查看开发者微信二维码"
        aria-haspopup="dialog"
        @click="developerDialog?.showModal()"
      >
        <span class="brand-beads" aria-hidden="true">
          <i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i>
        </span>
        <span><b>拼了个豆</b></span>
      </button>
      <div class="top-actions">
        <button
          v-if="collabActive"
          class="studio-collaborators"
          type="button"
          :aria-expanded="collabMemberOpen"
          aria-label="查看联机成员"
          @click="toggleCollabMemberPopup"
        >
          <i
            v-for="member in (collabRoom?.members ?? []).slice(0, 4)"
            :key="member.memberId"
            :style="{ background: COLLAB_COLORS[member.colorIndex] || COLLAB_COLORS[0] }"
          >{{ member.name.slice(0, 1) }}</i>
          <em v-if="collabMemberCount > 4">+{{ collabMemberCount - 4 }}</em>
        </button>
        <span class="studio-save-indicator" :class="`is-${cloudSaveState}`" role="status">
          <AppIcon name="cloud" />{{ cloudSaveStatus }}
        </span>
        <button
          v-if="showTopLicenseEntry"
          class="license-entry-button"
          type="button"
          :class="{ 'is-trial-expired': !licensedKey && trialExpired, 'license-expired': licensedKey && (licenseInfo?.status === 'time_expired' || licenseInfo?.status === 'exhausted') }"
          aria-label="商用授权与试用状态"
          @click="openLicenseDialog"
        >
          <AppIcon name="license" />
          <template v-if="licensedKey">{{ licenseEntryLabel }}</template>
          <template v-else-if="trialExpired">试用已到期</template>
          <template v-else>试用 {{ trialRemainingLabel }}</template>
        </button>
        <button
          v-if="showTopLicenseEntry"
          class="license-entry-button mobile-license-entry-button"
          type="button"
          :class="{ 'is-trial-expired': !licensedKey && trialExpired, 'license-expired': licensedKey && (licenseInfo?.status === 'time_expired' || licenseInfo?.status === 'exhausted') }"
          aria-label="授权与试用"
          @click="openLicenseDialog"
        >
          <AppIcon name="license" />
          <span>{{ licensedKey ? licenseEntryLabel : '试用已到期' }}</span>
        </button>
        <button
          class="nav-function-toggle"
          type="button"
          :aria-expanded="!toolsCollapsed"
          :aria-label="isMobileLayout ? '更多功能' : '功能'"
          :title="isMobileLayout ? '更多' : '功能'"
          aria-controls="stage-tools"
          @click="toggleTools"
        >
          <AppIcon name="menu" />
          <span>{{ isMobileLayout ? '更多' : '功能' }}</span>
        </button>
        <button class="nav-generate-button" type="button" aria-label="打开图纸生成" @click="openSettingsDialog">
          <span class="nav-generate-label-full">生成图纸</span>
          <span class="nav-generate-label-short" aria-hidden="true">生成</span>
        </button>
      </div>
    </header>

    <main class="workspace" :inert="showSplash" :aria-hidden="showSplash ? 'true' : undefined">
      <!-- 顶部「联机中」状态条 + 下方成员列表轻弹窗 -->
      <div v-if="collabActive" class="collab-member-wrap">
        <button
          class="collab-status-bar"
          type="button"
          :aria-expanded="collabMemberOpen"
          @click="toggleCollabMemberPopup"
        >
          <span class="collab-status-dot" aria-hidden="true"></span>
          <AppIcon name="online" />
          联机中 · {{ collabMemberCount }} 人
        </button>
        <div v-if="collabMemberOpen" class="collab-popup collab-member-popup">
          <div class="collab-popup-head">
            <strong>联机成员</strong>
            <small>{{ collabMemberCount }} / 5 人</small>
          </div>
          <p v-if="collabPermMessage" class="collab-note collab-perm-note">{{ collabPermMessage }}</p>
          <ul class="collab-member-list">
            <li
              v-for="m in collabRoom?.members ?? []"
              :key="m.memberId"
              class="collab-member-item"
              :class="{ 'is-self': m.memberId === collabMyMember?.memberId }"
            >
              <i class="collab-member-color" :style="{ background: COLLAB_COLORS[m.colorIndex] || COLLAB_COLORS[0] }" aria-hidden="true"></i>
              <span class="collab-member-name">
                {{ m.name }}{{ m.memberId === collabMyMember?.memberId ? '（我）' : '' }}
              </span>
              <span v-if="m.isHost" class="collab-member-tag">房主</span>
              <span v-else-if="m.memberId === collabMyMember?.memberId" class="collab-member-tag">我的颜色</span>
              <div v-if="collabIsHost && !m.isHost" class="collab-member-controls">
                <label
                  class="collab-perm-check"
                  :class="{ 'is-on': m.canEdit }"
                  :title="m.canEdit ? '该成员可以编辑豆板' : '该成员仅能查看豆板'"
                >
                  <input
                    type="checkbox"
                    :checked="m.canEdit"
                    :aria-label="`${m.name} 编辑权限`"
                    @change="handleTogglePermission(m.memberId, m.canEdit)"
                  />
                  <span>编辑</span>
                </label>
                <label
                  class="collab-perm-check"
                  :class="{ 'is-on': m.canSave }"
                  :title="m.canSave ? '该成员可以保存、导出图纸' : '该成员不可保存、导出图纸'"
                >
                  <input
                    type="checkbox"
                    :checked="m.canSave"
                    :aria-label="`${m.name} 共享权限`"
                    @change="handleToggleSavePermission(m.memberId, m.canSave)"
                  />
                  <span>共享</span>
                </label>
                <button type="button" class="collab-kick-btn" title="将好友踢出联机房间" @click="handleKickMember(m.memberId)"><AppIcon name="kick" />踢出</button>
              </div>
              <!-- 自己：仅保留退出联机入口（申请编辑/申请共享已移至左侧工具栏，不在此重复） -->
              <div v-if="m.memberId === collabMyMember?.memberId && !collabIsHost" class="collab-member-self-actions">
                <button class="collab-exit-mini-btn" type="button" title="退出联机房间" @click="requestMemberExit">
                  <AppIcon class="collab-exit-mini-icon" name="exit" />
                  退出
                </button>
              </div>
            </li>
          </ul>
          <button class="collab-popup-detail-btn" type="button" @click="openCollabDialog"><AppIcon name="info" />联机详情与邀请码</button>
        </div>
      </div>
      <dialog
        ref="licenseDialog"
        class="license-dialog"
        aria-labelledby="license-dialog-title"
      >
        <div class="license-dialog-shell">
          <header class="workspace-dialog-header">
            <div>
              <small>授权与试用</small>
              <h2 id="license-dialog-title">密钥授权</h2>
            </div>
            <button type="button" aria-label="关闭密钥窗口" title="关闭" @click="licenseDialog?.close()"><AppIcon name="close" /></button>
          </header>
          <div class="license-dialog-content">
            <template v-if="licensedKey && licenseInfo">
              <p v-if="licenseInfo.status === 'time_expired'" class="license-status-note warning">使用期限已到，豆板编辑已锁定；仍可生成图纸、查看熨烫效果与导出</p>
              <p v-else-if="licenseInfo.status === 'exhausted'" class="license-status-note warning">生成次数已用完，豆板编辑已锁定，请联系获取新密钥</p>
              <p v-else class="license-status-note">密钥状态正常，可正常生成图纸</p>
              <dl class="license-status-list">
                <div><dt>剩余时长</dt><dd>{{ licenseInfo.status === 'time_expired' ? '已用完' : formatDuration(licenseRemainingSeconds) }}</dd></div>
                <div><dt>剩余生成次数</dt><dd>{{ licenseInfo.remainingCount }} 次</dd></div>
                <div><dt>绑定设备</dt><dd>本机（一密钥一设备）</dd></div>
              </dl>
            </template>
            <template v-else>
              <div class="license-trial-status" :class="{ expired: trialExpired }">
                <strong>{{ trialExpired ? '免费试用已结束' : '免费试用中' }}</strong>
                <p v-if="!trialExpired">试用剩余时间 {{ trialRemainingLabel }} · 还可生成 {{ trialRemainingGenerations }} 次</p>
                <p v-else>试用时长或生成次数已用完，激活密钥后可继续使用。</p>
              </div>
              <label class="field">
                <span>激活密钥</span>
                <input :value="licenseInput" :placeholder="loginLockRemaining > 0 ? `已锁定，${loginLockRemaining} 秒后重试` : '请输入已购买的授权密钥'" autocomplete="off" :disabled="loginLockRemaining > 0" @input="onLicenseInput" @keydown.enter="activateLicense" />
              </label>
            </template>
          </div>
          <footer class="workspace-dialog-actions license-dialog-actions">
            <button class="secondary" type="button" @click="requestLicense"><AppIcon name="get-key" />获取密钥</button>
            <div>
              <button v-if="licensedKey && licenseInfo" class="secondary" type="button" @click="deactivateLicense"><AppIcon name="remove-key" />移除密钥</button>
              <button v-else type="button" :disabled="licenseVerifying || loginLockRemaining > 0" @click="activateLicense"><AppIcon name="activate-key" />{{ loginLockRemaining > 0 ? '激活密钥 ' + loginLockRemaining + 's' : (licenseVerifying ? '校验中…' : '激活密钥') }}</button>
              <button type="button" @click="licenseDialog?.close()"><AppIcon name="close" />关闭</button>
            </div>
          </footer>
        </div>
      </dialog>

      <dialog
        ref="syncCloudDialog"
        class="license-dialog"
        aria-labelledby="sync-confirm-title"
        @cancel.prevent
      >
        <div class="license-dialog-shell">
          <header class="workspace-dialog-header">
            <div>
              <small>云端存档</small>
              <h2 id="sync-confirm-title">是否同步云端数据？</h2>
            </div>
          </header>
          <div class="license-dialog-content">
            <p>检测到该账号已有云端存档。同步后将以下载的云端豆板覆盖当前内容，当前拼豆数据将被清空。</p>
            <p>选择「保留本地」则继续使用当前豆板，并会覆盖云端存档。</p>
          </div>
          <footer class="workspace-dialog-actions license-dialog-actions">
            <button class="secondary" type="button" @click="declineSyncCloud"><AppIcon name="local" />保留本地</button>
            <button type="button" @click="confirmSyncCloud"><AppIcon name="cloud" />同步云端</button>
          </footer>
        </div>
      </dialog>

      <dialog
        ref="weChatBrowserDialog"
        class="export-dialog wechat-entry-dialog"
        aria-labelledby="wechat-entry-dialog-title"
        @click="closeDialogFromBackdrop($event, weChatBrowserDialog)"
      >
        <div class="export-dialog-shell">
          <header class="export-dialog-header">
            <div><h2 id="wechat-entry-dialog-title">建议使用系统浏览器</h2></div>
            <button type="button" aria-label="关闭系统浏览器提示" title="关闭" :disabled="systemBrowserPreparing" @click="weChatBrowserDialog?.close()"><AppIcon name="close" /></button>
          </header>

          <div class="wechat-entry-content">
            <div class="wechat-entry-badge" aria-hidden="true">↗</div>
            <div>
              <strong>当前正在微信内打开</strong>
              <p>系统浏览器对文件下载、全屏横屏和本地草稿保存的支持更完整，建议切换后继续使用。</p>
            </div>
            <div class="wechat-browser-instructions" role="status">
              <strong>{{ showSystemBrowserGuide ? '如果没有自动跳转' : '如何打开' }}</strong>
              <span>点击下方按钮尝试跳转；如果没有反应，请点击微信右上角“…” → “在浏览器打开”。</span>
              <small v-if="systemBrowserAddress">图纸和参数已加入接力链接，切换浏览器后会自动恢复。</small>
            </div>
            <label v-if="systemBrowserAddress" class="wechat-manual-address">
              <span>接力地址（复制受限时请长按）</span>
              <textarea
                :value="systemBrowserAddress"
                readonly
                rows="2"
                aria-label="系统浏览器接力地址"
                @focus="selectSystemBrowserAddress"
                @click="selectSystemBrowserAddress"
              ></textarea>
            </label>
          </div>

          <footer class="export-dialog-actions wechat-entry-actions">
            <span>无法自动跳转时，可复制地址后粘贴到浏览器</span>
            <div>
              <button type="button" :disabled="systemBrowserPreparing" @click="weChatBrowserDialog?.close()"><AppIcon name="close" />留在微信</button>
              <button v-if="systemBrowserAddress" type="button" :disabled="systemBrowserPreparing" @click="copySystemBrowserAddress"><AppIcon name="copy" />复制地址</button>
              <button class="confirm" type="button" :disabled="systemBrowserPreparing" @click="openInSystemBrowser">
                <AppIcon name="browser" />
                {{ systemBrowserPreparing ? '正在准备…' : '尝试打开系统浏览器' }}
              </button>
            </div>
          </footer>
        </div>
      </dialog>

      <dialog
        ref="developerDialog"
        class="developer-dialog"
        aria-labelledby="developer-dialog-title"
        @click="closeDialogFromBackdrop($event, developerDialog)"
      >
        <div class="developer-dialog-shell">
          <header class="workspace-dialog-header">
            <div>
              <h2 id="developer-dialog-title">开发者微信</h2>
              <small>微信号：{{ developerContact.weChatId || '暂未配置' }}</small>
            </div>
            <button type="button" aria-label="关闭开发者微信窗口" title="关闭" @click="developerDialog?.close()"><AppIcon name="close" /></button>
          </header>
          <div class="developer-dialog-content">
            <img
              v-if="developerContact.qrCodeUrl"
              :src="developerContact.qrCodeUrl"
              alt="开发者微信二维码"
              width="600"
              height="600"
            />
            <p v-else class="developer-contact-empty">暂未配置微信二维码</p>
            <p>长按二维码添加微信咨询</p>
            <p class="developer-version">版本号 {{ applicationVersion }}</p>
          </div>
          <footer class="workspace-dialog-actions developer-dialog-actions">
            <button class="secondary" type="button" @click="developerDialog?.close()"><AppIcon name="close" />关闭</button>
            <button class="secondary" type="button" :disabled="!developerContact.weChatId" @click="copyDeveloperWeChat(false)"><AppIcon name="copy" />复制微信号</button>
            <button type="button" :disabled="!developerContact.weChatId" @click="copyDeveloperWeChat(true)"><AppIcon name="wechat" />复制并打开微信</button>
          </footer>
        </div>
      </dialog>

      <dialog
        ref="settingsDialog"
        class="settings-dialog"
        :class="{ 'is-docked-generation': generationPanelDocked }"
        aria-labelledby="settings-dialog-title"
        @click="closeDialogFromBackdrop($event, settingsDialog)"
        @close="handleGenerationPanelClose"
      >
        <div class="settings-dialog-shell">
          <header class="workspace-dialog-header generation-panel-header">
            <div>
              <small>四步完成图纸</small>
              <h2 id="settings-dialog-title">图纸生成</h2>
            </div>
            <button type="button" aria-label="关闭图纸生成面板" title="关闭" @click="closeGenerationPanel"><AppIcon name="close" /></button>
          </header>
          <nav class="generation-stepper" aria-label="图纸生成步骤">
            <button
              v-for="item in generationSteps"
              :key="item.step"
              type="button"
              :class="{ active: generationStep === item.step, complete: generationStep > item.step }"
              :aria-current="generationStep === item.step ? 'step' : undefined"
              :title="item.label"
              @click="setGenerationStep(item.step)"
            >
              <span>{{ item.step }}</span><b>{{ item.short }}</b>
            </button>
          </nav>

          <div class="generation-pages">
            <section v-show="generationStep === 1" class="generation-page upload-section" :hidden="generationStep !== 1">
              <div class="generation-page-heading"><span>01</span><div><h3>上传图片</h3><p>支持 JPG、PNG、WEBP，单张不超过 15MB</p></div></div>
              <button
                class="dropzone"
                :class="{ dragging, 'has-image': previewUrl }"
                type="button"
                @click="fileInput?.click()"
                @dragenter.prevent="dragging = true"
                @dragover.prevent="dragging = true"
                @dragleave.prevent="dragging = false"
                @drop.prevent="onDrop"
              >
                <img v-if="previewUrl" :src="previewUrl" alt="待生成原图预览" />
                <template v-else>
                  <AppIcon class="upload-icon" name="upload-image" />
                  <b>拖入图片或点击上传</b>
                  <small>稍后可裁剪构图</small>
                </template>
                <span
                  v-if="previewUrl"
                  class="remove-image-btn"
                  role="button"
                  title="删除图片"
                  aria-label="删除图片"
                  @click.stop="clearUploadedImage"
                ><AppIcon name="close" /></span>
              </button>
              <input ref="fileInput" class="visually-hidden" type="file" accept="image/jpeg,image/png,image/webp" aria-label="选择需要生成图纸的原图" @change="onFileInputChange" />
              <p class="generation-page-tip"><AppIcon name="info" />不上传图片也可以继续，最后选择创建空白画布。</p>
            </section>

            <section v-show="generationStep === 2" class="generation-page" :hidden="generationStep !== 2">
              <div class="generation-page-heading"><span>02</span><div><h3>选择豆子</h3><p>选择手头豆子的品牌、色卡和匹配底板</p></div></div>
              <label class="field">
                <span>品牌 / 厂商</span>
                <select data-validation-key="brand" :value="generationDraft.brandId" @change="changeBrand">
                  <option v-for="brand in brands" :key="brand.id" :value="brand.id">{{ brand.heatRank }}. {{ brand.name }}</option>
                </select>
              </label>
              <label class="field">
                <span>色卡版本</span>
                <select data-validation-key="palette" :value="generationDraft.paletteId" @change="changePalette">
                  <option v-for="item in generationBrand?.palettes || []" :key="item.id" :value="item.id">{{ item.name }} · {{ item.colorCount }}色</option>
                </select>
              </label>
              <div v-if="generationPaletteSummary" class="info-note">
                <span :class="generationPaletteSummary.verified ? 'verified' : 'community'">{{ generationPaletteSummary.verified ? '标准版' : '公开对照版' }}</span>
                {{ generationPaletteSummary.note }}
              </div>
              <label class="field">
                <span>实际使用底板</span>
                <select data-validation-key="board" v-model="generationDraft.boardId">
                  <option v-for="board in generationCompatibleBoards" :key="board.id" :value="board.id">{{ board.name }}</option>
                </select>
              </label>
              <p v-if="generationSizeMismatch" class="warning-note">当前色卡规格与底板豆径不一致，请更换兼容底板。</p>
            </section>

            <section v-show="generationStep === 3" class="generation-page" :hidden="generationStep !== 3">
              <div class="generation-page-heading"><span>03</span><div><h3>设置图纸</h3><p>按成品尺寸与清晰度配置豆板参数</p></div></div>
              <span class="field-label">常用尺寸</span>
              <div class="preset-row">
                <button v-for="size in gridPresets" :key="size" type="button" :class="{ active: generationDraft.width === size && generationDraft.height === size }" @click="applyGridPreset(size)">{{ size }}</button>
              </div>
              <div class="two-fields">
                <label class="field"><span>横向颗数</span><input data-validation-key="width" v-model.number="generationDraft.width" type="number" min="8" max="160" @change="clampGenerationMaxColors" /></label>
                <label class="field"><span>纵向颗数</span><input data-validation-key="height" v-model.number="generationDraft.height" type="number" min="8" max="160" @change="clampGenerationMaxColors" /></label>
              </div>
              <small class="field-help grid-size-help">格子越多细节越清晰，同时需要更多豆子和处理时间。</small>
              <label class="field range-field">
                <span><b>最多颜色</b><output>{{ generationDraft.maxColors }}色</output></span>
                <input data-validation-key="maxColors" v-model.number="generationDraft.maxColors" type="range" min="2" :max="Math.min(96, generationPaletteSummary?.colorCount || 96, Math.max(2, generationDraft.width * generationDraft.height))" />
                <small class="field-help">限制最多使用的色号种类，不是豆子总颗数。</small>
              </label>
              <label class="field range-field">
                <span><b>杂色抑制</b><output>{{ generationNoiseSuppressionLabel }}</output></span>
                <input data-validation-key="noiseSuppression" v-model.number="generationDraft.noiseSuppression" type="range" min="0" max="3" step="1" />
                <small class="field-help">合并孤立色点；照片建议使用“标准”。</small>
              </label>
              <label class="switch-row"><span><b>误差扩散</b><small>照片渐变更自然</small></span><input v-model="generationDraft.dither" type="checkbox" /></label>
              <label class="switch-row"><span><b>自动去背景</b><small>从四角识别连续背景</small></span><input v-model="generationDraft.removeBackground" type="checkbox" /></label>
              <label v-if="generationDraft.removeBackground" class="field range-field">
                <span><b>背景容差</b><output>{{ generationDraft.backgroundThreshold }}</output></span>
                <input data-validation-key="backgroundThreshold" v-model.number="generationDraft.backgroundThreshold" type="range" min="2" max="30" />
                <small class="field-help">主体边缘被误删时请调低。</small>
              </label>
            </section>

            <section v-show="generationStep === 4" class="generation-page generation-create-page" :hidden="generationStep !== 4">
              <div class="generation-page-heading"><span>04</span><div><h3>选择创建方式</h3><p>参数已就绪，选择本次要创建的内容</p></div></div>
              <div class="generation-summary-card">
                <div><span>豆子</span><b>{{ generationBrand?.name || '未选择' }} · {{ generationPaletteSummary?.name || '未选择' }}</b></div>
                <div><span>图纸</span><b>{{ generationDraft.width }} × {{ generationDraft.height }} · 最多 {{ generationDraft.maxColors }} 色</b></div>
                <div><span>原图</span><b>{{ imageFile?.name || '暂未上传' }}</b></div>
              </div>
              <div class="generation-choice-list">
                <article :class="{ disabled: !imageFile }">
                  <i><AppIcon name="upload-image" /></i>
                  <div><b>从图片生成</b><span>{{ imageFile ? '将原图转换为可编辑拼豆图纸' : '需要先上传并完成裁剪' }}</span></div>
                </article>
                <article>
                  <i><AppIcon name="grid" /></i>
                  <div><b>创建空白画布</b><span>保留以上豆子与尺寸参数，从空白豆板开始</span></div>
                </article>
              </div>
            </section>
          </div>
          <footer class="workspace-dialog-actions generation-flow-actions">
            <button v-if="generationStep > 1" class="secondary generation-back-button" type="button" @click="previousGenerationStep"><AppIcon name="cancel" />上一步</button>
            <span v-else>可随时关闭，不会修改当前画布</span>
            <button v-if="generationStep < 4" class="generation-next-button" type="button" @click="advanceGenerationStep">
              下一步<AppIcon name="chevron-right" />
            </button>
            <div v-else class="generation-create-actions">
              <button class="secondary blank-canvas-button" type="button" :disabled="loading || generationSubmitting" @click="openGenerateConfirmDialog('blank')"><AppIcon name="grid" />空白画布</button>
              <button class="confirm image-generate-button" type="button" :disabled="loading || generationSubmitting || !imageFile" @click="openGenerateConfirmDialog('image')"><span>{{ loading || generationSubmitting ? '生成中…' : '生成图纸' }}</span></button>
            </div>
          </footer>
        </div>
      </dialog>

      <dialog
        ref="cropDialog"
        class="crop-dialog"
        aria-labelledby="crop-dialog-title"
        @close="handleCropDialogClose"
        @click="closeDialogFromBackdrop($event, cropDialog)"
      >
        <div class="crop-dialog-shell">
          <header class="workspace-dialog-header">
            <div>
              <small>{{ width }}×{{ height }} 图纸比例</small>
              <h2 id="crop-dialog-title">裁剪上传图片</h2>
            </div>
            <button type="button" aria-label="关闭裁剪窗口" title="关闭" :disabled="cropBusy" @click="cropDialog?.close()"><AppIcon name="close" /></button>
          </header>

          <div class="crop-dialog-content">
            <p>默认完整显示原图；拖动可调整位置，缩小后的空白区域为透明，不会生成豆子。</p>
            <div class="crop-viewport-wrap">
              <div
                ref="cropViewport"
                class="crop-viewport"
                :style="cropViewportStyle"
                @pointerdown="beginCropDrag"
                @pointermove="moveCropImage"
                @pointerup="endCropDrag"
                @pointercancel="endCropDrag"
                @wheel.prevent="zoomCropFromWheel"
              >
                <img
                  v-if="cropSourceUrl"
                  ref="cropImage"
                  :src="cropSourceUrl"
                  :style="cropImageStyle"
                  alt="待裁剪原图"
                  draggable="false"
                />
                <span class="crop-grid" aria-hidden="true"></span>
              </div>
            </div>
            <label class="crop-zoom-control">
              <span>缩放</span>
              <input
                :value="cropZoom"
                type="range"
                min="1"
                max="4"
                step="0.01"
                @input="updateCropZoom(Number(($event.target as HTMLInputElement).value))"
              />
              <output>{{ Math.round(cropZoom * 100) }}%</output>
            </label>
          </div>

          <footer class="workspace-dialog-actions crop-dialog-actions">
            <span>透明区域不会匹配色号，原图不会被修改</span>
            <div>
              <button class="secondary" type="button" :disabled="cropBusy" @click="cropDialog?.close()"><AppIcon name="cancel" />取消</button>
              <button type="button" :disabled="cropBusy" @click="confirmCrop">
                <AppIcon name="crop" />
                {{ cropBusy ? '正在裁剪…' : '确认裁剪并上传' }}
              </button>
            </div>
          </footer>
        </div>
      </dialog>

      <dialog
        ref="generateConfirmDialog"
        class="generation-confirm-dialog"
        aria-labelledby="generation-confirm-title"
        @close="handleGenerateConfirmClose"
      >
        <div class="generation-confirm-shell">
          <header class="workspace-dialog-header">
            <div>
              <small>{{ generationTarget === 'image' ? '从图片生成' : '创建空白画布' }}</small>
              <h2 id="generation-confirm-title">确认创建</h2>
            </div>
            <button type="button" aria-label="关闭参数确认窗口" title="关闭" @click="generateConfirmDialog?.close()"><AppIcon name="close" /></button>
          </header>
          <div class="generation-confirm-content">
            <p class="generation-confirm-intro">请确认以下配置与实际使用的拼豆和底板一致，确认后将替换当前画布。</p>
            <dl class="generation-parameter-list">
              <div><dt>创建方式</dt><dd>{{ generationTarget === 'image' ? '从图片生成图纸' : '创建空白画布' }}</dd></div>
              <div v-if="generationTarget === 'image'"><dt>原图</dt><dd>{{ imageFile?.name || '未上传' }}</dd></div>
              <div><dt>厂家</dt><dd>{{ generationBrand?.name || '未选择' }}</dd></div>
              <div><dt>色卡</dt><dd>{{ generationPaletteSummary ? `${generationPaletteSummary.name} · ${generationPaletteSummary.colorCount}色` : '未选择' }}</dd></div>
              <div><dt>底板</dt><dd>{{ generationBoard?.name || '未选择' }}（{{ generationBoardSummary }}）</dd></div>
              <div><dt>图纸尺寸</dt><dd>{{ generationDraft.width }} × {{ generationDraft.height }}，共 {{ generationDraft.width * generationDraft.height }} 格</dd></div>
              <div><dt>最多颜色</dt><dd>{{ generationDraft.maxColors }} 色</dd></div>
              <div><dt>图像处理</dt><dd>{{ generationDraft.dither ? '误差扩散' : '不扩散' }} · 杂色抑制{{ generationNoiseSuppressionLabel }} · {{ generationDraft.removeBackground ? `去背景（容差 ${generationDraft.backgroundThreshold}）` : '保留背景' }}</dd></div>
            </dl>
            <p v-if="generationSizeMismatch" class="generation-confirm-warning">当前色卡规格与底板豆径不一致，必须更换为兼容底板后才能生成。</p>
            <div v-if="generationValidationErrors.length" class="generation-validation-errors" role="alert">
              <strong>还有参数需要调整：</strong>
              <ul>
                <li v-for="issue in generationValidationIssues" :key="`${issue.key}-${issue.message}`">
                  <button type="button" @click="focusGenerationIssue(issue.key)"><AppIcon name="locate" />{{ issue.message }} <span>去修改 <AppIcon name="chevron-right" /></span></button>
                </li>
              </ul>
            </div>
          </div>
          <footer class="workspace-dialog-actions generation-confirm-actions">
            <span>{{ generationValidationErrors.length ? '请先返回修改有误参数' : '参数检查完成，可以创建' }}</span>
            <div>
              <button class="secondary" type="button" @click="returnToGenerationIssue"><AppIcon :name="generationValidationErrors.length ? 'locate' : 'cancel'" />{{ generationValidationErrors.length ? '定位第一个问题' : '返回修改' }}</button>
              <button type="button" :disabled="loading || generationSubmitting || generationValidationErrors.length > 0" @click="generate">
                <AppIcon :name="generationTarget === 'image' ? 'generate' : 'grid'" />
                {{ loading || generationSubmitting ? '正在生成…' : generationTarget === 'image' ? '确认生成图纸' : '确认创建空白画布' }}
              </button>
            </div>
          </footer>
        </div>
      </dialog>

      <dialog ref="saveDialog" class="materials-dialog" aria-labelledby="save-dialog-title" @click="closeDialogFromBackdrop($event, saveDialog)">
        <div class="materials-dialog-shell">
          <header class="workspace-dialog-header">
            <div>
              <small>{{ licensedKey ? '保存到云端图纸库' : '保存到本地图纸库' }}</small>
              <h2 id="save-dialog-title">保存图纸</h2>
            </div>
            <button type="button" aria-label="关闭保存窗口" title="关闭" @click="saveDialog?.close()"><AppIcon name="close" /></button>
          </header>
          <div class="save-dialog-content">
            <label class="save-name-field">
              <span>图纸名称</span>
              <input v-model="saveName" type="text" maxlength="40" placeholder="输入图纸名称" @keydown.enter="confirmSave" />
            </label>
            <p class="save-dialog-hint">{{ licensedKey ? '将保存到当前密钥的云端图纸库，可在任意设备登录后打开。' : '将保存到当前浏览器本地，可保存多份图纸。' }}</p>
          </div>
          <footer class="workspace-dialog-actions">
            <span>{{ width }}×{{ height }} · {{ beadCount }} 颗豆</span>
            <div>
              <button class="secondary" type="button" @click="saveDialog?.close()"><AppIcon name="cancel" />取消</button>
              <button type="button" @click="confirmSave"><AppIcon name="save" />确认保存</button>
            </div>
          </footer>
        </div>
      </dialog>

      <dialog ref="boardExpandDialog" class="materials-dialog" aria-labelledby="board-expand-title" @click="closeDialogFromBackdrop($event, boardExpandDialog)">
        <div class="materials-dialog-shell">
          <header class="workspace-dialog-header">
            <div>
              <small>只增不减，上限 160×160</small>
              <h2 id="board-expand-title">拼接豆板</h2>
            </div>
            <button type="button" aria-label="关闭拼接豆板窗口" title="关闭" @click="boardExpandDialog?.close()"><AppIcon name="close" /></button>
          </header>
          <div class="board-expand-content">
            <p class="board-expand-current">当前豆板：{{ width }} × {{ height }}</p>
            <div class="board-expand-fields">
              <label>
                <span>宽度</span>
                <input v-model.number="expandWidth" type="number" :min="width" :max="160" step="1" />
              </label>
              <label>
                <span>高度</span>
                <input v-model.number="expandHeight" type="number" :min="height" :max="160" step="1" />
              </label>
            </div>
            <div class="board-expand-presets">
              <span>常用规格：</span>
              <button v-for="size in boardExpandPresets" :key="size" type="button" :disabled="size < width || size < height" @click="expandWidth = Math.max(expandWidth, size); expandHeight = Math.max(expandHeight, size)">
                {{ size }}×{{ size }}
              </button>
            </div>
            <p class="board-expand-hint">扩大后原豆子图案保留在左上角，新区域为空白，不影响已有图案。</p>
          </div>
          <footer class="workspace-dialog-actions">
            <span>目标规格：{{ Math.max(width, expandWidth || width) }} × {{ Math.max(height, expandHeight || height) }}</span>
            <div>
              <button class="secondary" type="button" @click="boardExpandDialog?.close()"><AppIcon name="cancel" />取消</button>
              <button type="button" @click="confirmBoardExpand"><AppIcon name="stitch" />确认拼接</button>
            </div>
          </footer>
        </div>
      </dialog>

      <dialog ref="libraryDialog" class="materials-dialog" aria-labelledby="library-dialog-title" @click="closeDialogFromBackdrop($event, libraryDialog)">
        <div class="materials-dialog-shell">
          <header class="workspace-dialog-header">
            <div>
              <small>{{ licensedKey ? '云端图纸库' : '本地图纸库' }}</small>
              <h2 id="library-dialog-title">我的图纸</h2>
            </div>
            <button type="button" aria-label="关闭我的图纸窗口" title="关闭" @click="libraryDialog?.close()"><AppIcon name="close" /></button>
          </header>
          <div class="library-dialog-content">
            <p v-if="libraryLoading" class="library-empty">正在读取图纸…</p>
            <p v-else-if="libraryList.length === 0" class="library-empty">还没有保存的图纸，先在工具栏点击「保存」保存当前图纸。</p>
            <ul v-else class="library-list">
              <li v-for="item in libraryList" :key="item.id" class="library-row" :class="{ 'is-selected': selectedLibraryIds.includes(item.id) }">
                <input type="checkbox" class="library-check" :checked="selectedLibraryIds.includes(item.id)" @change="toggleLibrarySelect(item.id)" :aria-label="`选择 ${item.name}`" />
                <button type="button" class="library-item" @click="requestOpenSavedProject(item)">
                  <span class="library-item-name">{{ item.name }}</span>
                  <span class="library-item-meta">{{ item.project.width }}×{{ item.project.height }} · {{ item.project.cells.filter(v => v >= 0).length }} 颗 · {{ formatSavedAt(item.savedAt) }}</span>
                </button>
                <button type="button" class="library-rename-btn" title="重命名" aria-label="重命名" @click.stop="requestRenameSavedProject(item)">
                  <AppIcon name="rename" />
                </button>
                <button type="button" class="library-delete-btn" title="删除" aria-label="删除" @click.stop="requestDeleteSavedProject(item)">
                  <AppIcon name="delete" />
                </button>
              </li>
            </ul>
          </div>
          <footer class="workspace-dialog-actions">
            <span>点击图纸可同步到当前豆板</span>
            <div>
              <button v-if="selectedLibraryIds.length" class="danger danger-confirm" type="button" @click="requestDeleteSelected"><AppIcon name="delete" />批量删除（{{ selectedLibraryIds.length }}）</button>
              <button class="secondary" type="button" @click="libraryDialog?.close()"><AppIcon name="close" />关闭</button>
            </div>
          </footer>
        </div>
      </dialog>

      <!-- 图纸重命名对话框 -->
      <dialog ref="renameDialog" class="materials-dialog" aria-labelledby="rename-dialog-title" @click="closeDialogFromBackdrop($event, renameDialog)">
        <div class="materials-dialog-shell">
          <header class="workspace-dialog-header">
            <div>
              <small>我的图纸</small>
              <h2 id="rename-dialog-title">重命名图纸</h2>
            </div>
            <button type="button" aria-label="关闭重命名窗口" title="关闭" @click="renameDialog?.close()"><AppIcon name="close" /></button>
          </header>
          <div class="library-dialog-content">
            <!-- 复用系统「保存图纸」的名称输入框样式，保证风格统一。 -->
            <label class="save-name-field">
              <span>图纸名称</span>
              <input :value="renameInput" autocomplete="off" maxlength="60" placeholder="请输入新的图纸名称" @input="renameInput = ($event.target as HTMLInputElement).value" @keydown.enter="confirmRenameSavedProject" />
            </label>
            <p v-if="renameTarget" class="library-rename-note">名称不能与其他图纸重复。</p>
          </div>
          <footer class="workspace-dialog-actions">
            <button class="secondary" type="button" @click="renameDialog?.close()"><AppIcon name="cancel" />取消</button>
            <button type="button" @click="confirmRenameSavedProject"><AppIcon name="rename" />确认重命名</button>
          </footer>
        </div>
      </dialog>

      <dialog ref="importDialog" class="materials-dialog" aria-labelledby="import-dialog-title" @click="closeDialogFromBackdrop($event, importDialog)">
        <div class="materials-dialog-shell">
          <header class="workspace-dialog-header">
            <div>
              <small>工程文件</small>
              <h2 id="import-dialog-title">导入工程文件</h2>
            </div>
            <button type="button" aria-label="关闭导入窗口" title="关闭" @click="importDialog?.close()"><AppIcon name="close" /></button>
          </header>
          <div class="materials-content">
            <p class="generation-confirm-intro">选择导出的Json工程文件，校验通过后即可导入。</p>
            <button
              class="dropzone import-dropzone"
              :class="{ dragging: importDragging }"
              type="button"
              @click="importFileInput?.click()"
              @dragenter.prevent="importDragging = true"
              @dragover.prevent="importDragging = true"
              @dragleave.prevent="importDragging = false"
              @drop.prevent="onImportDrop"
            >
              <AppIcon class="upload-icon" name="import" />
              <b>拖入JSON文件或点击上传</b>
              <small>支持 .json 格式的工程文件</small>
            </button>
            <input ref="importFileInput" class="visually-hidden" type="file" accept=".json,application/json" aria-label="选择需要导入的图纸工程文件" @change="onImportFileChange" />
            <p v-if="importFileName" class="import-file-ok">已选择：{{ importFileName }}（校验通过）</p>
            <p v-if="importFileError" class="import-file-error">{{ importFileError }}</p>
          </div>
          <footer class="workspace-dialog-actions">
            <div>
              <button class="secondary" type="button" @click="importDialog?.close()"><AppIcon name="cancel" />取消</button>
              <button type="button" :disabled="!pendingImportProject || importBusy" @click="requestImportProject"><AppIcon name="import" />确认导入</button>
            </div>
          </footer>
        </div>
      </dialog>

      <dialog ref="confirmImportDialog" class="generation-confirm-dialog" aria-labelledby="confirm-import-title">
        <div class="generation-confirm-shell">
          <header class="workspace-dialog-header">
            <div>
              <small>导入工程文件</small>
              <h2 id="confirm-import-title">确认导入</h2>
            </div>
            <button type="button" aria-label="关闭确认窗口" title="关闭" @click="confirmImportDialog?.close()"><AppIcon name="close" /></button>
          </header>
          <div class="generation-confirm-content">
            <p class="generation-confirm-intro">是否导入所选工程文件？当前豆板数据将被覆盖（可使用撤销恢复）。</p>
          </div>
          <footer class="workspace-dialog-actions">
            <span>导入后可通过撤销恢复当前内容</span>
            <div>
              <button class="secondary" type="button" @click="confirmImportDialog?.close()"><AppIcon name="cancel" />取消</button>
              <button type="button" @click="confirmImportProject"><AppIcon name="import" />确认导入</button>
            </div>
          </footer>
        </div>
      </dialog>

      <dialog ref="confirmOpenDialog" class="generation-confirm-dialog" aria-labelledby="confirm-open-title">
        <div class="generation-confirm-shell">
          <header class="workspace-dialog-header">
            <div>
              <small>打开新图纸</small>
              <h2 id="confirm-open-title">确认打开</h2>
            </div>
            <button type="button" aria-label="关闭确认窗口" title="关闭" @click="confirmOpenDialog?.close()"><AppIcon name="close" /></button>
          </header>
          <div class="generation-confirm-content">
            <p class="generation-confirm-intro">是否打开新图纸「{{ pendingOpenProject?.name }}」？当前豆板数据将被替换（可使用撤销恢复）。</p>
          </div>
          <footer class="workspace-dialog-actions">
            <span>打开后可通过撤销恢复当前内容</span>
            <div>
              <button class="secondary" type="button" @click="confirmOpenDialog?.close()"><AppIcon name="cancel" />取消</button>
              <button type="button" @click="confirmOpenSavedProject"><AppIcon name="open" />确认打开</button>
            </div>
          </footer>
        </div>
      </dialog>

      <!-- 联机成员替换图纸申请：生成/加载自家图纸后，需向房主申请替换整张联机图纸 -->
      <dialog ref="replaceConfirmDialog" class="generation-confirm-dialog" aria-labelledby="replace-confirm-title" @cancel.prevent="cancelReplaceRequest">
        <div class="generation-confirm-shell">
          <header class="workspace-dialog-header">
            <div>
              <small>替换联机图纸</small>
              <h2 id="replace-confirm-title">申请替换当前图纸</h2>
            </div>
            <button type="button" aria-label="关闭替换确认窗口" title="关闭" @click="cancelReplaceRequest"><AppIcon name="close" /></button>
          </header>
          <div class="generation-confirm-content">
            <p class="generation-confirm-intro">你正在生成/使用自己的图纸替换整张联机画布。需向房主申请，房主同意后房主与所有成员同步更新图纸；申请 1 分钟内有效，超时自动作废。</p>
          </div>
          <footer class="workspace-dialog-actions">
            <span>同一成员 1 分钟内不可重复申请</span>
            <div>
              <button class="secondary" type="button" @click="cancelReplaceRequest"><AppIcon name="cancel" />取消</button>
              <button type="button" @click="confirmReplaceSubmit"><AppIcon name="replace-color" />申请替换</button>
            </div>
          </footer>
        </div>
      </dialog>

      <dialog ref="confirmDeleteDialog" class="generation-confirm-dialog" aria-labelledby="confirm-delete-title">
        <div class="generation-confirm-shell">
          <header class="workspace-dialog-header">
            <div>
              <small>删除图纸</small>
              <h2 id="confirm-delete-title">确认删除</h2>
            </div>
            <button type="button" aria-label="关闭删除确认窗口" title="关闭" @click="confirmDeleteDialog?.close()"><AppIcon name="close" /></button>
          </header>
          <div class="generation-confirm-content">
            <p class="generation-confirm-intro">确定删除{{ pendingDeleteItems.length > 1 ? `这 ${pendingDeleteItems.length} 份` : '这份' }}图纸「{{ pendingDeleteItems.map(i => i.name).join('、') }}」吗？删除后不可恢复。</p>
          </div>
          <footer class="workspace-dialog-actions">
            <span>删除操作不可恢复</span>
            <div>
              <button class="secondary" type="button" @click="confirmDeleteDialog?.close()"><AppIcon name="cancel" />取消</button>
              <button class="danger-confirm" type="button" @click="confirmDeleteSavedProjects"><AppIcon name="delete" />确认删除</button>
            </div>
          </footer>
        </div>
      </dialog>

      <section
        ref="centerStage"
        class="center-stage"
        :class="{ 'is-fullscreen': isFullscreen, 'force-landscape': forceLandscape, 'tools-collapsed': toolsCollapsed, 'generation-panel-open': generationPanelOpen && generationPanelDocked }"
      >
        <!-- 全屏时统一使用左侧快捷编辑栏，避免横屏后的 JS 布局状态与 CSS 断点不同步而两套工具栏同时消失。 -->
        <div v-if="!isMobileLayout || isFullscreen" class="quick-edit-toolbar panel" aria-label="快捷编辑工具">
          <div class="quick-edit-body">
            <section class="quick-edit-section draw-edit-group" aria-label="操作">
              <span class="quick-edit-label">操作</span>
              <div class="tool-group icon-tools">
                <button type="button" class="quick-icon-action" :class="{ active: interactionMode === 'pan' }" title="拖拽画板" aria-label="拖拽画板" @click="selectPanQuickTool"><AppIcon name="pan" /></button>
                <button type="button" class="quick-icon-action" :class="{ active: interactionMode === 'paint' && selectedColorIndex >= 0 }" title="豆笔：放置豆子" aria-label="豆笔：放置豆子" :disabled="editingLocked || collabReadOnly" @click="selectPaintQuickTool"><AppIcon name="needle" /></button>
                <button type="button" class="quick-icon-action" :class="{ active: interactionMode === 'paint' && selectedColorIndex === -1 }" title="镊子：取出豆子" aria-label="镊子：取出豆子" :disabled="editingLocked || collabReadOnly" @click="selectEraserQuickTool"><AppIcon name="tweezer" /></button>
                <button class="quick-icon-action copy-color-button" :class="{ active: interactionMode === 'pick' }" type="button" title="复制豆板颜色" aria-label="复制豆板颜色" :disabled="editingLocked || collabReadOnly" @click="selectCopyColorQuickTool"><AppIcon name="copy-color" /></button>
              </div>
              <div v-if="collabReadOnly" class="rail-permission-actions">
                <button type="button" title="申请编辑权限" aria-label="申请编辑权限" @click="handlePermApply('edit')"><AppIcon name="edit-permission" /></button>
                <button v-if="!collabCanSave" type="button" title="申请共享权限" aria-label="申请共享权限" @click="handlePermApply('save')"><AppIcon name="share-permission" /></button>
              </div>
            </section>

            <section class="quick-edit-section palette-edit-group" aria-label="颜色">
              <span class="quick-edit-label">颜色</span>
              <div class="quick-color-grid">
                <button
                  v-for="entry in quickPaletteColors"
                  :key="entry.color.id"
                  type="button"
                  :class="{ active: selectedColorIndex === entry.index }"
                  :style="{ '--quick-color': entry.color.hex }"
                  :title="`${entry.color.code} ${entry.color.name}`"
                  :aria-label="`选择 ${entry.color.code} ${entry.color.name}`"
                  @click="handleQuickPalettePick(entry.index)"
                ><i></i></button>
                <ColorPickerPopover
                  class="rail-color-picker"
                  :model-value="selectedColorIndex"
                  :colors="colors"
                  :item-counts="colorCounts"
                  :usage-revision="contentRevision"
                  :extra-colors="palette?.colors"
                  :brand-name="selectedBrand?.name"
                  :palette-name="currentPaletteSummary?.name"
                  @update:model-value="handleColorPick"
                />
              </div>
            </section>

            <section class="quick-edit-section history-edit-group" aria-label="历史">
              <span class="quick-edit-label">历史</span>
              <div class="tool-group icon-tools">
                <button type="button" title="撤销 Ctrl+Z" aria-label="撤销" :disabled="editingLocked || (collabActive ? !collabCanUndo : !history.length)" @click="collabActive ? collab.undoOwnEdit() : store.undo()"><AppIcon name="undo" /></button>
                <button type="button" title="恢复 Ctrl+Y" aria-label="恢复" :disabled="editingLocked || (collabActive ? !collabCanRedo : !future.length)" @click="collabActive ? collab.redoOwnEdit() : store.redo()"><AppIcon name="redo" /></button>
              </div>
            </section>
          </div>
        </div>
        <StudioMobileNav
          v-if="isMobileLayout && !isFullscreen"
          :interaction-mode="interactionMode"
          :selected-color-index="selectedColorIndex"
          :colors="colors"
          :color-counts="colorCounts"
          :extra-colors="palette?.colors"
          :brand-name="selectedBrand?.name"
          :palette-name="currentPaletteSummary?.name"
          :editing-disabled="editingLocked || collabReadOnly"
          @pan="selectPanQuickTool"
          @paint="selectPaintQuickTool"
          @erase="selectEraserQuickTool"
          @copy="selectCopyColorQuickTool"
          @update:color="handleColorPick"
        />
        <div v-if="isMobileLayout && !isFullscreen" class="studio-history-strip" role="group" aria-label="撤销与恢复">
          <button
            type="button"
            title="撤销"
            aria-label="撤销"
            :disabled="editingLocked || (collabActive ? !collabCanUndo : !history.length)"
            @click="collabActive ? collab.undoOwnEdit() : store.undo()"
          ><AppIcon name="undo" /></button>
          <button
            type="button"
            title="恢复"
            aria-label="恢复"
            :disabled="editingLocked || (collabActive ? !collabCanRedo : !future.length)"
            @click="collabActive ? collab.redoOwnEdit() : store.redo()"
          ><AppIcon name="redo" /></button>
        </div>
        <button v-if="isFullscreen" class="floating-generate-button" type="button" @click="openSettingsDialog">
          <AppIcon name="generate" /><span>生成图纸</span>
        </button>
        <button
          v-if="isFullscreen"
          class="floating-toolbar-toggle"
          type="button"
          :aria-expanded="!toolsCollapsed"
          aria-label="功能"
          title="功能"
          aria-controls="stage-tools"
          @click="toggleTools"
        >
          <AppIcon name="menu" /><span>功能</span>
        </button>
        <button v-if="isFullscreen" class="fullscreen-exit-button" type="button" @click="toggleFullscreen">
          <AppIcon name="fullscreen" /><span>退出全屏</span>
        </button>
        <button
          v-else
          class="fullscreen-enter-button"
          type="button"
          title="全屏"
          aria-label="全屏"
          @click="toggleFullscreen"
        >
          <AppIcon name="fullscreen" /><span>全屏</span>
        </button>
        <div id="stage-tools" ref="stageTools" v-show="!toolsCollapsed" class="stage-toolbar panel">
          <div class="toolbar-section mobile-edit-tools">
            <span class="toolbar-label">项目</span>
            <div class="tool-group">
              <button
                v-if="!collabIsMember || collabCanSave"
                class="project-save-action"
                type="button"
                :disabled="produceLocked || !hasPattern || !beadCount"
                @click="openSaveDialog"
              ><AppIcon name="save" />保存</button>
              <button
                v-else
                type="button"
                :disabled="collab.permCooldownRemaining('save') > 0"
                @click="handlePermApply('save')"
              ><AppIcon name="share-permission" />申请共享</button>
              <button v-if="!collabIsMember" class="project-clear-action" type="button" :disabled="!beadCount" @click="openClearCanvasDialog"><AppIcon name="delete" />清空豆板</button>
              <button v-if="!licensingDisabled" type="button" @click="openLicenseDialog"><AppIcon name="license" />授权与试用</button>
              <button
                v-if="collabReadOnly"
                type="button"
                :disabled="collab.permCooldownRemaining('edit') > 0"
                @click="handlePermApply('edit')"
              ><AppIcon name="edit-permission" />申请编辑</button>
              <button v-if="isMobileLayout" class="toolbar-toggle-button" type="button" :class="{ active: beadShape === 'circle' }" :aria-pressed="beadShape === 'circle'" @click="beadShape = 'circle'">
                <AppIcon name="round-bead" />圆豆
              </button>
              <button v-if="isMobileLayout" class="toolbar-toggle-button" type="button" :class="{ active: beadShape === 'square' }" :aria-pressed="beadShape === 'square'" @click="beadShape = 'square'">
                <AppIcon name="color-block" />色块
              </button>
              <button v-if="collabIsMember" type="button" @click="requestMemberExit"><AppIcon name="exit" />退出联机</button>
            </div>
          </div>
          <div class="toolbar-section mobile-display-tools">
            <span class="toolbar-label">显示</span>
            <div class="tool-group">
              <button class="toolbar-toggle-button" type="button" :class="{ active: showGrid }" :aria-pressed="showGrid" @click="showGrid = !showGrid"><AppIcon name="grid" />网格</button>
              <button class="toolbar-toggle-button" type="button" :class="{ active: showCodes }" :aria-pressed="showCodes" @click="showCodes = !showCodes"><AppIcon name="code" />色号</button>
              <button class="toolbar-toggle-button" type="button" :class="{ active: showBoardSplit }" :aria-pressed="showBoardSplit" @click="showBoardSplit = !showBoardSplit"><AppIcon name="stitch" />分板线</button>
              <button class="toolbar-toggle-button" type="button" :class="{ active: showCoordinates }" :aria-pressed="showCoordinates" @click="showCoordinates = !showCoordinates"><AppIcon name="locate" />坐标</button>
              <button class="toolbar-toggle-button" type="button" :class="{ active: soundEnabled }" :aria-pressed="soundEnabled" @click="sound.toggle()"><AppIcon name="volume" />声音</button>
            </div>
          </div>
          <div class="toolbar-section function-tools">
            <span class="toolbar-label">功能</span>
            <div class="tool-group">
              <button v-if="!collabIsMember" type="button" :disabled="editingLocked || !beadCount || collabActive" title="联机时不可批量替换" @click="openReplacementDialog"><AppIcon name="replace-color" />颜色替换</button>
              <button type="button" :disabled="!hasPattern" @click="openMaterialsDialog"><AppIcon name="materials" />查看用料</button>
              <button type="button" :disabled="!hasPattern || collabActive" title="联机时不可拼接豆板" @click="openBoardExpandDialog"><AppIcon name="stitch" />拼接豆板</button>
              <button type="button" @click="openLibraryDialog"><AppIcon name="library" />我的图纸</button>
              <!-- 好友联机入口：授权关闭或试用到期（无密钥）时隐藏，联机不可用 -->
              <button
                v-if="!licensingDisabled && !(trialExpired && !licensedKey)"
                class="collab-entry-button toolbar-toggle-button"
                :class="{ active: collabActive }"
                type="button"
                :aria-pressed="collabActive"
                :disabled="!hasPattern"
                @click="openCollabDialog"
              >
                <AppIcon name="online" />
                {{ collabActive ? '联机中' : '好友联机' }}
              </button>
            </div>
          </div>
          <div class="toolbar-section file-tools">
            <span class="toolbar-label">文件</span>
            <div class="tool-group">
              <button
                class="ironed-preview-button"
                type="button"
                :disabled="produceLocked || !hasPattern"
                @click="openIronedPreview"
              >
                <AppIcon class="ironed-preview-mark" name="ironed" />
                查看熨烫效果
              </button>
              <button v-if="!collabIsMember" class="export-toolbar-button file-import-action" type="button" :disabled="editingLocked || importBusy || collabActive" title="联机时不可导入" @click="openImportDialog">
                <AppIcon name="import" />导入
              </button>
              <button v-if="!collabIsMember || collabCanSave" class="export-toolbar-button file-export-action" type="button" :disabled="produceLocked || !hasPattern || exportBusy" @click="openExportDialog">
                <AppIcon name="export" />
                {{ exportBusy ? '导出中…' : '导出' }}
              </button>
            </div>
          </div>
        </div>

        <div class="canvas-frame panel">
          <PatternCanvas ref="patternCanvas" @minimum-zoom-change="updateMinimumCanvasZoom" />
          <div class="canvas-command-dock">
            <div v-if="hasPattern" class="canvas-view-controls">
              <div class="canvas-bead-shape-toggle" role="radiogroup" aria-label="豆子显示形状">
                <button type="button" :class="{ active: beadShape === 'circle' }" :aria-pressed="beadShape === 'circle'" title="圆豆" @click="beadShape = 'circle'">
                  <AppIcon name="round-bead" />
                </button>
                <button type="button" :class="{ active: beadShape === 'square' }" :aria-pressed="beadShape === 'square'" title="色块" @click="beadShape = 'square'">
                  <AppIcon name="color-block" />
                </button>
              </div>
              <button class="canvas-fit-button" type="button" title="完整显示画板" @click="patternCanvas?.fitPatternInViewport()">
                <AppIcon name="locate" /><span>适应画布</span>
              </button>
              <label class="canvas-zoom-control">
                <input
                  :value="cellSize"
                  type="range"
                  :min="minimumCanvasCellSize"
                  max="40"
                  step="1"
                  aria-label="调整画板缩放比例"
                  @input="changeCanvasZoom"
                />
                <span class="canvas-zoom-percentage">
                  <input
                    :value="zoomPercent"
                    type="number"
                    :min="minimumZoomPercent"
                    max="200"
                    step="5"
                    inputmode="numeric"
                    aria-label="输入画板缩放百分比"
                    @change="changeCanvasZoomPercent"
                    @keydown.enter="($event.target as HTMLInputElement).blur()"
                  />
                  <i>%</i>
                </span>
              </label>
            </div>
            <div class="canvas-meta">
              <!-- 画板信息：过长省略，点击弹窗显示完整 -->
              <button type="button" class="canvas-meta-info" title="查看完整画板信息" @click="openCanvasMetaDialog"><AppIcon name="info" /><span>{{ canvasMetaText }}</span></button>
              <!-- 显示开关：位于保存状态左侧，多选框与文字不换行 -->
              <div class="canvas-meta-views" role="group" aria-label="显示选项">
                <label title="显示网格"><input v-model="showGrid" type="checkbox" />网格</label>
                <label title="显示色号"><input v-model="showCodes" type="checkbox" />色号</label>
                <label title="显示分板线"><input v-model="showBoardSplit" type="checkbox" />分板线</label>
                <label title="显示坐标"><input v-model="showCoordinates" type="checkbox" />坐标</label>
                <label title="声音开关"><input type="checkbox" :checked="soundEnabled" @change="sound.toggle()" />声音</label>
              </div>
              <span class="autosave-status" :class="`is-${cloudSaveState}`" role="status">{{ cloudSaveStatus }}</span>
            </div>
          </div>
        </div>

        <section v-if="showIronedPreview" ref="ironedPreview" class="ironed-preview-host" :class="{ 'force-landscape': ironedForceLandscape }" aria-label="熨烫效果预览">
          <IronedPreview :download-disabled="produceLocked" @close="closeIronedPreview" @notify="notify" />
        </section>

        <!-- 画板信息完整弹窗：信息栏过长省略时点击展示全量信息 -->
        <dialog
          ref="canvasMetaDialog"
          class="materials-dialog"
          aria-labelledby="canvas-meta-dialog-title"
          @click="closeDialogFromBackdrop($event, canvasMetaDialog)"
        >
          <div class="materials-dialog-shell">
            <header class="workspace-dialog-header">
              <div>
                <small>画板信息</small>
                <h2 id="canvas-meta-dialog-title">画板信息</h2>
              </div>
              <button type="button" aria-label="关闭画板信息窗口" title="关闭" @click="canvasMetaDialog?.close()"><AppIcon name="close" /></button>
            </header>
            <div class="materials-content">
              <dl class="canvas-meta-detail">
                <div><dt>图纸尺寸</dt><dd>{{ width }} × {{ height }} 颗</dd></div>
                <div><dt>物理尺寸</dt><dd>{{ physicalWidth.toFixed(1) }} × {{ physicalHeight.toFixed(1) }} cm</dd></div>
                <div><dt>底板规格</dt><dd>{{ selectedBoard?.name || '未选择' }}（{{ boardSummary }}）</dd></div>
                <div v-if="processingInfo"><dt>处理信息</dt><dd>{{ processingInfo }}</dd></div>
                <div><dt>自动保存</dt><dd class="autosave-status" :class="`is-${cloudSaveState}`">{{ cloudSaveStatus }}</dd></div>
              </dl>
            </div>
            <footer class="workspace-dialog-actions">
              <button type="button" @click="canvasMetaDialog?.close()"><AppIcon name="close" />关闭</button>
            </footer>
          </div>
        </dialog>

        <dialog
          ref="materialsDialog"
          class="materials-dialog"
          aria-labelledby="materials-dialog-title"
          @click="closeDialogFromBackdrop($event, materialsDialog)"
        >
          <div class="materials-dialog-shell">
            <header class="workspace-dialog-header">
              <div><h2 id="materials-dialog-title">用料统计</h2></div>
              <button type="button" aria-label="关闭用料统计窗口" title="关闭" @click="materialsDialog?.close()"><AppIcon name="close" /></button>
            </header>
            <div class="materials-stats">
              <article><small>总豆数</small><strong>{{ beadCount.toLocaleString() }}</strong><span>颗</span></article>
              <article><small>使用颜色</small><strong>{{ usedColorCount }}</strong><span>种</span></article>
              <article><small>预计底板</small><strong>{{ boardCount }}</strong><span>块</span></article>
              <article><small>豆子规格</small><strong>{{ selectedBoard?.beadSize || '—' }}</strong><span>mm</span></article>
            </div>
            <footer class="workspace-dialog-actions">
              <button type="button" @click="materialsDialog?.close()"><AppIcon name="close" />关闭</button>
            </footer>
          </div>
        </dialog>

        <dialog
          ref="replacementDialog"
          class="replacement-dialog"
          aria-labelledby="replacement-dialog-title"
          @click="closeDialogFromBackdrop($event, replacementDialog)"
        >
          <div class="replacement-dialog-shell">
            <header class="workspace-dialog-header">
              <div>
                <small>缺少某个色号时，可整图替换为其他豆子</small>
                <h2 id="replacement-dialog-title">颜色替换</h2>
              </div>
              <button type="button" aria-label="关闭颜色替换窗口" title="关闭" @click="replacementDialog?.close()"><AppIcon name="close" /></button>
            </header>
            <div class="replacement-dialog-content">
              <div class="replacement-flow">
                <div class="replacement-field replacement-source-field">
                  <span>需要替换的颜色</span>
                  <ColorPickerPopover
                    v-model="replacementFromIndex"
                    :colors="colors"
                    :allowed-indices="replacementSourceIndices"
                    :item-counts="replacementSourceCounts"
                    :brand-name="selectedBrand?.name"
                    :palette-name="currentPaletteSummary?.name"
                  />
                </div>
                <span class="replacement-arrow" aria-hidden="true"><AppIcon name="chevron-right" /></span>
                <div class="replacement-field replacement-target-field">
                  <span>替代为</span>
                  <ColorPickerPopover
                    v-model="replacementToIndex"
                    :colors="colors"
                    :brand-name="selectedBrand?.name"
                    :palette-name="currentPaletteSummary?.name"
                  />
                </div>
              </div>
              <div class="replacement-preview" :class="{ invalid: replacementFromIndex === replacementToIndex }">
                <i :style="{ background: replacementSourceColor?.hex || '#fff' }"></i>
                <span>{{ replacementSourceColor?.code || '—' }}</span>
                <b aria-hidden="true"><AppIcon name="chevron-right" /></b>
                <i :style="{ background: replacementTargetColor?.hex || '#fff' }"></i>
                <span>{{ replacementTargetColor?.code || '—' }}</span>
                <small>{{ replacementFromIndex === replacementToIndex ? '请选择不同的替代色' : `将替换豆板中的 ${replacementSourceCount} 颗豆子` }}</small>
              </div>
              <p class="replacement-note">替换会记录为一次历史操作，可以通过快捷编辑中的“撤销”恢复原颜色。</p>
            </div>
            <footer class="workspace-dialog-actions replacement-dialog-actions">
              <button class="secondary" type="button" @click="replacementDialog?.close()"><AppIcon name="cancel" />取消</button>
              <button
                type="button"
                :disabled="!replacementSourceColor || !replacementTargetColor || replacementFromIndex === replacementToIndex"
                @click="confirmColorReplacement"
              >
                <AppIcon name="replace-color" />确认替换
              </button>
            </footer>
          </div>
        </dialog>

        <dialog
          ref="saveErrorDialog"
          class="save-error-dialog"
          aria-labelledby="save-error-dialog-title"
          @click="closeDialogFromBackdrop($event, saveErrorDialog)"
        >
          <div class="save-error-dialog-shell">
            <header class="workspace-dialog-header save-error-header">
              <div><h2 id="save-error-dialog-title">实时保存失败</h2></div>
              <button type="button" aria-label="关闭保存失败窗口" title="关闭" @click="saveErrorDialog?.close()"><AppIcon name="close" /></button>
            </header>
            <div class="save-error-content">
              <strong>本地草稿未能保存</strong>
              <p>{{ autoSaveError }}</p>
              <small>可以先导出图纸留存，或修复问题后再次尝试保存。</small>
            </div>
            <footer class="workspace-dialog-actions save-error-actions">
              <button class="secondary" type="button" :disabled="!hasPattern" @click="exportAfterSaveFailure"><AppIcon name="export" />导出图纸</button>
              <button type="button" @click="retryAutoSave"><AppIcon name="retry" />再次尝试</button>
            </footer>
          </div>
        </dialog>

        <dialog
          ref="clearCanvasDialog"
          class="save-error-dialog clear-canvas-dialog"
          aria-labelledby="clear-canvas-dialog-title"

        >
          <div class="save-error-dialog-shell">
            <header class="workspace-dialog-header clear-canvas-header">
              <div><h2 id="clear-canvas-dialog-title">确认清空豆板</h2></div>
              <button type="button" aria-label="关闭清空豆板确认窗口" title="关闭" @click="clearCanvasDialog?.close()"><AppIcon name="close" /></button>
            </header>
            <div class="clear-canvas-content">
              <strong>确定要移除豆板上的全部豆子吗？</strong>
              <small>{{ collabActive && collabIsHost ? '联机中：将同步清空所有成员的共享画布。清空后保留一次历史记录，可使用撤销恢复。' : '清空后会保留一次历史记录，可以使用撤销恢复。' }}</small>
            </div>
            <footer class="workspace-dialog-actions clear-canvas-actions">
              <button class="secondary" type="button" @click="clearCanvasDialog?.close()"><AppIcon name="cancel" />取消</button>
              <button class="danger-confirm" type="button" @click="confirmClearCanvas"><AppIcon name="delete" />确认清空</button>
            </footer>
          </div>
        </dialog>

        <dialog ref="exportDialog" class="export-dialog" aria-labelledby="export-dialog-title" @click="closeExportDialogFromBackdrop">
          <div class="export-dialog-shell">
            <header class="export-dialog-header">
              <div><h2 id="export-dialog-title">选择导出内容</h2></div>
              <button type="button" aria-label="关闭导出窗口" title="关闭" :disabled="exportBusy" @click="exportDialog?.close()"><AppIcon name="close" /></button>
            </header>

            <div class="export-choice-list">
              <label :class="{ selected: exportSelection.png }">
                <input v-model="exportSelection.png" type="checkbox" />
                <span><b>PNG 高清图纸</b><small>适合预览、打印和发送给朋友</small></span>
              </label>
              <label :class="{ selected: exportSelection.xlsx }">
                <input v-model="exportSelection.xlsx" type="checkbox" />
                <span><b>Excel 图纸</b><small>包含图纸、材料清单和制作说明</small></span>
              </label>
              <label :class="{ selected: exportSelection.csv }">
                <input v-model="exportSelection.csv" type="checkbox" />
                <span><b>CSV 材料清单</b><small>用于配货、报价或导入其他系统</small></span>
              </label>
              <label :class="{ selected: exportSelection.json }">
                <input v-model="exportSelection.json" type="checkbox" />
                <span><b>JSON 工程文件</b><small>保留完整数据，便于后续继续编辑</small></span>
              </label>
            </div>

            <p class="export-download-note">
              {{ isWeChat ? '微信内可保存 PNG 图片；Excel 等文件请在系统浏览器下载。' : '单项直接下载；选择多项时会合并为一个 ZIP，避免浏览器拦截。' }}
            </p>

            <footer class="export-dialog-actions">
              <span>已选择 {{ selectedExportCount }} 项</span>
              <div>
                <button type="button" :disabled="exportBusy" @click="exportDialog?.close()"><AppIcon name="cancel" />取消</button>
                <button class="confirm" type="button" :disabled="!selectedExportCount || exportBusy" @click="confirmExport">
                  <AppIcon name="export" />
                  {{ exportBusy ? '正在生成…' : '确认导出' }}
                </button>
              </div>
            </footer>
          </div>
        </dialog>

        <dialog
          ref="weChatExportDialog"
          class="export-dialog wechat-export-dialog"
          aria-labelledby="wechat-export-dialog-title"
          @close="releaseWeChatPreview"
          @click="closeDialogFromBackdrop($event, weChatExportDialog)"
        >
          <div class="export-dialog-shell">
            <header class="export-dialog-header">
              <div><h2 id="wechat-export-dialog-title">微信内导出</h2></div>
              <button type="button" aria-label="关闭微信导出窗口" title="关闭" @click="weChatExportDialog?.close()"><AppIcon name="close" /></button>
            </header>

            <div class="wechat-export-content">
              <template v-if="weChatPreviewUrl">
                <img :src="weChatPreviewUrl" alt="待保存的拼豆图纸" />
                <strong>长按上方图纸可保存到手机相册</strong>
                <small>如果长按没有保存选项，可点击“查看大图”后再次长按。</small>
              </template>
              <div v-if="weChatHasOtherFiles || !weChatPreviewUrl" class="wechat-browser-guide">
                <strong>{{ weChatHasOtherFiles ? '其他文件需要在系统浏览器下载' : '当前格式需要在系统浏览器下载' }}</strong>
                <small>打开系统浏览器后，再点击一次导出即可自动下载到默认位置。</small>
              </div>
              <div v-if="showSystemBrowserGuide" class="wechat-browser-instructions" role="status">
                <strong>如果没有自动跳转</strong>
                <span>点击微信右上角“…” → 选择“在浏览器打开”。</span>
                <small>当前图纸和参数会随链接恢复；链接十分钟内有效且只能使用一次。</small>
              </div>
            </div>

            <footer class="export-dialog-actions wechat-export-actions">
              <span>微信会限制部分文件直接下载</span>
              <div>
                <button v-if="weChatPreviewUrl" type="button" @click="openWeChatImage"><AppIcon name="large-image" />查看大图</button>
                <button v-if="showSystemBrowserGuide" type="button" @click="copySystemBrowserAddress"><AppIcon name="copy" />复制页面地址</button>
                <button class="confirm" type="button" :disabled="systemBrowserPreparing" @click="openInSystemBrowser">
                  <AppIcon name="browser" />
                  {{ systemBrowserPreparing ? '正在准备…' : '在浏览器打开' }}
                </button>
              </div>
            </footer>
          </div>
        </dialog>

      </section>

      <dialog
        ref="collabDialog"
        class="generation-confirm-dialog collab-dialog"
        aria-labelledby="collab-dialog-title"
        @click="closeDialogFromBackdrop($event, collabDialog)"
      >
        <div class="generation-confirm-shell collab-dialog-shell">
          <header class="workspace-dialog-header">
            <div>
              <small>好友联机</small>
              <h2 id="collab-dialog-title">好友联机</h2>
            </div>
            <button type="button" aria-label="关闭联机窗口" title="关闭" @click="closeCollabDialog"><AppIcon name="close" /></button>
          </header>

          <div class="collab-dialog-content">
            <!-- 未联机：发起或加入 -->
            <template v-if="!collabActive">
              <section class="collab-section">
                <div class="collab-section-head">
                  <strong>作为房主邀请好友</strong>
                  <small>最多支持 5 人同时在线联机</small>
                </div>
                <p class="collab-section-desc">
                  创建房间后，把邀请码或邀请链接发给好友，就能一起实时拼同一块豆板，最多 5 人同时在线协作，每处修改即时同步，联机画布由房主统一保存。
                </p>
                <button class="collab-primary-btn" type="button" :disabled="collabBusy || !hasPattern || !isCollabEntitled()" @click="handleHostCollab">
                  <AppIcon name="create-room" />
                  {{ collabBusy ? '正在创建…' : '创建联机房间' }}
                </button>
              </section>

              <section class="collab-section">
                <div class="collab-section-head">
                  <strong>输入邀请码加入</strong>
                  <small>好友邀请码由房主分享</small>
                </div>
                <div class="collab-join-row">
                  <input
                    v-model="collabInviteInput"
                    type="text"
                    maxlength="6"
                    aria-label="好友联机邀请码"
                    placeholder="请输入 6 位邀请码"
                    :disabled="collabBusy || collabJoinWaiting"
                    @keyup.enter="handleJoinCollab"
                  />
                  <button
                    type="button"
                    :disabled="collabBusy || collabJoinWaiting || !collabInviteInput.trim()"
                    @click="handleJoinCollab"
                  >
                    <AppIcon name="join-room" />
                    {{ collabBusy ? '申请中…' : (collabJoinWaiting ? `等待审批${collabRemainSeconds(collabJoinExpiresAt)}s` : '申请联机') }}
                  </button>
                </div>
                <p v-if="collabJoinMessage" class="collab-note">{{ collabJoinMessage }}</p>
                <p v-else class="collab-section-desc">未授权用户只能通过邀请码加入好友的房间，不能发起联机。</p>
                <!-- 申请等待倒计时：与房主端申请列表同步，刷新页面不影响计时 -->
                <p v-if="collabJoinWaiting && collabJoinExpiresAt" class="collab-note collab-countdown-note">
                  等待房主审批{{ collabRemainSeconds(collabJoinExpiresAt) > 0 ? `，${collabCountdownText(collabJoinExpiresAt)}` : '' }}
                </p>
                <button v-if="collabJoinWaiting" class="collab-cancel-btn" type="button" @click="collab.cancelJoin()">
                  <AppIcon class="collab-cancel-icon" name="cancel" />
                  取消申请
                </button>
              </section>
            </template>

            <!-- 联机中 -->
            <template v-else>
              <section v-if="collabIsHost" class="collab-section collab-invite-section">
                <div class="collab-section-head">
                  <strong>邀请码（分享给好友加入）</strong>
                  <small>刷新后原邀请码作废，已加入的好友不受影响</small>
                </div>
                <div class="collab-invite-code"><code>{{ collabInviteCode }}</code></div>
                <div class="collab-invite-actions">
                  <button type="button" @click="copyInviteCode"><AppIcon name="copy" />复制邀请码</button>
                  <button type="button" :disabled="collabBusy" @click="handleRefreshInvite"><AppIcon name="refresh-invite" />刷新邀请码</button>
                </div>
                <div class="collab-invite-link">
                  <div class="collab-invite-link-label">邀请链接（好友点开自动申请）</div>
                  <div class="collab-invite-link-row">
                    <code class="collab-invite-link-url">{{ collabInviteLink }}</code>
                    <button type="button" @click="copyInviteLink"><AppIcon name="invite" />复制链接</button>
                  </div>
                </div>
              </section>

              <section v-if="collabIsHost && collabPending.length" class="collab-section">
                <div class="collab-section-head">
                  <strong>联机申请</strong>
                  <small>{{ collabPending.length }} 人等待审批</small>
                </div>
                <ul class="collab-apply-list">
                  <li v-for="apply in collabPending" :key="apply.applyId">
                    <span class="collab-apply-name">{{ apply.name }}</span>
                    <span class="collab-apply-countdown">{{ collabCountdownText(apply.expiresAt) }}</span>
                    <div class="collab-apply-actions">
                      <button type="button" @click="handleReject(apply.applyId)"><AppIcon name="close" />拒绝</button>
                      <button class="primary" type="button" @click="handleApprove(apply.applyId)"><AppIcon name="confirm" />同意</button>
                    </div>
                  </li>
                </ul>
              </section>

              <section class="collab-section">
                <div class="collab-section-head">
                  <strong>联机成员</strong>
                  <small>{{ collabMemberCount }} / 5 人</small>
                </div>
                <ul class="collab-member-list">
                  <li
                    v-for="m in collabRoom?.members ?? []"
                    :key="m.memberId"
                    class="collab-member-item"
                    :class="{ 'is-self': m.memberId === collabMyMember?.memberId }"
                  >
                    <i class="collab-member-color" :style="{ background: COLLAB_COLORS[m.colorIndex] || COLLAB_COLORS[0] }" aria-hidden="true"></i>
                    <span class="collab-member-name">
                      {{ m.name }}{{ m.memberId === collabMyMember?.memberId ? '（我）' : '' }}
                    </span>
                    <span v-if="m.isHost" class="collab-member-tag">房主</span>
                    <span v-else-if="m.memberId === collabMyMember?.memberId" class="collab-member-tag">我的颜色</span>
                    <div v-if="collabIsHost && !m.isHost" class="collab-member-controls">
                      <label
                        class="collab-perm-check"
                        :class="{ 'is-on': m.canEdit }"
                        :title="m.canEdit ? '该成员可以编辑豆板' : '该成员仅能查看豆板'"
                      >
                        <input
                          type="checkbox"
                          :checked="m.canEdit"
                          :aria-label="`${m.name} 编辑权限`"
                          @change="handleTogglePermission(m.memberId, m.canEdit)"
                        />
                        <span>编辑</span>
                      </label>
                      <label
                        class="collab-perm-check"
                        :class="{ 'is-on': m.canSave }"
                        :title="m.canSave ? '该成员可以保存、导出图纸' : '该成员不可保存、导出图纸'"
                      >
                        <input
                          type="checkbox"
                          :checked="m.canSave"
                          :aria-label="`${m.name} 共享权限`"
                          @change="handleToggleSavePermission(m.memberId, m.canSave)"
                        />
                        <span>共享</span>
                      </label>
                      <button type="button" class="collab-kick-btn" title="将好友踢出联机房间" @click="handleKickMember(m.memberId)"><AppIcon name="kick" />踢出</button>
                    </div>
                  </li>
                </ul>
              </section>
            </template>
          </div>

          <footer class="workspace-dialog-actions collab-dialog-actions">
            <span v-if="collabActive">{{ collabIsHost ? '共享画布仅由你自动保存' : '共享房主豆板，退出后恢复本人数据' }}</span>
            <template v-if="collabActive">
              <button class="danger-confirm" type="button" @click="collabIsHost ? handleEndCollab() : requestMemberExit()">
                <AppIcon name="exit" />
                {{ collabIsHost ? '结束联机' : '退出联机' }}
              </button>
            </template>
            <button v-else class="secondary" type="button" @click="closeCollabDialog"><AppIcon name="close" />关闭</button>
          </footer>
        </div>
      </dialog>

      <!-- 成员退出联机确认提示：防止误触导致丢失联机数据。 -->
      <dialog
        ref="collabExitDialog"
        class="generation-confirm-dialog"
        aria-labelledby="collab-exit-title"
        @click="closeDialogFromBackdrop($event, collabExitDialog)"
      >
        <div class="generation-confirm-shell">
          <header class="workspace-dialog-header">
            <div>
              <small>好友联机</small>
              <h2 id="collab-exit-title">确认退出联机</h2>
            </div>
            <button type="button" aria-label="关闭退出确认窗口" title="关闭" @click="collabExitDialog?.close()"><AppIcon name="close" /></button>
          </header>
          <div class="generation-confirm-content">
            <p class="generation-confirm-intro">退出后共享联机画布将关闭，并恢复你进入联机前的本人豆板数据。确定要退出联机吗？</p>
          </div>
          <footer class="workspace-dialog-actions">
            <button class="secondary" type="button" @click="collabExitDialog?.close()"><AppIcon name="cancel" />取消</button>
            <button type="button" @click="confirmMemberExit"><AppIcon name="exit" />确认退出</button>
          </footer>
        </div>
      </dialog>
    </main>

    <!-- 右上角铃铛通知：房主联机中可见，悬浮页面右上角顶层，动态提示联机申请 -->
    <div v-if="collabBellVisible" class="collab-bell-wrap">
      <button
        class="collab-bell-btn"
        type="button"
        :class="{ 'has-new': collabPending.length > 0, 'is-open': collabBellOpen }"
        :aria-expanded="collabBellOpen"
        aria-label="联机申请通知"
        @click="toggleCollabBell"
      >
        <AppIcon class="collab-bell-icon" name="online" />
        <i v-if="collabPending.length" class="collab-bell-badge">{{ collabPending.length > 9 ? '9+' : collabPending.length }}</i>
      </button>
      <div v-if="collabBellOpen" class="collab-popup collab-bell-popup">
        <div class="collab-popup-head">
          <strong>联机申请</strong>
          <small>{{ collabPending.length }} 人等待审批</small>
        </div>
        <ul v-if="collabPending.length" class="collab-apply-list">
          <li v-for="apply in collabPending" :key="apply.applyId">
            <span class="collab-apply-name">{{ apply.name }}</span>
            <span class="collab-apply-countdown">{{ collabCountdownText(apply.expiresAt) }}</span>
            <div class="collab-apply-actions">
              <button type="button" @click="handleReject(apply.applyId)"><AppIcon name="close" />拒绝</button>
              <button class="primary" type="button" @click="handleApprove(apply.applyId)"><AppIcon name="confirm" />同意</button>
            </div>
          </li>
        </ul>
        <p v-else class="collab-popup-empty">暂无联机申请</p>
      </div>
    </div>

    <!-- 房主审批消息铃铛：权限申请 + 替换图纸申请统一入口，均带倒计时，超时自动移出队列；仅房主联机中可见 -->
    <div v-if="collabPermBellVisible" class="collab-bell-wrap collab-perm-bell-wrap">
      <button
        class="collab-bell-btn collab-perm-bell-btn"
        type="button"
        :class="{ 'has-new': collabPendingPerms.length > 0 || collabPendingReplaces.length > 0, 'is-open': collabPermBellOpen }"
        :aria-expanded="collabPermBellOpen"
        aria-label="房主审批消息"
        @click="toggleCollabPermBell"
      >
        <AppIcon class="collab-bell-icon" name="edit-permission" />
        <i v-if="collabPendingPerms.length || collabPendingReplaces.length" class="collab-bell-badge">{{ (collabPendingPerms.length + collabPendingReplaces.length) > 9 ? '9+' : collabPendingPerms.length + collabPendingReplaces.length }}</i>
      </button>
      <div v-if="collabPermBellOpen" class="collab-popup collab-bell-popup">
        <div class="collab-popup-head">
          <strong>审批消息</strong>
          <small>{{ collabPendingReplaces.length + collabPendingPerms.length }} 条待审批</small>
        </div>
        <!-- 替换图纸申请：x豆 申请替换当前图纸（1 分钟有效期倒计时） -->
        <template v-if="collabPendingReplaces.length">
          <p class="collab-queue-section-title">替换图纸申请</p>
          <ul class="collab-apply-list">
            <li v-for="apply in collabPendingReplaces" :key="apply.applyId">
              <span class="collab-apply-name">
                <i class="collab-member-color collab-perm-color" :style="{ background: COLLAB_COLORS[apply.colorIndex] || COLLAB_COLORS[0] }" aria-hidden="true"></i>
                <span class="collab-apply-name-text">{{ apply.name }}申请替换当前图纸</span>
              </span>
              <span class="collab-apply-countdown">{{ collabCountdownText(apply.expiresAt) }}</span>
              <div class="collab-apply-actions">
                <button type="button" @click="handleReplaceDecide(apply.applyId, false)"><AppIcon name="close" />拒绝</button>
                <button class="primary" type="button" @click="handleReplaceDecide(apply.applyId, true)"><AppIcon name="confirm" />同意</button>
              </div>
            </li>
          </ul>
        </template>
        <!-- 权限申请：x豆 申请编辑/共享权限（30 秒有效期倒计时） -->
        <template v-if="collabPendingPerms.length">
          <p class="collab-queue-section-title">权限申请</p>
          <ul class="collab-apply-list">
            <li v-for="apply in collabPendingPerms" :key="apply.applyId">
              <span class="collab-apply-name">
                <i class="collab-member-color collab-perm-color" :style="{ background: COLLAB_COLORS[apply.colorIndex] || COLLAB_COLORS[0] }" aria-hidden="true"></i>
                <span class="collab-apply-name-text">{{ apply.name }}申请{{ apply.perm === 'save' ? '共享' : '编辑' }}权限</span>
              </span>
              <span class="collab-apply-countdown">{{ collabCountdownText(apply.expiresAt) }}</span>
              <div class="collab-apply-actions">
                <button type="button" @click="handlePermDecide(apply.applyId, false)"><AppIcon name="close" />拒绝</button>
                <button class="primary" type="button" @click="handlePermDecide(apply.applyId, true)"><AppIcon name="confirm" />同意</button>
              </div>
            </li>
          </ul>
        </template>
        <p v-if="!collabPendingReplaces.length && !collabPendingPerms.length" class="collab-popup-empty">暂无待审批消息</p>
      </div>
    </div>

    <div v-if="error" class="error-banner">{{ error }}</div>
    <!-- toast 仅在存在消息时渲染；无消息时整个元素从 DOM 移除，避免残留空白提示条。 -->
    <div v-if="toast" ref="toastElement" class="toast" :class="{ 'is-mobile-bottom': toastMobileBottom }" popover="manual" role="status" aria-live="polite">
      <!-- 好友联机消息提示图标：仅联机相关消息显示，随系统主题（绿色）配色；普通消息不显示。 -->
      <AppIcon v-if="toastIcon" class="toast-icon" name="online" />
      <span class="toast-text">{{ toast }}</span>
    </div>
    <!-- 页脚：ICP 备案号（点击跳转工信部）+ 版本信息 -->
    <footer class="site-footer">
      <a href="https://beian.miit.gov.cn/" target="_blank" rel="noopener noreferrer">桂ICP备2026012053号-1</a>
      <span class="site-footer-version">V0.2.0</span>
    </footer>
  </div>
</template>
