// 文件：CollabModels.cs
// 用途：定义好友联机房间、成员、申请、格子锁与各接口请求/响应模型。
// 核心职责：联机协作的纯数据模型；房间与成员常驻内存，格子锁用于同格单人编辑冲突控制。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-27

using System.Collections.Concurrent;

namespace Pindou.Api.Models;

/// <summary>联机房间成员（含房主）。</summary>
public sealed class CollabMember
{
    public required string MemberId { get; init; }
    public required string Token { get; init; }
    public required string Name { get; init; }
    /// <summary>0 = 房主系统默认色；1..4 = 好友分配色。</summary>
    public int ColorIndex { get; init; }
    public bool IsHost { get; init; }
    public DateTime JoinedAt { get; init; }
    /// <summary>好友的授权密钥（可为空）；非空且授权到期时会被自动踢出房间。</summary>
    public string? LicenseKey { get; init; }
    /// <summary>申请人/成员来源 IP：无密钥的试用用户据此判定试用是否到期（到期自动退出联机）。</summary>
    public string? IpAddress { get; init; }
    /// <summary>是否允许该成员编辑豆板；房主可单独开关，关闭后该成员的编辑会被服务端拒绝。
    /// 默认关闭：成员加入时默认只读，由房主按需开启编辑权限。</summary>
    public bool CanEdit { get; set; } = false;
    /// <summary>是否允许该成员保存/导出共享豆板；房主可单独开关（共享权限），关闭后成员端隐藏保存、导出入口。
    /// 默认开启：成员加入即可保存/导出（共享权限）。</summary>
    public bool CanSave { get; set; } = true;
    /// <summary>成员（非房主）当前活跃的 SSE 连接数（刷新/多标签场景计数，避免误判离线）。</summary>
    public int SseCount { get; set; }
    /// <summary>成员离线起始时间：成员无活跃 SSE 连接超过阈值即视为离线，自动移出房间并通知房主。</summary>
    public DateTime? OfflineSince { get; set; }
    /// <summary>最近一次完成授权状态复核的时间；后台清扫据此避免每轮重复访问数据库。</summary>
    public DateTime LastEntitlementCheckAt { get; set; }
}

/// <summary>待房主审批的好友申请。</summary>
public sealed class CollabApply
{
    public required string ApplyId { get; init; }
    public required string RoomId { get; init; }
    public required string Name { get; init; }
    public string? LicenseKey { get; init; }
    /// <summary>申请人来源 IP：无密钥的试用用户据此判定试用是否到期（到期申请作废/不可发起）。</summary>
    public string? IpAddress { get; init; }
    public DateTime CreatedAt { get; init; }
    /// <summary>申请过期时间（创建后 30 秒）：房主 30 秒内未响应即作废，并通知成员「房主未响应，可再次发起申请」。</summary>
    public DateTime ExpiresAt { get; init; }
    /// <summary>申请人最近一次轮询时间（UTC）：成员等待审批期间每次轮询视为在线；超过离线阈值未轮询即视为成员离线，申请自动作废。</summary>
    public DateTime LastSeenAt { get; set; }
    /// <summary>是否已处理（房主通过/拒绝后置位，防止重复审批）。</summary>
    public bool Handled { get; set; }
}

/// <summary>待房主审批的成员权限申请（编辑/共享），成员申请、房主审批；30 秒冷却限制重复申请。</summary>
public sealed class CollabPermApply
{
    public required string ApplyId { get; init; }
    public required string RoomId { get; init; }
    public required string MemberId { get; init; }
    public required string Name { get; init; }
    /// <summary>申请者豆子颜色索引（1..4），房主据此识别申请人。</summary>
    public int ColorIndex { get; init; }
    /// <summary>申请权限类型：'edit' = 编辑权限，'save' = 共享（保存/导出）权限。</summary>
    public required string Perm { get; init; }
    public DateTime CreatedAt { get; init; }
    /// <summary>申请过期时间（创建后 30 秒）：房主 30 秒内未响应即作废并通知成员。</summary>
    public DateTime ExpiresAt { get; init; }
    /// <summary>申请人最近一次活跃时间（创建时写入；成员离线判定以房间内在线状态为准）。</summary>
    public DateTime LastSeenAt { get; set; }
    /// <summary>是否已处理（房主同意/拒绝后置位）。</summary>
    public bool Handled { get; set; }
}

