/**
 * 文件：api.ts
 * 用途：封装目录查询、图纸量化、Excel 导出、商用授权与云端存档请求。
 * 核心职责：统一 API 地址、表单字段、错误解析与响应类型；为请求自动附加 HMAC 签名（时间戳+nonce 防重放）
 * 与授权密钥/会话令牌（密钥即账号）。SSE 因无法携带请求头，签名经 query 传递。
 * 版权：@董志伟-联系方式-makabak1204
 * 最后修改：2026-08-27
 */

import type { BoardPreset, BrandSummary, CollabApprovedPayload, CollabEditItem, CollabExpiredPayload, CollabLockEvent, CollabRejectedPayload, CollabRoomDto, CollabSnapshotDto, LicenseStatus, LoginResult, PaletteDetail, PatternExportPayload, PortableProject, QuantizeResponse, SavedProject, TrialInfoResponse, TrialStatus } from './types'

const configuredApiBase = String(import.meta.env.VITE_API_BASE_URL || '').trim().replace(/\/$/, '')

/**
 * 本机开发时让 API/SSE 直连 5080，避免它们与 Vite 热更新共用 8088 的 HTTP/1.1 连接池。
 * 局域网开发仍保留同源代理，因为后端的本地启动配置未必监听网卡地址；生产构建始终使用同源接口。
 */
function resolveApiBase(): string {
  if (configuredApiBase) return configuredApiBase
  if (!import.meta.env.DEV) return ''

  const host = window.location.hostname.toLowerCase()
  if (host !== 'localhost' && host !== '127.0.0.1' && host !== '[::1]') return ''
  return `http://${host}:5080`
}

const API_BASE = resolveApiBase()
const LICENSE_STORAGE_KEY = 'pindou-license-key'
const SESSION_STORAGE_KEY = 'pindou-session-token'
const DEFAULT_REQUEST_TIMEOUT = 20_000
const activeRequestControllers = new Set<AbortController>()

type ApiRequestInit = RequestInit & {
  /** 普通接口最长等待时间；图片处理、导出等慢请求可按调用场景单独放宽。 */
  timeoutMs?: number
}

// 后端返回的非 2xx 错误；status/code 用于区分授权失败等业务错误。
export class ApiError extends Error {
  status: number
  code?: string
  constructor(message: string, status: number, code?: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
  }
}

export function getActiveLicenseKey(): string {
  return localStorage.getItem(LICENSE_STORAGE_KEY) || ''
}

export function setActiveLicenseKey(key: string): void {
  localStorage.setItem(LICENSE_STORAGE_KEY, key.trim())
}

// 会话令牌存 sessionStorage（标签页级）：同一浏览器多标签页各自独立会话，
// 新标签页不再复用旧标签页的会话令牌，服务端才能按密钥单点踢出旧标签页。
export function getSessionToken(): string {
  return sessionStorage.getItem(SESSION_STORAGE_KEY) || ''
}

export function setSessionToken(token: string | null): void {
  if (token) sessionStorage.setItem(SESSION_STORAGE_KEY, token)
  else sessionStorage.removeItem(SESSION_STORAGE_KEY)
}

export function clearActiveLicenseKey(): void {
  localStorage.removeItem(LICENSE_STORAGE_KEY)
}

// ---------- 请求层：认证由 HttpOnly 会话 Cookie 与各接口业务鉴权承担（高-01 阶段3，已移除 HMAC 签名） ----------

/**
 * 页面离开或组件销毁时统一取消尚未完成的普通请求。
 * SSE 有自己的关闭流程，不放入这个集合，避免 BFCache 恢复时误用已经失效的连接。
 */
export function abortActiveApiRequests(): void {
  const reason = new DOMException('页面已离开，取消未完成请求。', 'AbortError')
  for (const controller of activeRequestControllers) controller.abort(reason)
  activeRequestControllers.clear()
}

