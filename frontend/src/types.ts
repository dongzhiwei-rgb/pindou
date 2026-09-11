/**
 * 文件：types.ts
 * 用途：声明前端使用的品牌、色卡、底板、量化结果和导出数据类型。
 * 核心职责：与后端 API 契约保持同步，为状态仓库、组件和导出模块提供静态类型保护。
 * 版权：@董志伟-联系方式-makabak1204
 * 最后修改：2026-08-27
 */

export interface PaletteSummary {
  id: string
  name: string
  colorCount: number
  beadSizes: number[]
  verified: boolean
  version: string
  note: string
}

export interface BrandSummary {
  id: string
  name: string
  country: string
  kind: string
  heatRank: number
  palettes: PaletteSummary[]
}

export interface BeadColor {
  id: string
  brand: string
  code: string
  name: string
  hex: string
  rgb: number[]
  lab: number[]
  source: string
  license: string
}

export interface PaletteDetail {
  brandId: string
  brandName: string
  paletteId: string
  paletteName: string
  colorCount: number
  verified: boolean
  version: string
  note: string
  colors: BeadColor[]
}

export interface BoardPreset {
  id: string
  name: string
  columns: number
  rows: number
  beadSize: number
  category: string
  note: string
}

export interface UsageItem {
  colorIndex: number
  code: string
  name: string
  hex: string
  count: number
}

export interface QuantizeResponse {
  width: number
  height: number
  brandId: string
  paletteId: string
  colors: BeadColor[]
  cells: number[]
  usage: UsageItem[]
  usedColorCount: number
  beadCount: number
  processingMs: number
  algorithm: string
}

export interface PatternExportPayload {
  title: string
  width: number
  height: number
  beadSize: number
  boardColumns: number
  boardRows: number
  brandName: string
  paletteName: string
  colors: Array<{ code: string; name: string; hex: string }>
  cells: number[]
}

export interface PortableProject {
  version: 1
  title: string
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
  beadShape: 'circle' | 'square'
  showCodes: boolean
  showGrid: boolean
  showBoardSplit: boolean
  showCoordinates: boolean
  cellSize: number
  cells: number[]
  /** 当前选择的豆针色号索引（-1 为取出/橡皮）；缺省时由前端从第一个已用色号推断。 */
  selectedColorIndex?: number
  /** 最近使用过的正常色号索引；切回画笔时优先恢复。 */
  lastPaintColorIndex?: number
  /** 生成时的实际色板（图片用到的品牌色子集）；缺省时回退为完整色卡。 */
  colors?: Array<{ id: string; brand: string; code: string; name: string; hex: string; rgb: number[]; lab: number[]; source: string; license: string }>
}

export interface LicenseStatus {
  status: 'active' | 'time_expired' | 'invalid' | 'expired' | 'exhausted' | 'revoked' | 'device_mismatch' | 'device_conflict' | 'device_active'
  remainingSeconds: number | null
  remainingCount: number | null
  message: string | null
  // 兼容旧接口字段；即时下线模式下始终为 null。
  kickInSeconds: number | null
}

// 登录（激活密钥并绑定账号）的结果；isNewUser 区分新用户注册还是老用户重登，hasSave 表示是否有可同步的云端存档。
export interface LoginResult {
  status: 'active' | 'time_expired' | 'invalid' | 'expired' | 'exhausted' | 'revoked' | 'device_mismatch' | 'device_conflict' | 'device_active'
  remainingSeconds: number | null
  remainingCount: number | null
  message: string | null
  isNewUser: boolean
  hasSave: boolean
  sessionToken: string | null
}

export interface TrialStatus {
  remainingSeconds: number
  remainingGenerations: number
  totalMinutes: number
  expired: boolean
}

// 同色合并识别结果：从图纸中识别出的可合并矩形大块（颜色相同的相邻小格）。
export interface MergeBlock {
  x: number
  y: number
  w: number
  h: number
  colorIndex: number
}

// 「我的图纸」库条目：命名保存的图纸（本地多份 / 云端整库承载）。
export interface SavedProject {
  id: string
  name: string
  savedAt: string
  project: PortableProject
}

export interface TrialInfoResponse {
  licensed: boolean
  license: LicenseStatus | null
  trial: TrialStatus | null
  /** 后端通过配置关闭了授权/试用功能时返回 true；前端据此隐藏授权与试用入口。 */
  licensingDisabled?: boolean
}

// ---------- 好友联机（Collab） ----------