/// <summary>待房主审批的成员替换图纸申请：成员生成/加载自家图纸后申请替换整张画布；1 分钟有效期与冷却。</summary>
public sealed class CollabReplace
{
    public required string ApplyId { get; init; }
    public required string RoomId { get; init; }
    public required string MemberId { get; init; }
    public required string Name { get; init; }
    /// <summary>申请者豆子颜色索引（1..4），房主据此识别申请人。</summary>
    public int ColorIndex { get; init; }
    /// <summary>拟替换的整张豆板快照：房主同意后作为房间新权威快照广播给全房间。</summary>
    public required CollabSnapshot Snapshot { get; init; }
    public DateTime CreatedAt { get; init; }
    /// <summary>申请过期时间（创建后 1 分钟）：房主 1 分钟内未响应即作废，自动退出消息队列。</summary>
    public DateTime ExpiresAt { get; init; }
    /// <summary>是否已处理（房主同意/拒绝后置位，防止重复审批）。</summary>
    public bool Handled { get; set; }
}

/// <summary>格子编辑锁：同格同时只允许一人编辑。</summary>
public sealed class CellLock
{
    public required string MemberId { get; init; }
    public DateTime ExpiresAt { get; set; }
}

/// <summary>联机房间：容量上限 5 人（房主 + 4 好友）。</summary>
public sealed class CollabRoom
{
    /// <summary>
    /// 房间级业务同步边界。ConcurrentDictionary 只保证单次字典操作安全，
    /// 人数检查、成员加入、快照替换、格子写入与序号增长等组合操作仍必须在此锁内完成。
    /// </summary>
    public object SyncRoot { get; } = new();
    public required string RoomId { get; init; }
    public required string HostToken { get; init; }
    public required string HostMemberId { get; init; }
    public required string HostName { get; init; }
    public string InviteCode { get; set; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public int NextMemberColor { get; set; } = 1;
    public int NextNameIndex { get; set; } = 1;
    /// <summary>房主的授权密钥；启用授权且过期时关闭整个房间。</summary>
    public string? HostLicenseKey { get; init; }

    /// <summary>已加入成员：memberId -&gt; member。</summary>
    public ConcurrentDictionary<string, CollabMember> Members { get; } = new();

    /// <summary>待房主审批的申请：applyId -&gt; 申请。</summary>
    public ConcurrentDictionary<string, CollabApply> Pending { get; } = new();

    /// <summary>待房主审批的成员权限申请（编辑/共享）：applyId -&gt; 申请；成员离线/超时自动清空。</summary>
    public ConcurrentDictionary<string, CollabPermApply> PendingPerms { get; } = new();

    /// <summary>待房主审批的成员替换图纸申请：applyId -&gt; 申请；1 分钟有效期，超时自动退出消息队列。</summary>
    public ConcurrentDictionary<string, CollabReplace> PendingReplaces { get; } = new();

    /// <summary>格子锁：cellIndex -&gt; 锁（含过期时间）。</summary>
    public ConcurrentDictionary<int, CellLock> Locks { get; } = new();

    /// <summary>房主当前活跃的 SSE 连接数（多标签页 / 刷新场景计数，避免把刷新误判为离线）。</summary>
    public int HostSseCount { get; set; }
    /// <summary>房主离线起始时间：房主无活跃 SSE 连接超过 10 秒视为离线，关闭房间并通知成员。</summary>
    public DateTime? HostOfflineSince { get; set; }
    /// <summary>最近一次完成房主授权复核的时间，避免高频清扫反复查询同一授权。</summary>
    public DateTime LastHostEntitlementCheckAt { get; set; }

    /// <summary>房主豆板快照：新成员加入时下发；编辑以增量 edits 广播并同步更新此快照。</summary>
    public CollabSnapshot? Snapshot { get; set; }

    /// <summary>编辑事件序号：客户端按序应用，避免乱序覆盖。</summary>
    public long EditSeq { get; set; }
    /// <summary>整张豆板版本：清空、替换或重新同步时递增，用于拒绝基于旧图纸产生的延迟编辑。</summary>
    public long BoardRevision { get; set; } = 1;

    /// <summary>每个格子的最近服务端变更版本；撤销时据此确认该格仍是本人那次修改，避免覆盖他人后续编辑。</summary>
    public long[] CellVersions { get; set; } = [];
    /// <summary>每个格子的最后编辑者（memberId）：撤销/恢复仅作用于自己最后编辑的格子，跳过他人后续修改过的格子。</summary>
    public string?[] CellLastEditors { get; set; } = [];
    public long MutationVersion { get; set; }
    /// <summary>按成员隔离的撤销/恢复栈，仅在 SyncRoot 内访问。</summary>
    public Dictionary<string, List<CollabHistoryOperation>> UndoHistory { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<CollabHistoryOperation>> RedoHistory { get; } = new(StringComparer.Ordinal);

    public int Capacity => 5;
    public bool IsFull => Members.Count >= Capacity;
}

/// <summary>房主豆板快照（只读下发，编辑以增量 edits 广播）。</summary>
public sealed class CollabSnapshot
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int[] Cells { get; set; } = [];
    /// <summary>经过后端结构与长度校验的色板；成员端用于绘制和校验格子色号。</summary>
    public CollabColor[] Colors { get; set; } = [];
    public string Title { get; set; } = "";
}

/// <summary>
/// 联机快照使用的最小完整色板契约。使用强类型模型而非 object/JsonElement，
/// 使数量、字段长度、颜色格式及格子索引能够在服务端可靠校验。
/// </summary>
public sealed class CollabColor
{
    public string Id { get; set; } = "";
    public string Brand { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Hex { get; set; } = "";
    public double[] Rgb { get; set; } = [];
    public double[] Lab { get; set; } = [];
    public string Source { get; set; } = "";
    public string License { get; set; } = "";
}

/// <summary>一次由同一用户发起的联机编辑操作，可跨多个网络批次合并。</summary>
public sealed class CollabHistoryOperation
{
    public required string OperationId { get; init; }
    public required string MemberId { get; init; }
    public Dictionary<int, CollabCellChange> Changes { get; } = new();
}

/// <summary>某格在一次编辑操作前后的值及服务端版本，用于安全撤销和恢复。</summary>
public sealed class CollabCellChange
{
    public int Index { get; init; }
    public int Before { get; init; }
    public int After { get; set; }
    public long AppliedVersion { get; set; }
    public long UndoVersion { get; set; }
}

// ---------- 请求 ----------

public sealed class CollabHostRequest { public CollabSnapshot? Snapshot { get; set; } }
public sealed class CollabTokenRequest { public string? Token { get; set; } }
public sealed class CollabApplyRequest { public string? InviteCode { get; set; } public string? LicenseKey { get; set; } }
public sealed class CollabCancelApplyRequest { public string? ApplyId { get; set; } }
public sealed class CollabDecideRequest { public string? Token { get; set; } public string? ApplyId { get; set; } public bool Accept { get; set; } }
public sealed class CollabKickRequest { public string? Token { get; set; } public string? MemberId { get; set; } }
public sealed class CollabLeaveRequest { public string? Token { get; set; } }
public sealed class CollabPermissionRequest { public string? Token { get; set; } public string? MemberId { get; set; } public bool CanEdit { get; set; } }
public sealed class CollabSavePermissionRequest { public string? Token { get; set; } public string? MemberId { get; set; } public bool CanSave { get; set; } }
public sealed class CollabLockRequest { public string? Token { get; set; } public int[]? Cells { get; set; } }
public sealed class CollabUnlockRequest { public string? Token { get; set; } public int[]? Cells { get; set; } }
public sealed class CollabEditRequest { public string? Token { get; set; } public string? OperationId { get; set; } public long BoardRevision { get; set; } public CollabEditItem[]? Edits { get; set; } }
public sealed class CollabEditItem { public int Index { get; set; } public int ColorIndex { get; set; } }
public sealed class CollabHistoryRequest { public string? Token { get; set; } public string? Action { get; set; } }
public sealed class CollabPermApplyRequest { public string? Token { get; set; } public string? Perm { get; set; } }
public sealed class CollabPermDecideRequest { public string? Token { get; set; } public string? ApplyId { get; set; } public bool Accept { get; set; } }
public sealed class CollabResyncRequest { public string? Token { get; set; } public CollabSnapshot? Snapshot { get; set; } }
public sealed class CollabReplaceRequest { public string? Token { get; set; } public CollabSnapshot? Snapshot { get; set; } }
public sealed class CollabReplaceDecideRequest { public string? Token { get; set; } public string? ApplyId { get; set; } public bool Accept { get; set; } }

// ---------- 响应约定（匿名对象直接序列化，这里仅作注释） ----------
// HostCreate → { roomId, inviteCode, hostToken, memberId, hostName, colorIndex, room }
// RefreshInvite → { inviteCode }
// Apply → { applyId, name }
// Decide → { ok }
// Kick → { ok }
// Leave → { ok }
// SubmitEdits → { seq, edits, reverts, locks }