// 统一请求入口：超时必须覆盖“响应头 + 完整响应体”，不能在 fetch() 返回响应头后就清除定时器。
// 先把响应体缓冲为内存 Response，后续 json()/blob() 只读取本地数据，不会再因代理半断开而无限等待。
async function signedFetch(url: string, init: ApiRequestInit = {}): Promise<Response> {
  const { timeoutMs = DEFAULT_REQUEST_TIMEOUT, signal: externalSignal, ...requestInit } = init
  const controller = new AbortController()
  let timeoutTriggered = false
  const abortFromCaller = () => controller.abort(externalSignal?.reason)
  if (externalSignal?.aborted) abortFromCaller()
  else externalSignal?.addEventListener('abort', abortFromCaller, { once: true })

  activeRequestControllers.add(controller)
  const timeout = window.setTimeout(() => {
    timeoutTriggered = true
    controller.abort(new DOMException('请求超时。', 'TimeoutError'))
  }, Math.max(1000, timeoutMs))
  try {
    const response = await fetch(`${API_BASE}${url}`, {
      ...requestInit,
      credentials: requestInit.credentials ?? 'include',
      signal: controller.signal,
    })
    const statusHasNoBody = response.status === 204 || response.status === 205 || response.status === 304
    const body = statusHasNoBody || !response.body ? null : await response.arrayBuffer()
    return new Response(body, {
      status: response.status,
      statusText: response.statusText,
      headers: response.headers,
    })
  } catch (reason) {
    if (timeoutTriggered) {
      throw new ApiError('请求超时，请检查网络后重试。', 408, 'REQUEST_TIMEOUT')
    }
    throw reason
  } finally {
    window.clearTimeout(timeout)
    activeRequestControllers.delete(controller)
    externalSignal?.removeEventListener('abort', abortFromCaller)
  }
}

// 建立 SSE 连接：EventSource 无法携带请求头，身份由 HttpOnly 会话 Cookie / 一次性票据承担。
async function signedEventSource(path: string): Promise<EventSource> {
  return new EventSource(`${API_BASE}${path}`, { withCredentials: true })
}

async function readJson<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const problem = await response.json().catch(() => ({ message: `请求失败（${response.status}）` }))
    throw new ApiError(problem.message || problem.detail || `请求失败（${response.status}）`, response.status, problem.code)
  }
  return response.json() as Promise<T>
}

// 目录批量接口：一次请求获取厂商/色卡与底板预设，避免初始化时多次往返。
export async function getCatalogOverview(): Promise<{ brands: BrandSummary[]; boards: BoardPreset[] }> {
  return readJson(await signedFetch('/api/catalog/overview'))
}

export async function getPalette(brandId: string, paletteId: string): Promise<PaletteDetail> {
  return readJson(await signedFetch(`/api/catalog/brands/${encodeURIComponent(brandId)}/palettes/${encodeURIComponent(paletteId)}`))
}

export async function quantizeImage(file: File, options: {
  paletteId: string
  width: number
  height: number
  maxColors: number
  dither: boolean
  removeBackground: boolean
  backgroundThreshold: number
  noiseSuppression: number
}): Promise<QuantizeResponse> {
  const body = new FormData()
  body.append('image', file)
  Object.entries(options).forEach(([key, value]) => body.append(key, String(value)))
  return readJson(await signedFetch('/api/patterns/quantize', {
    method: 'POST',
    headers: { 'X-License-Key': getActiveLicenseKey(), 'X-Session-Token': getSessionToken() },
    body,
    timeoutMs: 120_000,
  }))
}

export async function getLicenseInfo(key: string): Promise<LicenseStatus> {
  return readJson(await signedFetch('/api/license/info', {
    headers: { 'X-License-Key': key },
  }))
}

export async function startLicenseSession(key: string, sessionToken: string): Promise<LicenseStatus> {
  return readJson(await signedFetch('/api/license/session', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ key, sessionToken }),
  }))
}