/** 联机房间成员（房主固定 colorIndex=0 系统默认色，好友 1..4）。 */
export interface CollabMemberDto {
  memberId: string
  name: string
  colorIndex: number
  isHost: boolean
  canEdit: boolean
  canSave: boolean
  canUndo?: boolean
  canRedo?: boolean
}

/** 待房主审批的好友申请。 */
export interface CollabPendingDto {
  applyId: string
  name: string
  /** 申请过期时间（Unix 毫秒）：房主 30 秒内未处理即超时作废；房主端据此显示倒计时，刷新页面不受影响。 */
  expiresAt: number
}

/** 待房主审批的成员权限申请（编辑/共享）。 */
export interface CollabPendingPermDto {
  applyId: string
  memberId: string
  name: string
  colorIndex: number
  perm: 'edit' | 'save'
  /** 申请过期时间（Unix 毫秒）：房主 30 秒内未响应即超时作废；据此显示倒计时，刷新页面不受影响。 */
  expiresAt: number
}

/** 待房主审批的成员替换图纸申请（x豆 申请替换当前图纸）。 */
export interface CollabReplaceDto {
  applyId: string
  memberId: string
  name: string
  colorIndex: number
  /** 申请过期时间（Unix 毫秒）：房主 1 分钟内未响应即自动退出消息队列；据此显示倒计时，刷新页面不受影响。 */
  expiresAt: number
}

/** 联机房间状态。 */
export interface CollabRoomDto {
  roomId: string
  inviteCode: string
  hostName: string
  capacity: number
  members: CollabMemberDto[]
  pending: CollabPendingDto[]
  pendingPerms?: CollabPendingPermDto[]
  pendingReplaces?: CollabReplaceDto[]
}

/** 房主豆板快照（只读下发；联机编辑以 edits 增量广播）。 */
export interface CollabSnapshotDto {
  width: number
  height: number
  cells: number[]
  colors: BeadColor[]
  title: string
}

/** 一次格子编辑（index 为扁平格子下标，colorIndex 为色板索引，-1 表示透明）。 */
export interface CollabEditItem {
  index: number
  colorIndex: number
}

/** 某个成员本次编辑抢占的格子集合（用于以成员颜色描框）。 */
export interface CollabLockEvent {
  memberId: string
  colorIndex: number
  cells: number[]
}

/** SSE 初始状态：房间 + 快照 + 本人信息（重连兜底）。 */
export interface CollabStateEvent {
  type: 'state'
  seq: number
  boardRevision: number
  room: CollabRoomDto
  snapshot: CollabSnapshotDto
  member: { memberId: string; name: string; colorIndex: number; isHost: boolean; canEdit: boolean; canSave: boolean; canUndo?: boolean; canRedo?: boolean }
}

/** 房间成员/申请变化事件。 */
export interface CollabRoomEvent {
  type: 'room'
  room: CollabRoomDto
}

/** 编辑广播事件：edits 已接受、reverts 冲突回滚（权威值）、locks 本次抢占格子。 */
export interface CollabEditsEvent {
  type: 'edits'
  seq: number
  edits: CollabEditItem[]
  reverts: CollabEditItem[]
  locks: CollabLockEvent[]
}

/** 房主单独调整某个成员编辑权限。 */
export interface CollabPermissionEvent {
  type: 'permission'
  memberId: string
  canEdit: boolean
}

/** 房主单独调整某个成员的保存共享权限。 */
export interface CollabSavePermissionEvent {
  type: 'savepermission'
  memberId: string
  canSave: boolean
}

/** 成员被踢出 / 房间关闭。 */
export interface CollabKickedEvent {
  type: 'kicked'
  memberId: string
  reason: string
}
export interface CollabClosedEvent {
  type: 'closed'
  reason: string
}

/** 申请审批结果（好友轮询 apply/result 获取）。 */
export interface CollabApprovedPayload {
  type: 'approved'
  seq: number
  boardRevision: number
  member: { memberId: string; name: string; colorIndex: number; token: string; canEdit?: boolean; canSave?: boolean }
  room: CollabRoomDto
  snapshot: CollabSnapshotDto
}
export interface CollabRejectedPayload {
  type: 'rejected'
  reason: string
}

/** 申请超时/作废结果（好友轮询 apply/result 获取）：房主未响应、成员离线、房主离线/房间关闭等。 */
export interface CollabExpiredPayload {
  type: 'expired'
  reason: 'host_no_response' | 'member_offline' | 'host_offline' | 'host_left' | string
}