export async function heartbeatLicense(key: string, sessionToken: string): Promise<LicenseStatus> {
  return readJson(await signedFetch('/api/license/heartbeat', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ key, sessionToken }),
    timeoutMs: 8000,
  }))
}

export async function getTrialStatus(): Promise<TrialInfoResponse> {
  return readJson(await signedFetch('/api/license/trial', {
    headers: { 'X-License-Key': getActiveLicenseKey() },
  }))
}

// 试用心跳：仅在前台在线时累加试用时长；关闭/后台/锁屏/断网不累计。
export async function trialHeartbeat(): Promise<TrialStatus> {
  return readJson(await signedFetch('/api/license/trial/heartbeat', { timeoutMs: 8000 }))
}

export async function loginLicense(key: string, sessionToken: string): Promise<LoginResult> {
  return readJson(await signedFetch('/api/license/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ key, sessionToken }),
  }))
}

// 登出（退出登录）：清除会话并标记密钥离线。
export async function logoutLicense(key: string, sessionToken: string): Promise<void> {
  await signedFetch('/api/license/logout', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ key, sessionToken }),
  })
}

// 建立「被踢下线」事件长连接（SSE）；新设备用同一密钥登录时旧设备会收到 session-kicked 事件并立即下线。
// 申请 SSE 连接票据（一次性、60 秒有效），避免把授权密钥/会话令牌/管理令牌放入 URL。
async function requestSseTicket(purpose: string, headers?: Record<string, string>): Promise<string> {
  const response = await signedFetch('/api/license/tickets', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...headers },
    body: JSON.stringify({ purpose }),
  })
  if (!response.ok) {
    const problem = await response.json().catch(() => ({ message: '票据申请失败' }))
    throw new ApiError(problem.message || '票据申请失败', response.status, problem.code)
  }
  const data = await response.json()
  return data.ticket as string
}

export async function createKickEventSource(key: string, sessionToken: string): Promise<EventSource> {
  const ticket = await requestSseTicket('kick', { 'X-License-Key': key, 'X-Session-Token': sessionToken })
  return signedEventSource(`/api/license/events?ticket=${encodeURIComponent(ticket)}`)
}

// 建立「开关状态」事件长连接（SSE）；管理员接口修改授权/试用开关时服务端即时推送 switch-changed 事件。
export async function createSwitchEventSource(): Promise<EventSource> {
  return signedEventSource('/api/license/switches/events')
}

export async function getCloudSave(key: string): Promise<string | null> {
  const response = await signedFetch('/api/saves', {
    headers: { 'X-License-Key': key, 'X-Session-Token': getSessionToken() },
  })
  if (response.status === 404) return null
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}))
    throw new ApiError(problem.message || `读取存档失败（${response.status}）`, response.status, problem.code)
  }
  const data = await response.json()
  return data.payload as string
}

// 读取「我的图纸」云端图纸库（payload 承载整库列表；兼容旧版单张存档格式）。
export async function getSaveLibrary(key: string): Promise<SavedProject[] | null> {
  const payload = await getCloudSave(key)
  if (!payload) return null
  const parsed = JSON.parse(payload)
  if (parsed && parsed.kind === 'pindou-library' && Array.isArray(parsed.items)) return parsed.items as SavedProject[]
  if (parsed && parsed.version === 1) {
    return [{ id: 'legacy', name: (parsed.title as string) || '旧存档', savedAt: '', project: parsed }]
  }
  return []
}

// 把「我的图纸」图纸库整体写回云端。
export async function saveSaveLibrary(key: string, items: SavedProject[]): Promise<void> {
  await saveCloudSave(key, JSON.stringify({ kind: 'pindou-library', items }))
}

export async function saveCloudSave(key: string, payload: string): Promise<void> {
  const response = await signedFetch('/api/saves', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Session-Token': getSessionToken() },
    body: JSON.stringify({ key, payload }),
  })
  if (!response.ok) {
    const problem = await response.json().catch(() => ({ message: '存档保存失败' }))
    throw new ApiError(problem.message || '存档保存失败', response.status, problem.code)
  }
}

export async function exportExcel(payload: PatternExportPayload): Promise<Blob> {
  const response = await signedFetch('/api/exports/xlsx', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
    timeoutMs: 60_000,
  })
  if (!response.ok) {
    const problem = await response.json().catch(() => ({ message: 'Excel导出失败。' }))
    throw new Error(problem.message || problem.detail || 'Excel导出失败。')
  }
  return response.blob()
}

export async function createBrowserHandoff(project: PortableProject): Promise<{ token: string; expiresAt: string }> {
  return readJson(await signedFetch('/api/handoffs', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ project }),
  }))
}

export async function consumeBrowserHandoff(token: string): Promise<PortableProject> {
  return readJson(await signedFetch(`/api/handoffs/${encodeURIComponent(token)}`, {
    cache: 'no-store',
  }))
}

// ---------- 好友联机（Collab） ----------

// 联机成员身份凭证（memberToken）只通过请求体传递；SSE 使用一次性票据连接，避免令牌入 URL。

export interface CollabHostResult {
  roomId: string
  inviteCode: string
  hostToken: string
  memberId: string
  hostName: string
  colorIndex: number
  seq: number
  boardRevision: number
  room: CollabRoomDto
}

/** 房主发起联机：携带当前豆板快照创建房间；授权开启时房主必须持有有效授权密钥。 */
export async function hostCollab(snapshot: CollabSnapshotDto): Promise<CollabHostResult> {
  return readJson(await signedFetch('/api/collab/host', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-License-Key': getActiveLicenseKey() },
    body: JSON.stringify({ snapshot }),
  }))
}

/** 房主刷新邀请码：旧码与旧邀请链接作废，已联机好友不受影响（不踢出）。 */
export async function refreshCollabInvite(token: string): Promise<{ inviteCode: string }> {
  return readJson(await signedFetch('/api/collab/refresh', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token }),
  }))
}

/** 好友凭邀请码申请加入；返回 applyId（用于轮询房主审批结果）。 */
export async function applyCollab(inviteCode: string): Promise<{ applyId: string; name: string }> {
  return readJson(await signedFetch('/api/collab/apply', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-License-Key': getActiveLicenseKey() },
    body: JSON.stringify({ inviteCode }),
  }))
}

/** 申请人主动取消尚未审批的联机申请。 */
export async function cancelCollabApply(applyId: string): Promise<{ ok: boolean }> {
  return readJson(await signedFetch('/api/collab/apply/cancel', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ applyId }),
  }))
}

/** 房主审批好友申请（同意/拒绝）。 */
export async function decideCollab(token: string, applyId: string, accept: boolean): Promise<{ ok: boolean }> {
  return readJson(await signedFetch('/api/collab/decide', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token, applyId, accept }),
  }))
}

/** 好友轮询审批结果：pending=true 表示仍在等待；approved 携带成员身份与房主豆板快照；rejected/expired 为终态。 */
export async function getCollabApplyResult(applyId: string): Promise<CollabApprovedPayload | CollabRejectedPayload | CollabExpiredPayload | { pending: true }> {
  return readJson(await signedFetch(`/api/collab/apply/result/${encodeURIComponent(applyId)}`, { cache: 'no-store' }))
}

/** 房主踢出联机成员。 */
export async function kickCollabMember(token: string, memberId: string): Promise<{ ok: boolean }> {
  return readJson(await signedFetch('/api/collab/kick', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token, memberId }),
  }))
}

/** 房主单独开启/关闭某个成员的编辑权限。 */
export async function setCollabPermission(token: string, memberId: string, canEdit: boolean): Promise<{ ok: boolean }> {
  return readJson(await signedFetch('/api/collab/permission', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token, memberId, canEdit }),
  }))
}

/** 房主单独开启/关闭某个成员的保存共享权限。 */
export async function setCollabSavePermission(token: string, memberId: string, canSave: boolean): Promise<{ ok: boolean }> {
  return readJson(await signedFetch('/api/collab/savepermission', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token, memberId, canSave }),
  }))
}

/** 成员退出联机；房主退出会关闭整个房间并踢出全部好友。 */
export async function leaveCollab(token: string): Promise<{ ok: boolean }> {
  return readJson(await signedFetch('/api/collab/leave', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token }),
  }))
}

/** 成员提交编辑：服务端仲裁冲突并广播 edits/reverts/locks。 */
export async function submitCollabEdits(token: string, boardRevision: number, operationId: string, edits: CollabEditItem[]): Promise<{ seq: number; edits: CollabEditItem[]; reverts: CollabEditItem[]; locks: CollabLockEvent[]; canUndo: boolean; canRedo: boolean }> {
  return readJson(await signedFetch('/api/collab/edits', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token, boardRevision, operationId, edits }),
  }))
}

/** 服务端权威撤销/恢复：只作用于当前成员自己的最近一次有效编辑。 */
export async function submitCollabHistory(token: string, action: 'undo' | 'redo'): Promise<{ seq: number; edits: CollabEditItem[]; canUndo: boolean; canRedo: boolean }> {
  return readJson(await signedFetch('/api/collab/history', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token, action }),
  }))
}

/** 房主清空共享画布：清空房间全部格子并广播 clear 事件给所有成员（成员端整体清空本地画布）。 */
export async function submitCollabClear(token: string): Promise<{ ok: boolean }> {
  return readJson(await signedFetch('/api/collab/clear', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token }),
  }))
}

/** 成员申请权限（编辑 edit / 共享 save）：仅可申请自己的权限，30 秒冷却与 30 秒超时由服务端仲裁。 */
export async function submitCollabPermApply(token: string, perm: 'edit' | 'save'): Promise<{ applyId: string }> {
  return readJson(await signedFetch('/api/collab/permission-apply', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token, perm }),
  }))
}

/** 房主审批成员权限申请（同意/拒绝）。 */
export async function submitCollabPermDecide(token: string, applyId: string, accept: boolean): Promise<{ ok: boolean }> {
  return readJson(await signedFetch('/api/collab/permission-decide', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token, applyId, accept }),
  }))
}

/** 房主生成/替换整块画布后同步给成员：更新服务端快照并广播 resync 事件（联机豆板数据统一）。 */
export async function submitCollabResync(token: string, snapshot: CollabSnapshotDto): Promise<{ ok: boolean }> {
  return readJson(await signedFetch('/api/collab/resync', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token, snapshot }),
  }))
}

/** 成员申请替换整张联机图纸：1 分钟冷却与有效期，房主审批通过后全房间同步新图纸。 */
export async function submitCollabReplaceRequest(token: string, snapshot: CollabSnapshotDto): Promise<{ applyId: string }> {
  return readJson(await signedFetch('/api/collab/replace-request', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token, snapshot }),
  }))
}

/** 房主审批成员替换图纸申请（同意/拒绝）。 */
export async function submitCollabReplaceDecide(token: string, applyId: string, accept: boolean): Promise<{ ok: boolean }> {
  return readJson(await signedFetch('/api/collab/replace-decide', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token, applyId, accept }),
  }))
}

/** 建立联机事件 SSE 长连接：先用成员令牌换取一次性票据，再以票据建立连接。 */
export async function createCollabEventSource(token: string): Promise<EventSource> {
  const response = await signedFetch('/api/collab/ticket', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token }),
  })
  if (!response.ok) {
    const problem = await response.json().catch(() => ({ message: '票据申请失败' }))
    throw new ApiError(problem.message || '票据申请失败', response.status, problem.code)
  }
  const data = await response.json()
  return signedEventSource(`/api/collab/events?ticket=${encodeURIComponent(data.ticket as string)}`)
}
