// 文件：CollabService.cs
// 用途：好友联机核心服务——房间创建、邀请码刷新、好友申请审批、踢出/退出、编辑同步与格子冲突锁。
// 核心职责：以内存态维护联机房间；用 Channel 向房间成员广播实时事件；
//         编辑采用「客户端乐观落笔 + 服务端仲裁回滚」，保证同一格子同一时刻只允许一人编辑。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-27

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Pindou.Api.Models;

namespace Pindou.Api.Services;

public sealed class CollabService
{
    private const int MaxRooms = 300;              // 并发房间上限，防止内存被拖垮
    private const int MaxPendingPerRoom = 5;       // 每个房间待审批申请上限（申请列表最多 5 个）
    private const int LockTtlSeconds = 8;          // 格子锁有效期：短暂独占，到期释放
    private const int MaxEditsPerRequest = 500;    // 单次编辑批量上限，避免超大请求体
    private const int MaxSnapshotCells = 160 * 160;
    private const int MaxSnapshotColors = 2000;
    private const int MaxSseConnectionsPerMember = 3;
    private static readonly TimeSpan ApplyWaitTtl = TimeSpan.FromSeconds(30);   // 申请等待时长：30 秒内房主未响应即作废
    private static readonly TimeSpan ApplyResultTtl = TimeSpan.FromMinutes(2);  // 审批结果保留时长：好友延迟连接轮询兜底
    private static readonly TimeSpan ApplyOfflineTtl = TimeSpan.FromSeconds(15); // 成员等待审批期间超过 15 秒未轮询视为离线，申请自动作废
    private static readonly TimeSpan HostOfflineTtl = TimeSpan.FromSeconds(10); // 房主无活跃 SSE 超过 10 秒视为离线并关闭房间
    private static readonly TimeSpan MemberOfflineTtl = TimeSpan.FromSeconds(15); // 成员关闭网页/断线超过 15 秒视为离线，自动移出房间并通知房主（刷新重建连接远小于此阈值，不会误判）
    private static readonly TimeSpan PermApplyWaitTtl = TimeSpan.FromSeconds(30);    // 权限申请等待时长：30 秒内房主未响应即作废并通知成员
    private static readonly TimeSpan PermApplyCoolDownTtl = TimeSpan.FromSeconds(60); // 权限申请冷却：同一成员同一权限 60 秒内不可重复申请（大于 30 秒有效期 + 清扫/网络延迟，避免未到期重复发送）
    private static readonly TimeSpan ReplaceWaitTtl = TimeSpan.FromMinutes(1);     // 替换图纸申请有效期：房主 1 分钟内未响应即作废，自动退出消息队列
    private static readonly TimeSpan ReplaceCooldownTtl = TimeSpan.FromMinutes(1); // 替换图纸申请冷却：同一成员 1 分钟内不可重复申请
    private static readonly TimeSpan EntitlementCheckInterval = TimeSpan.FromSeconds(30); // 授权状态无需随 3 秒内存清扫重复查询

    private static readonly JsonSerializerOptions EventJsonOptions = new(JsonSerializerDefaults.Web)
    {
        // 事件数据通过 Channel 内存传递，不涉及反射序列化外部对象以外的敏感信息。
        MaxDepth = 32,
    };

    private readonly ConcurrentDictionary<string, CollabRoom> _rooms = new(StringComparer.Ordinal);                                  // roomId -> room
    private readonly ConcurrentDictionary<string, string> _tokenToRoom = new(StringComparer.Ordinal);                                // memberToken -> roomId
    private readonly ConcurrentDictionary<string, Channel<string>> _applyChannels = new(StringComparer.Ordinal);                     // applyId -> 审批等待通道
    private readonly ConcurrentDictionary<string, (string Json, DateTime ExpiresAt)> _applyResults = new(StringComparer.Ordinal);    // applyId -> 已审批结果
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Channel<string>, string>> _roomChannels = new(StringComparer.Ordinal); // roomId -> (订阅者通道 -> memberId)
    private readonly ConcurrentDictionary<string, DateTime> _roomLastActive = new(StringComparer.Ordinal);                              // roomId -> 最后活跃时间（订阅者变化时更新，用于闲置房间清理）
    private readonly ConcurrentDictionary<string, string> _applyToRoom = new(StringComparer.Ordinal);                                    // applyId -> roomId（申请在线状态跟踪：轮询刷新 LastSeenAt）
    private readonly ConcurrentDictionary<string, DateTime> _lastPermApplyAt = new(StringComparer.Ordinal);                                // memberId:perm -> 上次权限申请时间（30 秒冷却）
    private readonly ConcurrentDictionary<string, DateTime> _lastReplaceAt = new(StringComparer.Ordinal);                                  // memberId -> 上次替换图纸申请时间（1 分钟冷却）
    private readonly object _roomLifecycleLock = new();                                                                                // 保护房间数量上限、邀请码唯一性和房间注册

    private readonly LicenseStore _licenses;
    private readonly FeatureSwitchStore _switches;
    private readonly IpTrialTracker _ipTrials;

    public CollabService(LicenseStore licenses, FeatureSwitchStore switches, IpTrialTracker ipTrials)
    {
        _licenses = licenses;
        _switches = switches;
        _ipTrials = ipTrials;
    }

    // ---------- 房间创建 ----------

    /// <summary>房主创建联机房间并返回房间与房主成员；失败时抛出带中文提示的异常。</summary>
    public (CollabRoom Room, CollabMember Host) HostCreate(CollabSnapshot snapshot, string? licenseKey)
    {
        ValidateSnapshot(snapshot);
        var roomId = NewToken(6);
        var hostMemberId = NewToken(8);
        var hostToken = NewToken(24);
        lock (_roomLifecycleLock)
        {
            if (_rooms.Count >= MaxRooms)
                throw new InvalidOperationException("联机房间已满，请稍后再试。");

            // 生成邀请码和注册房间必须在同一临界区，否则两个并发创建可能选中同一个短码。
            string inviteCode;
            do { inviteCode = NewInviteCode(); } while (_rooms.Values.Any(r => r.InviteCode == inviteCode));
            var room = new CollabRoom
            {
                RoomId = roomId,
                HostToken = hostToken,
                HostMemberId = hostMemberId,
                HostName = "房主",
                InviteCode = inviteCode,
                HostLicenseKey = string.IsNullOrWhiteSpace(licenseKey) ? null : licenseKey,
                Snapshot = CloneSnapshot(snapshot),
                CellVersions = new long[snapshot.Cells.Length],
                CellLastEditors = new string?[snapshot.Cells.Length],
                LastHostEntitlementCheckAt = DateTime.UtcNow,
            };
            var host = new CollabMember
            {
                MemberId = hostMemberId,
                Token = hostToken,
                Name = room.HostName,
                ColorIndex = 0,
                IsHost = true,
                JoinedAt = DateTime.UtcNow,
                LicenseKey = string.IsNullOrWhiteSpace(licenseKey) ? null : licenseKey,
                LastEntitlementCheckAt = DateTime.UtcNow,
                // 房主恒可编辑、可保存：成员默认只读（CanEdit=false），但房主不受影响。
                CanEdit = true,
                CanSave = true,
            };
            room.Members[hostMemberId] = host;
            _rooms[roomId] = room;
            _tokenToRoom[hostToken] = roomId;
            _roomLastActive[roomId] = DateTime.UtcNow;
            return (room, host);
        }
    }

    /// <summary>授权开启时，校验房主是否可发起联机（需有效授权密钥）；返回中文错误或 null。</summary>
    public async Task<string?> ValidateHostAsync(string? licenseKey, CancellationToken ct)
    {
        if (!_switches.EnableAuth) return null;
        if (string.IsNullOrWhiteSpace(licenseKey))
            return "未授权用户只能加入好友房间，不能发起联机。";
        var status = await _licenses.InspectAsync(licenseKey, ct);
        return status.Status == "active" ? null : "当前密钥不可用，无法发起联机。";
    }

    // ---------- 邀请码 ----------

    /// <summary>刷新邀请码：旧码与旧邀请链接作废，已联机好友不受影响（不踢出），仅作废待审批申请。返回新邀请码。</summary>
    public string RefreshInvite(string token)
    {
        var room = GetRoomByToken(token, requireHost: true)
            ?? throw new InvalidOperationException("联机状态已失效，请重新发起联机。");
        string inviteCode;
        List<string> pendingIds;
        lock (_roomLifecycleLock)
        lock (room.SyncRoot)
        {
            do { inviteCode = NewInviteCode(); } while (_rooms.Values.Any(r => r.InviteCode == inviteCode && !ReferenceEquals(r, room)));
            room.InviteCode = inviteCode;
            // 刷新邀请码后旧码与旧链接失效；已联机成员不受影响，仅作废待审批申请。
            pendingIds = room.Pending.Keys.ToList();
            room.Pending.Clear();
        }
        foreach (var applyId in pendingIds) RemoveApplyChannel(applyId);
        BroadcastRoom(room);
        return inviteCode;
    }

    // ---------- 好友申请 ----------

    /// <summary>好友凭邀请码申请加入；返回 (applyId, error)（applyId 用于等待房主审批）。
    /// 授权开启时校验申请人资格：密钥有效可申请；无密钥的试用用户试用到期则拒绝申请。</summary>
    public async Task<(string ApplyId, string? Error)> ApplyAsync(string inviteCode, string? licenseKey, string? ipAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(inviteCode))
        {
            return ("", "请输入邀请码。");
        }
        // 授权开启时先校验申请人资格（复制链接/手动输入邀请码同走此入口，试用到期不可发起申请）。
        var eligibilityError = await ValidateApplicantAsync(licenseKey, ipAddress, ct);
        if (eligibilityError is not null)
        {
            return ("", eligibilityError);
        }
        var room = FindRoomByInviteCode(inviteCode.Trim().ToUpperInvariant());
        if (room is null)
        {
            return ("", "邀请码无效或已失效。");
        }
        var applyId = NewToken(12);
        string name;
        lock (room.SyncRoot)
        {
            // 申请资格校验期间邀请码可能刷新或房间可能关闭，落库前必须再次确认。
            if (!_rooms.ContainsKey(room.RoomId) || !string.Equals(room.InviteCode, inviteCode.Trim(), StringComparison.OrdinalIgnoreCase))
                return ("", "邀请码无效或已失效。");
            if (room.IsFull) return ("", "房间人数已满（最多 5 人）。");
            if (room.Pending.Count >= MaxPendingPerRoom)
                return ("", "申请列表已满（最多 5 人），请稍后再试。");

            name = "好友" + (room.NextNameIndex + room.Pending.Count);
            room.Pending[applyId] = new CollabApply
            {
                ApplyId = applyId,
                RoomId = room.RoomId,
                Name = name,
                LicenseKey = string.IsNullOrWhiteSpace(licenseKey) ? null : licenseKey,
                IpAddress = string.IsNullOrWhiteSpace(ipAddress) ? null : ipAddress,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(ApplyWaitTtl),
                LastSeenAt = DateTime.UtcNow,
            };
        }
        _applyChannels[applyId] = CreateChannel();
        _applyToRoom[applyId] = room.RoomId;
        BroadcastRoom(room);
        return (applyId, null);
    }

    /// <summary>
    /// 申请人主动取消尚未审批的联机申请。applyId 是申请阶段唯一凭证；取消与房主审批在同一房间锁内串行，
    /// 保证双方并发操作时只有先到达服务端的一方成功，另一方收到“联机申请已失效”。
    /// </summary>
    public (bool Ok, string? Error) CancelApply(string applyId)
    {
        if (string.IsNullOrWhiteSpace(applyId)
            || !_applyToRoom.TryGetValue(applyId, out var roomId)
            || !_rooms.TryGetValue(roomId, out var room))
            return (false, "联机申请已失效。");

        CollabApply canceled;
        lock (room.SyncRoot)
        {
            if (!room.Pending.TryRemove(applyId, out canceled!))
                return (false, "联机申请已失效。");

            RemoveApplyChannel(applyId);
            // 保留短期终态，兼容取消请求返回前已经发出的轮询，避免旧轮询继续得到 pending。
            PushApplyResult(applyId, Serialize(new { type = "expired", reason = "member_cancelled" }));
        }

        // 先发精准移除事件，再发完整房间状态兜底；房主无需等待定时清扫即可移除列表项。
        BroadcastRoom(room, Serialize(new { type = "apply_removed", applyId, name = canceled.Name, reason = "member_cancelled" }));
        BroadcastRoom(room);
        return (true, null);
    }

    /// <summary>校验申请人可否申请联机：授权关闭时放行；有密钥按密钥有效状态判定；无密钥按 IP 试用状态判定（试用到期拒绝）。</summary>
    private async Task<string?> ValidateApplicantAsync(string? licenseKey, string? ipAddress, CancellationToken ct)
    {
        if (!_switches.EnableAuth) return null;
        if (!string.IsNullOrWhiteSpace(licenseKey))
        {
            var status = await _licenses.InspectAsync(licenseKey, ct);
            return status.Status == "active" ? null : "当前密钥不可用，无法申请联机。";
        }
        // 无密钥 = 试用用户：无法取得 IP 时宽松放行（避免内网/特殊部署误拦截）。
        if (string.IsNullOrWhiteSpace(ipAddress)) return null;
        var trial = await _ipTrials.GetTrialStatusAsync(ipAddress, ct);
        return trial.Expired || trial.RemainingGenerations <= 0
            ? "试用已到期，无法申请联机，请获取密钥后重试。"
            : null;
    }

    /// <summary>取已完成的审批结果（房主已处理、好友延迟连接的兜底）；仍未处理时刷新申请人的在线时间并返回 null。</summary>
    public string? GetApplyResult(string applyId)
    {
        if (_applyResults.TryGetValue(applyId, out var entry)) return entry.Json;
        // 成员仍在等待：每次轮询刷新在线时间，房主端据此判定「成员离线」。
        if (_applyToRoom.TryGetValue(applyId, out var roomId)
            && _rooms.TryGetValue(roomId, out var room)
            && room.Pending.TryGetValue(applyId, out var apply))
        {
            apply.LastSeenAt = DateTime.UtcNow;
        }
        return null;
    }

    /// <summary>房主审批好友申请；同意后创建成员并通过审批通道下发批准事件（含快照）。</summary>
    public (bool Ok, string? Error) Decide(string hostToken, string applyId, bool accept)
    {
        var room = GetRoomByToken(hostToken, requireHost: true)
            ?? throw new InvalidOperationException("联机状态已失效，请重新发起联机。");
        lock (room.SyncRoot)
        {
        if (!room.Pending.TryGetValue(applyId, out var apply))
            return (false, "联机申请已失效。");

        // 原子抢占：仅 TryRemove 成功的请求有权处理该申请，防止并发审批对同一申请重复创建成员导致房间超员。
        if (!room.Pending.TryRemove(applyId, out _))
            return (false, "联机申请已失效。");

        // 成员离线：申请作废。房主点击「同意」时给出离线提示（不创建成员）；「拒绝」则直接删除消息、无需提示。
        if (accept && DateTime.UtcNow - apply.LastSeenAt > ApplyOfflineTtl)
        {
            RemoveApplyChannel(applyId);
            PushApplyResult(applyId, Serialize(new { type = "expired", reason = "member_offline" }));
            BroadcastRoom(room);
            return (false, "该成员已离线，申请已自动取消。");
        }

        if (!accept)
        {
            RemoveApplyChannel(applyId);
            PushApplyResult(applyId, Serialize(new { type = "rejected", reason = "host_rejected" }));
            BroadcastRoom(room);
            return (true, null);
        }

        if (room.IsFull)
        {
            RemoveApplyChannel(applyId);
            PushApplyResult(applyId, Serialize(new { type = "rejected", reason = "room_full" }));
            BroadcastRoom(room);
            return (false, "房间人数已满（最多 5 人）。");
        }

        var memberId = NewToken(8);
        var memberToken = NewToken(24);
        // 颜色复用：成员进出后优先取 1..4 中当前未使用的颜色，避免颜色耗尽后新成员与房主同色/名称退化为「好友N」。
        var usedColors = new HashSet<int>(room.Members.Values.Where(m => !m.IsHost).Select(m => m.ColorIndex));
        var colorIndex = 1;
        while (colorIndex <= 4 && usedColors.Contains(colorIndex)) colorIndex++;
        if (colorIndex > 4) colorIndex = room.NextMemberColor++; // 4 色全占（正常不会发生）时继续递增兜底
        var member = new CollabMember
        {
            MemberId = memberId,
            Token = memberToken,
            Name = MemberColorName(colorIndex),
            ColorIndex = colorIndex,
            IsHost = false,
            JoinedAt = DateTime.UtcNow,
            LicenseKey = apply.LicenseKey,
            IpAddress = apply.IpAddress,
            LastEntitlementCheckAt = DateTime.UtcNow,
            // 成员默认权限：编辑权限默认关闭（只读，由房主按需开启），共享权限默认开启（可保存/导出）。
            CanEdit = false,
            CanSave = true,
        };
        room.Members[memberId] = member;
        _tokenToRoom[memberToken] = room.RoomId;
        // apply 已在抢占时从 Pending 移除；清理旧通道/结果映射后写入新结果。
        RemoveApplyChannel(applyId);

        // 审批通过：一次性下发成员身份与房主豆板快照，好友端据此进入联机画布。
        PushApplyResult(applyId, Serialize(new
        {
            type = "approved",
            seq = room.EditSeq,
            boardRevision = room.BoardRevision,
            member = new { member.MemberId, member.Name, member.ColorIndex, member.Token, member.CanEdit, member.CanSave },
            room = BuildRoomDto(room, includeHostPrivateData: false),
            snapshot = BuildSnapshotDto(room.Snapshot),
        }));
        BroadcastRoom(room);
        return (true, null);
        }
    }

    // ---------- 成员权限申请 / 房主审批 ----------

    /// <summary>
    /// 成员申请编辑/共享权限：校验成员身份、是否已拥有该权限、30 秒冷却与授权状态，
    /// 创建待审批申请并广播 perm_apply 通知房主。返回申请 ID；失败返回错误信息。
    /// </summary>
    public async Task<(string? ApplyId, string? Error)> ApplyPermissionAsync(string token, string perm, CancellationToken ct)
    {
        var room = GetRoomByToken(token);
        if (room is null) return (null, "联机状态已失效。");
        var member = room.Members.Values.FirstOrDefault(m => m.Token == token);
        if (member is null) return (null, "联机状态已失效。");
        if (member.IsHost) return (null, "房主无需申请权限。");
        if (perm is not ("edit" or "save")) return (null, "权限类型不正确。");
        var normalized = perm;
        if (normalized == "edit" && member.CanEdit) return (null, "你已拥有编辑权限。");
        if (normalized == "save" && member.CanSave) return (null, "你已拥有共享权限。");
        // 成员授权校验：试用到期 / 密钥不可用时拒绝申请权限。
        var entitleError = await ValidateApplicantAsync(member.LicenseKey, member.IpAddress, ct);
        if (entitleError is not null) return (null, entitleError);
        CollabPermApply apply;
        lock (room.SyncRoot)
        {
            if (!room.Members.ContainsKey(member.MemberId)) return (null, "联机状态已失效。");
            var coolKey = member.MemberId + ":" + normalized;
            if (_lastPermApplyAt.TryGetValue(coolKey, out var last) && DateTime.UtcNow - last < PermApplyCoolDownTtl)
                return (null, "申请过于频繁，请 1 分钟后再试。");
            if (room.PendingPerms.Values.Any(p => p.MemberId == member.MemberId && p.Perm == normalized && !p.Handled))
                return (null, "已有待审批的权限申请，请等待房主处理。");
            var applyId = NewToken(12);
            apply = new CollabPermApply
            {
                ApplyId = applyId,
                RoomId = room.RoomId,
                MemberId = member.MemberId,
                Name = member.Name,
                ColorIndex = member.ColorIndex,
                Perm = normalized,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(PermApplyWaitTtl),
                LastSeenAt = DateTime.UtcNow,
            };
            room.PendingPerms[applyId] = apply;
            _lastPermApplyAt[coolKey] = DateTime.UtcNow;
        }
        // 通知房主：新权限申请（独立于联机申请的铃铛消息），携带申请者豆子颜色与过期时间（房主端据此显示 30 秒倒计时）。
        BroadcastRoom(room, Serialize(new { type = "perm_apply", applyId = apply.ApplyId, memberId = member.MemberId, name = member.Name, colorIndex = member.ColorIndex, perm = normalized, expiresAt = new DateTimeOffset(apply.ExpiresAt).ToUnixTimeMilliseconds() }));
        BroadcastRoom(room);
        return (apply.ApplyId, null);
    }

    /// <summary>房主审批成员权限申请；同意后设置对应权限并广播，结果通知成员。</summary>
    public (bool Ok, string? Error) DecidePermission(string hostToken, string applyId, bool accept)
    {
        var room = GetRoomByToken(hostToken, requireHost: true)
            ?? throw new InvalidOperationException("联机状态已失效，请重新发起联机。");
        lock (room.SyncRoot)
        {
        // 原子抢占：仅 TryRemove 成功的请求有权处理，防止并发审批重复设置。
        if (!room.PendingPerms.TryRemove(applyId, out var apply))
            return (false, "该权限申请不存在或已被处理。");
        // 成员已离线/被移出房间：申请作废，房主端通知清空。
        if (!room.Members.TryGetValue(apply.MemberId, out var member))
        {
            BroadcastRoom(room, Serialize(new { type = "perm_removed", applyId, memberId = apply.MemberId, reason = "member_offline" }));
            BroadcastRoom(room);
            return (false, "该成员已离线，权限申请已取消。");
        }
        if (accept)
        {
            if (apply.Perm == "edit") member.CanEdit = true;
            else member.CanSave = true;
            // 广播权限变更（复用既有 permission/savepermission 事件）并通知申请结果。
            BroadcastRoom(room, Serialize(new
            {
                type = apply.Perm == "edit" ? "permission" : "savepermission",
                memberId = member.MemberId,
                canEdit = member.CanEdit,
                canSave = member.CanSave,
            }));
            BroadcastRoom(room);
        }
        // 申请已处理：通知房主从待审批列表移除该条（铃铛即时清空），并通知成员申请结果。
        BroadcastRoom(room, Serialize(new { type = "perm_removed", applyId, memberId = apply.MemberId, reason = "decided" }));
        BroadcastRoom(room, Serialize(new { type = "perm_decided", applyId, memberId = apply.MemberId, perm = apply.Perm, accepted = accept }));
        return (true, null);
        }
    }

    // ---------- 成员替换图纸申请 / 房主审批 ----------

    /// <summary>
    /// 成员生成/加载自家图纸后申请替换整张联机画布：校验成员身份、1 分钟冷却与授权状态，
    /// 创建待审批申请并广播 replace_apply 通知房主；房主同意后全房间同步新图纸。
    /// </summary>
    public async Task<(string? ApplyId, string? Error)> ReplaceRequestAsync(string token, CollabSnapshot snapshot, CancellationToken ct)
    {
        var room = GetRoomByToken(token);
        if (room is null) return (null, "联机状态已失效。");
        var member = room.Members.Values.FirstOrDefault(m => m.Token == token);
        if (member is null) return (null, "联机状态已失效。");
        if (member.IsHost) return (null, "房主可直接替换图纸，无需申请。");
        ValidateSnapshot(snapshot);
        // 成员授权校验：试用到期 / 密钥不可用时拒绝申请替换。
        var entitleError = await ValidateApplicantAsync(member.LicenseKey, member.IpAddress, ct);
        if (entitleError is not null) return (null, entitleError);
        CollabReplace replace;
        lock (room.SyncRoot)
        {
            if (!room.Members.ContainsKey(member.MemberId)) return (null, "联机状态已失效。");
            if (_lastReplaceAt.TryGetValue(member.MemberId, out var last) && DateTime.UtcNow - last < ReplaceCooldownTtl)
                return (null, "替换申请过于频繁，请 1 分钟后再试。");
            if (room.PendingReplaces.Values.Any(r => r.MemberId == member.MemberId && !r.Handled))
                return (null, "已有待审批的替换申请，请等待房主处理。");
            var applyId = NewToken(12);
            replace = new CollabReplace
            {
                ApplyId = applyId,
                RoomId = room.RoomId,
                MemberId = member.MemberId,
                Name = member.Name,
                ColorIndex = member.ColorIndex,
                Snapshot = CloneSnapshot(snapshot),
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(ReplaceWaitTtl),
            };
            room.PendingReplaces[applyId] = replace;
            _lastReplaceAt[member.MemberId] = DateTime.UtcNow;
        }
        // 通知房主：新替换申请（x豆 申请替换当前图纸），携带申请者豆子颜色与过期时间（房主端据此显示 1 分钟倒计时）。
        BroadcastRoom(room, Serialize(new { type = "replace_apply", applyId = replace.ApplyId, memberId = member.MemberId, name = member.Name, colorIndex = member.ColorIndex, expiresAt = new DateTimeOffset(replace.ExpiresAt).ToUnixTimeMilliseconds() }));
        BroadcastRoom(room);
        return (replace.ApplyId, null);
    }

    /// <summary>房主审批成员替换图纸申请；同意后整体替换房间快照并广播 resync（房主 + 所有成员同步更新后的图纸）。</summary>
    public (bool Ok, string? Error) ReplaceDecide(string hostToken, string applyId, bool accept)
    {
        var room = GetRoomByToken(hostToken, requireHost: true)
            ?? throw new InvalidOperationException("联机状态已失效，请重新发起联机。");
        lock (room.SyncRoot)
        {
        // 原子抢占：仅 TryRemove 成功的请求有权处理，防止并发审批重复应用。
        if (!room.PendingReplaces.TryRemove(applyId, out var replace))
            return (false, "该替换申请不存在或已被处理。");
        // 成员已离线/被移出房间：申请作废，房主端通知清空。
        if (!room.Members.TryGetValue(replace.MemberId, out _))
        {
            BroadcastRoom(room, Serialize(new { type = "replace_removed", applyId, memberId = replace.MemberId, reason = "member_offline" }));
            BroadcastRoom(room);
            return (false, "该成员已离线，替换申请已取消。");
        }
        if (accept)
        {
            // 房主同意：整体替换房间权威快照并广播 resync，房主与所有联机成员同步更新后的图纸。
            room.Snapshot = CloneSnapshot(replace.Snapshot);
            room.Locks.Clear();
            ResetHistory(room);
            var seq = ++room.EditSeq;
            BroadcastRoom(room, Serialize(new { type = "resync", seq, boardRevision = room.BoardRevision, snapshot = BuildSnapshotDto(room.Snapshot) }));
        }
        // 通知房主从待审批队列移除该条，并通知申请成员审批结果。
        BroadcastRoom(room, Serialize(new { type = "replace_removed", applyId, memberId = replace.MemberId, accepted = accept }));
        BroadcastRoom(room);
        return (true, null);
        }
    }

    // ---------- 踢出 / 退出 / 房间关闭 ----------

    /// <summary>房主踢出联机成员。</summary>
    public bool Kick(string hostToken, string memberId)
    {
        var room = GetRoomByToken(hostToken, requireHost: true)
            ?? throw new InvalidOperationException("联机状态已失效，请重新发起联机。");
        if (string.IsNullOrEmpty(memberId) || memberId == room.HostMemberId) return false;
        return KickMember(room, memberId, "host_kicked");
    }

    /// <summary>房主单独开启/关闭某个成员的编辑权限；返回是否设置成功。</summary>
    public bool SetPermission(string hostToken, string memberId, bool canEdit)
    {
        var room = GetRoomByToken(hostToken, requireHost: true)
            ?? throw new InvalidOperationException("联机状态已失效，请重新发起联机。");
        lock (room.SyncRoot)
        {
        if (string.IsNullOrEmpty(memberId) || memberId == room.HostMemberId || !room.Members.TryGetValue(memberId, out var member))
            return false;
        if (member.CanEdit == canEdit) return true;
        member.CanEdit = canEdit;
        if (!canEdit)
        {
            // 权限关闭后立即释放该成员的格子锁，避免其他人继续被已只读成员阻塞到 TTL 到期。
            foreach (var pair in room.Locks)
                if (pair.Value.MemberId == memberId) room.Locks.TryRemove(pair.Key, out _);
        }
        // 权限变更即时广播：目标成员端据此刻画板为只读，其余成员同步成员列表状态。
        BroadcastRoom(room);
        BroadcastRoom(room, Serialize(new { type = "permission", memberId, canEdit }));
        return true;
        }
    }

    /// <summary>房主单独开启/关闭某个成员的保存共享权限；返回是否设置成功。</summary>
    public bool SetSavePermission(string hostToken, string memberId, bool canSave)
    {
        var room = GetRoomByToken(hostToken, requireHost: true)
            ?? throw new InvalidOperationException("联机状态已失效，请重新发起联机。");
        lock (room.SyncRoot)
        {
        if (string.IsNullOrEmpty(memberId) || memberId == room.HostMemberId || !room.Members.TryGetValue(memberId, out var member))
            return false;
        if (member.CanSave == canSave) return true;
        member.CanSave = canSave;
        // 保存/导出权限变更即时广播：目标成员端据此隐藏/显示保存、导出入口，其余成员同步成员列表状态。
        BroadcastRoom(room);
        BroadcastRoom(room, Serialize(new { type = "savepermission", memberId, canSave }));
        return true;
        }
    }

    /// <summary>成员主动退出；房主退出时关闭整个房间并踢出所有好友。</summary>
    public bool Leave(string token)
    {
        var room = GetRoomByToken(token);
        if (room is null) return false;
        var member = room.Members.Values.FirstOrDefault(m => m.Token == token);
        if (member is null) return false;
        if (member.IsHost)
        {
            CloseRoom(room.RoomId, "host_left");
            return true;
        }
        return KickMember(room, member.MemberId, "self_left");
    }

    // ---------- 编辑同步 ----------

    /// <summary>应用成员编辑：同格被他人锁定时拒绝（回滚到权威值），其余应用到快照并广播。
    /// 返回 { seq, edits, reverts, locks } 供请求方即时回滚，广播会推给房间全部成员。</summary>
    public (long Seq, CollabEditItem[] Edits, CollabEditItem[] Reverts, CollabLockEvent[] Locks, bool CanUndo, bool CanRedo, string? Error) SubmitEdits(
        string token,
        string? operationId,
        long boardRevision,
        CollabEditItem[] edits)
    {
        var room = GetRoomByToken(token);
        if (room is null || room.Snapshot is null)
            return (0, [], [], [], false, false, "联机状态已失效。");
        lock (room.SyncRoot)
        {
            var member = room.Members.Values.FirstOrDefault(m => m.Token == token);
            var snapshot = room.Snapshot;
            if (member is null || snapshot is null)
                return (0, [], [], [], false, false, "联机状态已失效。");
            var now = DateTime.UtcNow;
            var canUndo = HasCurrentLockedHistory(room, room.UndoHistory, member.MemberId, now);
            var canRedo = HasCurrentLockedHistory(room, room.RedoHistory, member.MemberId, now);
            // 编辑必须基于当前整板版本；缺失版本（默认值 0）同样拒绝，避免旧页面缓存绕过清空/换图屏障。
            if (boardRevision != room.BoardRevision)
                return (room.EditSeq, [], [], [], false, false, "图纸版本已更新，正在同步最新画布。");
            var cellCount = snapshot.Cells.Length;
            // 房主关闭了该成员的编辑权限：拒绝本次全部写入并回滚到同一个权威快照。
            if (!member.CanEdit)
            {
                var blocked = edits.Where(e => e.Index >= 0 && e.Index < cellCount)
                    .Select(e => new CollabEditItem { Index = e.Index, ColorIndex = snapshot.Cells[e.Index] })
                    .ToArray();
                return (room.EditSeq, [], blocked, [], canUndo, canRedo, "该成员的编辑权限已被房主关闭。");
            }

            var accepted = new List<CollabEditItem>(Math.Min(edits.Length, MaxEditsPerRequest));
            var reverts = new List<CollabEditItem>();
            var lockedCells = new Dictionary<string, List<int>>();
            var lockTtl = TimeSpan.FromSeconds(LockTtlSeconds);
            var safeOperationId = NormalizeOperationId(operationId);
            CollabHistoryOperation? historyOperation = null;

            var count = 0;
            foreach (var edit in edits)
            {
                if (++count > MaxEditsPerRequest) break;
                if (edit.Index < 0 || edit.Index >= cellCount || edit.ColorIndex < -1 || edit.ColorIndex >= snapshot.Colors.Length) continue;
                // 冲突仲裁：该格仍被其他成员有效锁定时拒绝本次写入。
                if (room.Locks.TryGetValue(edit.Index, out var existing) && existing.ExpiresAt > now && existing.MemberId != member.MemberId)
                {
                    reverts.Add(new CollabEditItem { Index = edit.Index, ColorIndex = snapshot.Cells[edit.Index] });
                    continue;
                }
                var previous = snapshot.Cells[edit.Index];
                if (previous == edit.ColorIndex) continue;

                historyOperation ??= GetOrCreateHistoryOperation(room, member.MemberId, safeOperationId);
                var mutationVersion = ++room.MutationVersion;
                if (historyOperation.Changes.TryGetValue(edit.Index, out var priorChange))
                {
                    priorChange.After = edit.ColorIndex;
                    priorChange.AppliedVersion = mutationVersion;
                }
                else
                {
                    historyOperation.Changes[edit.Index] = new CollabCellChange
                    {
                        Index = edit.Index,
                        Before = previous,
                        After = edit.ColorIndex,
                        AppliedVersion = mutationVersion,
                    };
                }

                snapshot.Cells[edit.Index] = edit.ColorIndex;
                room.CellVersions[edit.Index] = mutationVersion;
                room.CellLastEditors[edit.Index] = member.MemberId;
                room.Locks[edit.Index] = new CellLock { MemberId = member.MemberId, ExpiresAt = now + lockTtl };
                accepted.Add(edit);
                if (!lockedCells.TryGetValue(member.MemberId, out var list))
                    lockedCells[member.MemberId] = list = new List<int>();
                list.Add(edit.Index);
            }

            canUndo = HasCurrentLockedHistory(room, room.UndoHistory, member.MemberId, now);
            canRedo = HasCurrentLockedHistory(room, room.RedoHistory, member.MemberId, now);
            if (accepted.Count == 0 && reverts.Count == 0)
                return (room.EditSeq, [], [], [], canUndo, canRedo, null);

            var seq = ++room.EditSeq;
            var locks = lockedCells.Select(pair =>
            {
                var owner = room.Members.TryGetValue(pair.Key, out var m) ? m : null;
                return new CollabLockEvent(pair.Key, owner?.ColorIndex ?? 0, pair.Value.ToArray());
            }).ToArray();

            var payload = new CollabEditsEvent(seq, accepted.ToArray(), reverts.ToArray(), locks);
            BroadcastRoom(room, Serialize(payload));
            BroadcastRoom(room, Serialize(new { type = "history", memberId = member.MemberId, canUndo, canRedo }));
            return (seq, payload.Edits, payload.Reverts, payload.Locks, canUndo, canRedo, null);
        }
    }

    /// <summary>
    /// 撤销或恢复当前成员自己的最近一次编辑。房主与成员使用完全相同的限制：
    /// 仅处理当前仍由本人有效锁定、且最后编辑者仍是本人的格子，不覆盖其他人的编辑。
    /// </summary>
    public (long Seq, CollabEditItem[] Edits, bool CanUndo, bool CanRedo, string? Error) ApplyOwnHistory(string token, string action)
    {
        var room = GetRoomByToken(token);
        if (room is null) return (0, [], false, false, "联机状态已失效。");
        lock (room.SyncRoot)
        {
            var member = room.Members.Values.FirstOrDefault(item => item.Token == token);
            var snapshot = room.Snapshot;
            if (member is null || snapshot is null) return (0, [], false, false, "联机状态已失效。");
            if (!member.CanEdit) return (room.EditSeq, [], false, false, "当前没有编辑权限。");

            var now = DateTime.UtcNow;
            var isUndo = string.Equals(action, "undo", StringComparison.Ordinal);
            if (!isUndo && !string.Equals(action, "redo", StringComparison.Ordinal))
                return (room.EditSeq, [],
                    HasCurrentLockedHistory(room, room.UndoHistory, member.MemberId, now),
                    HasCurrentLockedHistory(room, room.RedoHistory, member.MemberId, now),
                    "历史操作类型不正确。");

            var source = GetHistoryStack(isUndo ? room.UndoHistory : room.RedoHistory, member.MemberId);
            var target = GetHistoryStack(isUndo ? room.RedoHistory : room.UndoHistory, member.MemberId);
            CollabHistoryOperation? operation = null;
            // 从栈顶直接跳过已失锁或已被覆盖的历史，避免用户反复点击才能命中仍可处理的记录。
            // 跳过的记录不再保留：格子锁失效后，本规则明确禁止它再次参与撤销或恢复。
            while (source.Count > 0)
            {
                var candidate = source[^1];
                source.RemoveAt(source.Count - 1);
                if (candidate.Changes.Values.Any(change => IsCurrentLockedCell(room, member.MemberId, change.Index, now)))
                {
                    operation = candidate;
                    break;
                }
            }

            if (operation is null)
            {
                var emptyCanUndo = HasCurrentLockedHistory(room, room.UndoHistory, member.MemberId, now);
                var emptyCanRedo = HasCurrentLockedHistory(room, room.RedoHistory, member.MemberId, now);
                BroadcastRoom(room, Serialize(new { type = "history", memberId = member.MemberId, canUndo = emptyCanUndo, canRedo = emptyCanRedo }));
                return (room.EditSeq, [], emptyCanUndo, emptyCanRedo,
                    isUndo ? "当前没有由你锁定的格子可撤销。" : "当前没有由你锁定的格子可恢复。");
            }

            var applied = new CollabHistoryOperation { OperationId = operation.OperationId, MemberId = operation.MemberId };
            var edits = new List<CollabEditItem>(operation.Changes.Count);
            foreach (var change in operation.Changes.Values.OrderBy(item => item.Index))
            {
                if (!IsCurrentLockedCell(room, member.MemberId, change.Index, now))
                    continue;

                var nextValue = isUndo ? change.Before : change.After;
                var version = ++room.MutationVersion;
                snapshot.Cells[change.Index] = nextValue;
                room.CellVersions[change.Index] = version;
                // 撤销/恢复后本人成为该格最后编辑者，后续撤销/恢复仍可继续作用于本格。
                room.CellLastEditors[change.Index] = member.MemberId;
                var appliedChange = new CollabCellChange
                {
                    Index = change.Index,
                    Before = change.Before,
                    After = change.After,
                    AppliedVersion = isUndo ? change.AppliedVersion : version,
                    UndoVersion = isUndo ? version : change.UndoVersion,
                };
                applied.Changes[change.Index] = appliedChange;
                edits.Add(new CollabEditItem { Index = change.Index, ColorIndex = nextValue });
            }

            if (applied.Changes.Count > 0)
            {
                target.Add(applied);
                TrimHistory(target);
            }
            var canUndo = HasCurrentLockedHistory(room, room.UndoHistory, member.MemberId, now);
            var canRedo = HasCurrentLockedHistory(room, room.RedoHistory, member.MemberId, now);
            if (edits.Count == 0)
            {
                BroadcastRoom(room, Serialize(new { type = "history", memberId = member.MemberId, canUndo, canRedo }));
                return (room.EditSeq, [], canUndo, canRedo, isUndo ? "当前没有由你锁定的格子可撤销。" : "当前没有由你锁定的格子可恢复。");
            }

            // 历史操作不续期也不清锁，沿用原锁的剩余有效期，并保留房间内其他人的锁。
            var seq = ++room.EditSeq;
            BroadcastRoom(room, Serialize(new CollabEditsEvent(seq, edits.ToArray(), [], [])));
            BroadcastRoom(room, Serialize(new { type = "history", memberId = member.MemberId, canUndo, canRedo }));
            return (seq, edits.ToArray(), canUndo, canRedo, null);
        }
    }

    // ---------- 房主清空共享画布 ----------

    /// <summary>
    /// 房主清空整块共享画布：校验房主身份后清空全部格子与锁，并向所有成员广播 clear 事件（成员端收到后整体清空本地画布）。
    /// 返回错误信息；成功返回 null。
    /// </summary>
    public string? Clear(string token)
    {
        var room = GetRoomByToken(token);
        if (room is null || room.Snapshot is null)
            return "联机状态已失效。";
        lock (room.SyncRoot)
        {
        var member = room.Members.Values.FirstOrDefault(m => m.Token == token);
        if (member is null) return "联机状态已失效。";
        // 仅房主可清空共享画布，防止成员误清空全体。
        if (!member.IsHost) return "只有房主可以清空共享画布。";
        // 即使画布已为空也必须建立新的整板版本屏障，阻止其他成员在清空前排队的旧编辑随后写回。
        Array.Fill(room.Snapshot.Cells, -1);
        room.Locks.Clear();
        ResetHistory(room);
        var seq = ++room.EditSeq;
        BroadcastToRoom(room.RoomId, Serialize(new { type = "clear", seq, boardRevision = room.BoardRevision }));
        return null;
        }
    }

    // ---------- 房主重发整块画布（联机数据同步） ----------

    /// <summary>
    /// 房主重新生成/替换整块画布（重新生成图纸、新建、导入等）后，把新快照同步给房间全部成员：
    /// 更新服务端权威快照并广播 resync 事件，成员端整体替换本地画布，保证联机豆板数据统一。
    /// </summary>
    public string? ResyncSnapshot(string token, CollabSnapshot snapshot)
    {
        var room = GetRoomByToken(token, requireHost: true)
            ?? throw new InvalidOperationException("联机状态已失效，请重新发起联机。");
        ValidateSnapshot(snapshot);
        lock (room.SyncRoot)
        {
            room.Snapshot = CloneSnapshot(snapshot);
            room.Locks.Clear();
            ResetHistory(room);
            var seq = ++room.EditSeq;
            BroadcastRoom(room, Serialize(new { type = "resync", seq, boardRevision = room.BoardRevision, snapshot = BuildSnapshotDto(room.Snapshot) }));
            BroadcastRoom(room);
        }
        return null;
    }

    // ---------- SSE 订阅 ----------

    /// <summary>校验成员令牌并订阅房间事件流；返回房间（用于初始快照下发）或 null。</summary>
    public CollabRoom? Subscribe(string token, Channel<string> channel, out string memberId, out string? error)
    {
        memberId = "";
        error = null;
        var room = GetRoomByToken(token);
        if (room is null) return null;
        lock (room.SyncRoot)
        {
            var member = room.Members.Values.FirstOrDefault(m => m.Token == token);
            if (member is null) return null;
            memberId = member.MemberId;
            var subscribers = _roomChannels.GetOrAdd(room.RoomId, _ => new ConcurrentDictionary<Channel<string>, string>());
            if (subscribers.Values.Count(id => id == member.MemberId) >= MaxSseConnectionsPerMember)
            {
                error = $"同一成员最多建立 {MaxSseConnectionsPerMember} 条实时连接。";
                return null;
            }
            subscribers[channel] = member.MemberId;
            _roomLastActive[room.RoomId] = DateTime.UtcNow;
            // 建立 SSE 连接即视为在线；计数覆盖刷新和少量多标签页，同时设置单成员硬上限防止连接滥用。
            if (member.IsHost)
            {
                room.HostSseCount++;
                room.HostOfflineSince = null;
            }
            else
            {
                member.SseCount++;
                member.OfflineSince = null;
            }
            return room;
        }
    }

    public void Unsubscribe(string roomId, string memberId, Channel<string> channel)
    {
        if (!_rooms.TryGetValue(roomId, out var room)) return;
        lock (room.SyncRoot)
        {
            // 通道可能已因队列积压被广播器提前移除，但 Pump 的 finally 仍负责唯一一次在线计数回收。
            if (_roomChannels.TryGetValue(roomId, out var subscribers))
            {
                subscribers.TryRemove(channel, out _);
                if (subscribers.IsEmpty) _roomChannels.TryRemove(roomId, out _);
            }
            _roomLastActive[roomId] = DateTime.UtcNow;
            // 房主最后一条 SSE 断开时记录离线起始时间；Sweep 中超过阈值才关闭房间，避免刷新误关。
            if (memberId == room.HostMemberId)
            {
                if (room.HostSseCount > 0) room.HostSseCount--;
                if (room.HostSseCount <= 0) room.HostOfflineSince = DateTime.UtcNow;
                return;
            }
            // 成员最后一条 SSE 断开时记录离线起始时间；Sweep 中超过阈值才移出房间，避免刷新误判。
            if (room.Members.TryGetValue(memberId, out var offlineMember) && !offlineMember.IsHost)
            {
                if (offlineMember.SseCount > 0) offlineMember.SseCount--;
                if (offlineMember.SseCount <= 0) offlineMember.OfflineSince = DateTime.UtcNow;
            }
        }
    }

    /// <summary>按令牌查询房间与成员（供重连状态等使用）。</summary>
    public CollabRoom? GetRoomByToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (!_tokenToRoom.TryGetValue(token, out var roomId)) return null;
        _rooms.TryGetValue(roomId, out var room);
        return room;
    }

    /// <summary>当前房间状态事件 JSON（SSE 建立连接后立即下发一次，成员端据此同步成员列表）。</summary>
    public string? GetRoomEventJson(string token)
    {
        var room = GetRoomByToken(token);
        if (room is null) return null;
        lock (room.SyncRoot)
        {
            var member = room.Members.Values.FirstOrDefault(item => item.Token == token);
            return member is null ? null : Serialize(BuildRoomEvent(room, member.IsHost));
        }
    }

    /// <summary>SSE 建立连接后下发的初始状态：房间 + 豆板快照 + 本人信息（重连兜底，含授权过期前的最后数据）。</summary>
    public string? GetInitialStateJson(string token)
    {
        var room = GetRoomByToken(token);
        if (room is null) return null;
        lock (room.SyncRoot)
        {
            var member = room.Members.Values.FirstOrDefault(m => m.Token == token);
            if (member is null) return null;
            var now = DateTime.UtcNow;
            return Serialize(new
            {
                type = "state",
                seq = room.EditSeq,
                boardRevision = room.BoardRevision,
                room = BuildRoomDto(room, member.IsHost),
                snapshot = BuildSnapshotDto(room.Snapshot),
                member = new
                {
                    member.MemberId,
                    member.Name,
                    member.ColorIndex,
                    member.IsHost,
                    member.CanEdit,
                    member.CanSave,
                    canUndo = HasCurrentLockedHistory(room, room.UndoHistory, member.MemberId, now),
                    canRedo = HasCurrentLockedHistory(room, room.RedoHistory, member.MemberId, now),
                },
            });
        }
    }

    // ---------- 授权到期清扫（由后台服务定时调用） ----------

    /// <summary>周期清扫：房主授权过期关闭房间；好友授权过期自动踢出；清理过期格子锁与过期申请。</summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // 清理过期的申请审批结果。
        foreach (var pair in _applyResults)
            if (pair.Value.ExpiresAt <= now) _applyResults.TryRemove(pair.Key, out _);
        // 清理超时未处理的待审批申请；同时做房主离线检测。
        foreach (var room in _rooms.Values)
        {
            if (ct.IsCancellationRequested) return;
            // 注意：已处理的申请保留到 CreatedAt + ApplyResultTtl，确保好友轮询仍能取到审批结果，
            // 不能因为 handled 就立即清掉结果，否则会错过房主刚批准的好友。
            foreach (var pair in room.Pending)
            {
                // 成员等待审批期间离线（超过 15 秒未轮询）：申请自动作废，房主端列表即时移除并提示。
                if (now - pair.Value.LastSeenAt > ApplyOfflineTtl)
                {
                    // TryRemove 失败说明申请已被房主审批抢占，此时不能再作废/覆盖其结果。
                    if (!room.Pending.TryRemove(pair.Key, out _)) continue;
                    RemoveApplyChannel(pair.Key);
                    BroadcastRoom(room, Serialize(new { type = "apply_removed", applyId = pair.Key, name = pair.Value.Name, reason = "member_offline" }));
                    BroadcastRoom(room);
                }
                else if (pair.Value.ExpiresAt <= now)
                {
                    // TryRemove 失败说明申请已被房主审批抢占，此时不能再推送「超时」覆盖审批结果。
                    if (!room.Pending.TryRemove(pair.Key, out _)) continue;
                    RemoveApplyChannel(pair.Key);
                    // 房主 30 秒内未响应：通知成员「房主未响应」，可再次发起申请；同时广播刷新房主申请列表。
                    PushApplyResult(pair.Key, Serialize(new { type = "expired", reason = "host_no_response" }));
                    BroadcastRoom(room);
                }
            }
            // 权限申请清理：成员离线（不在房间/离线超阈值）对应申请消息清空；超时未审批作废并通知成员。
            foreach (var pair in room.PendingPerms)
            {
                var memberExists = room.Members.TryGetValue(pair.Value.MemberId, out var permMember);
                var memberOffline = memberExists && permMember?.OfflineSince is { } offSince && now - offSince > MemberOfflineTtl;
                if (!memberExists || memberOffline)
                {
                    if (!room.PendingPerms.TryRemove(pair.Key, out _)) continue;
                    BroadcastRoom(room, Serialize(new { type = "perm_removed", applyId = pair.Key, memberId = pair.Value.MemberId, reason = "member_offline" }));
                    BroadcastRoom(room);
                }
                else if (pair.Value.ExpiresAt <= now)
                {
                    if (!room.PendingPerms.TryRemove(pair.Key, out _)) continue;
                    BroadcastRoom(room, Serialize(new { type = "perm_expired", applyId = pair.Key, memberId = pair.Value.MemberId, perm = pair.Value.Perm }));
                }
            }
            // 替换图纸申请清理：成员离线对应申请消息清空；1 分钟超时未审批自动退出消息队列并通知成员。
            foreach (var pair in room.PendingReplaces)
            {
                var replaceMemberExists = room.Members.TryGetValue(pair.Value.MemberId, out var replaceMember);
                var replaceMemberOffline = replaceMemberExists && replaceMember?.OfflineSince is { } replaceOff && now - replaceOff > MemberOfflineTtl;
                if (!replaceMemberExists || replaceMemberOffline)
                {
                    if (!room.PendingReplaces.TryRemove(pair.Key, out _)) continue;
                    BroadcastRoom(room, Serialize(new { type = "replace_removed", applyId = pair.Key, memberId = pair.Value.MemberId, reason = "member_offline" }));
                    BroadcastRoom(room);
                }
                else if (pair.Value.ExpiresAt <= now)
                {
                    if (!room.PendingReplaces.TryRemove(pair.Key, out _)) continue;
                    // 房主 1 分钟内未响应：申请自动退出消息队列，并通知申请成员「房主未响应，可稍后再次申请」。
                    BroadcastRoom(room, Serialize(new { type = "replace_removed", applyId = pair.Key, memberId = pair.Value.MemberId, reason = "expired" }));
                    BroadcastRoom(room);
                }
            }
            // 房主无活跃 SSE 连接超过阈值：视为离线，关闭房间并通知成员「房主已离线」。
            if (room.HostOfflineSince is { } offlineSince && now - offlineSince > HostOfflineTtl)
            {
                CloseRoom(room.RoomId, "host_offline");
                continue;
            }
            // 成员无活跃 SSE 连接超过阈值：视为离线，自动移出房间并通知房主（房主端成员列表即时移除）。
            foreach (var member in room.Members.Values.ToList())
            {
                if (member.IsHost) continue;
                if (member.OfflineSince is { } memberOfflineSince && now - memberOfflineSince > MemberOfflineTtl)
                    KickMember(room, member.MemberId, "member_offline");
            }
        }

        foreach (var room in _rooms.Values)
        {
            if (ct.IsCancellationRequested) return;
            var enableAuth = _switches.EnableAuth;
            // 房间所有成员（含房主）断线超过 2 分钟：判定闲置并关闭，防止异常退出导致内存堆积。
            if (!_roomChannels.ContainsKey(room.RoomId)
                && _roomLastActive.TryGetValue(room.RoomId, out var lastActive)
                && now - lastActive > TimeSpan.FromMinutes(2))
            {
                CloseRoom(room.RoomId, "idle_timeout");
                continue;
            }
            // 房主授权过期：整个房间关闭。
            if (enableAuth && !string.IsNullOrEmpty(room.HostLicenseKey))
            {
                var inspectHost = false;
                lock (room.SyncRoot)
                {
                    if (now - room.LastHostEntitlementCheckAt >= EntitlementCheckInterval)
                    {
                        room.LastHostEntitlementCheckAt = now;
                        inspectHost = true;
                    }
                }
                try
                {
                    if (inspectHost)
                    {
                        var status = await _licenses.InspectAsync(room.HostLicenseKey, ct);
                        if (status.Status != "active")
                        {
                            CloseRoom(room.RoomId, "host_license_expired");
                            continue;
                        }
                    }
                }
                catch
                {
                    // 数据库瞬时异常时跳过本轮，避免误踢。
                }
            }
            // 好友授权过期：逐个踢出。
            foreach (var member in room.Members.Values.ToList())
            {
                if (member.IsHost) continue;
                var inspectMember = false;
                lock (room.SyncRoot)
                {
                    if (now - member.LastEntitlementCheckAt >= EntitlementCheckInterval)
                    {
                        member.LastEntitlementCheckAt = now;
                        inspectMember = true;
                    }
                }
                if (!inspectMember) continue;
                // 试用成员（无密钥）：按来源 IP 判定试用是否到期，到期自动退出联机。
                if (string.IsNullOrEmpty(member.LicenseKey))
                {
                    if (enableAuth && !string.IsNullOrEmpty(member.IpAddress))
                    {
                        try
                        {
                            var trial = await _ipTrials.GetTrialStatusAsync(member.IpAddress, ct);
                            if (trial.Expired || trial.RemainingGenerations <= 0)
                                KickMember(room, member.MemberId, "trial_expired");
                        }
                        catch
                        {
                            // 数据库瞬时异常时跳过本轮，避免误踢。
                        }
                    }
                    continue;
                }
                try
                {
                    var status = await _licenses.InspectAsync(member.LicenseKey, ct);
                    if (status.Status != "active")
                        KickMember(room, member.MemberId, "license_expired");
                }
                catch
                {
                    // 同上，跳过本轮。
                }
            }
            // 锁清扫与编辑续锁共用房间锁，避免清扫线程按旧快照误删刚被续期的新锁。
            lock (room.SyncRoot)
            {
                foreach (var pair in room.Locks)
                    if (pair.Value.ExpiresAt <= now) room.Locks.TryRemove(pair.Key, out _);
            }
        }
    }

    // ---------- 内部实现 ----------

    private CollabRoom? GetRoomByToken(string token, bool requireHost)
    {
        var room = GetRoomByToken(token);
        if (room is null) return null;
        if (requireHost && !room.Members.Values.Any(m => m.Token == token && m.IsHost)) return null;
        return room;
    }

    private CollabRoom? FindRoomByInviteCode(string inviteCode)
    {
        // 邀请码为随机短码，房间数量有限（上限 MaxRooms），线性匹配成本可接受。
        foreach (var room in _rooms.Values)
        {
            if (string.Equals(room.InviteCode, inviteCode, StringComparison.OrdinalIgnoreCase)) return room;
        }
        return null;
    }

    private bool KickMember(CollabRoom room, string memberId, string reason)
    {
        CollabMember member;
        List<string> permIds;
        List<string> replaceIds;
        lock (room.SyncRoot)
        {
            if (!room.Members.TryRemove(memberId, out member!)) return false;
            if (member.Token != null) _tokenToRoom.TryRemove(member.Token, out _);
            foreach (var pair in room.Locks)
                if (pair.Value.MemberId == memberId) room.Locks.TryRemove(pair.Key, out _);
            permIds = room.PendingPerms.Where(p => p.Value.MemberId == memberId).Select(p => p.Key).ToList();
            replaceIds = room.PendingReplaces.Where(r => r.Value.MemberId == memberId).Select(r => r.Key).ToList();
            foreach (var id in permIds) room.PendingPerms.TryRemove(id, out _);
            foreach (var id in replaceIds) room.PendingReplaces.TryRemove(id, out _);
            room.UndoHistory.Remove(memberId);
            room.RedoHistory.Remove(memberId);
            _lastPermApplyAt.TryRemove(memberId + ":edit", out _);
            _lastPermApplyAt.TryRemove(memberId + ":save", out _);
            _lastReplaceAt.TryRemove(memberId, out _);
        }

        // 先向该成员自己的通道发送终止事件并关闭通道，避免已撤销令牌继续接收房间数据。
        CloseMemberSubscriptions(room.RoomId, memberId, Serialize(new { type = "kicked", memberId, reason, name = member.Name }));
        foreach (var permId in permIds)
            BroadcastRoom(room, Serialize(new { type = "perm_removed", applyId = permId, memberId, reason = "member_offline" }));
        foreach (var replaceId in replaceIds)
            BroadcastRoom(room, Serialize(new { type = "replace_removed", applyId = replaceId, memberId, reason = "member_offline" }));
        BroadcastRoom(room, Serialize(new { type = "kicked", memberId, reason, name = member.Name }));
        BroadcastRoom(room);
        return true;
    }

    private void CloseRoom(string roomId, string reason)
    {
        CollabRoom room;
        lock (_roomLifecycleLock)
        {
            if (!_rooms.TryRemove(roomId, out room!)) return;
        }
        lock (room.SyncRoot)
        {
            foreach (var member in room.Members.Values)
            {
                if (member.Token != null) _tokenToRoom.TryRemove(member.Token, out _);
                _lastPermApplyAt.TryRemove(member.MemberId + ":edit", out _);
                _lastPermApplyAt.TryRemove(member.MemberId + ":save", out _);
                _lastReplaceAt.TryRemove(member.MemberId, out _);
            }
        // 通知仍在等待审批的好友：房间已关闭（房主离线/退出等），轮询端据此给出明确提示。
        var closeResult = Serialize(new { type = "expired", reason });
        var pendingIds = room.Pending.Keys.ToList();
        room.Pending.Clear();
        foreach (var applyId in pendingIds)
        {
            _applyResults[applyId] = (closeResult, DateTime.UtcNow.Add(ApplyResultTtl));
            _applyChannels.TryRemove(applyId, out _);
            _applyToRoom.TryRemove(applyId, out _);
        }
        room.PendingPerms.Clear();
        room.PendingReplaces.Clear();
        room.UndoHistory.Clear();
        room.RedoHistory.Clear();
        }
        BroadcastToRoom(roomId, Serialize(new { type = "closed", reason }));
        CloseAllSubscriptions(roomId);
        _roomLastActive.TryRemove(roomId, out _);
    }

    private void CloseMemberSubscriptions(string roomId, string memberId, string terminalJson)
    {
        if (!_roomChannels.TryGetValue(roomId, out var subscribers)) return;
        foreach (var pair in subscribers.Where(item => item.Value == memberId).ToList())
        {
            pair.Key.Writer.TryWrite("data: " + terminalJson + "\n\n");
            if (subscribers.TryRemove(pair.Key, out _)) pair.Key.Writer.TryComplete();
        }
        if (subscribers.IsEmpty) _roomChannels.TryRemove(roomId, out _);
    }

    private void CloseAllSubscriptions(string roomId)
    {
        if (!_roomChannels.TryRemove(roomId, out var subscribers)) return;
        foreach (var channel in subscribers.Keys) channel.Writer.TryComplete();
    }

    private void BroadcastRoom(CollabRoom room, string? overrideJson = null)
    {
        if (overrideJson is not null)
        {
            BroadcastToRoom(room.RoomId, overrideJson);
            return;
        }
        if (!_roomChannels.TryGetValue(room.RoomId, out var subscribers)) return;
        var hostJson = Serialize(BuildRoomEvent(room, includeHostPrivateData: true));
        var memberJson = Serialize(BuildRoomEvent(room, includeHostPrivateData: false));
        foreach (var pair in subscribers)
            WriteToSubscriber(room.RoomId, subscribers, pair.Key, pair.Value == room.HostMemberId ? hostJson : memberJson);
    }

    private void BroadcastToRoom(string roomId, string json)
    {
        if (!_roomChannels.TryGetValue(roomId, out var subscribers)) return;
        // 统一包装成标准 SSE 事件行（data: <json>\n\n），与初始 state 事件格式一致，EventSource 才能解析。
        foreach (var channel in subscribers.Keys)
            WriteToSubscriber(roomId, subscribers, channel, json);
        if (subscribers.IsEmpty) _roomChannels.TryRemove(roomId, out _);
    }

    private void WriteToSubscriber(
        string roomId,
        ConcurrentDictionary<Channel<string>, string> subscribers,
        Channel<string> channel,
        string json)
    {
        var sse = "data: " + json + "\n\n";
        if (channel.Writer.TryWrite(sse)) return;
        // 队列满表示客户端已明显落后。关闭订阅后，前端会重新申请一次性票据并通过完整 state 快照恢复。
        if (subscribers.TryRemove(channel, out _)) channel.Writer.TryComplete();
        if (subscribers.IsEmpty) _roomChannels.TryRemove(roomId, out _);
    }

    private void PushApplyResult(string applyId, string json)
    {
        // 结果持久化保留一段时间：好友可能尚未建立 SSE 连接（延迟连接兜底）。
        _applyResults[applyId] = (json, DateTime.UtcNow.Add(ApplyResultTtl));
        if (_applyChannels.TryGetValue(applyId, out var channel))
            channel.Writer.TryWrite(json);
    }

    private void RemoveApplyChannel(string applyId)
    {
        _applyChannels.TryRemove(applyId, out _);
        _applyResults.TryRemove(applyId, out _);
        _applyToRoom.TryRemove(applyId, out _);
    }

    private static Channel<string> CreateChannel() =>
        Channel.CreateBounded<string>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait });

    private static string Serialize(object value) => JsonSerializer.Serialize(value, EventJsonOptions);

    private static object BuildRoomEvent(CollabRoom room, bool includeHostPrivateData) =>
        new { type = "room", room = BuildRoomDto(room, includeHostPrivateData) };

    public static object BuildRoomDto(CollabRoom room, bool includeHostPrivateData = true) => new
    {
        room.RoomId,
        InviteCode = includeHostPrivateData ? room.InviteCode : "",
        room.HostName,
        room.Capacity,
        members = room.Members.Values
            .OrderBy(m => m.IsHost ? 0 : 1)
            .ThenBy(m => m.JoinedAt)
            .Select(m => new { m.MemberId, m.Name, m.ColorIndex, m.IsHost, m.CanEdit, m.CanSave })
            .ToArray(),
        pending = room.Pending.Values
            .Where(_ => includeHostPrivateData)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new
            {
                a.ApplyId,
                a.Name,
                // 申请过期时间（Unix 毫秒）：房主端据此显示 30 秒倒计时，刷新页面不受影响。
                ExpiresAt = new DateTimeOffset(a.ExpiresAt).ToUnixTimeMilliseconds(),
            })
            .ToArray(),
        // 权限申请队列（带过期时间）：房主端据此显示 30 秒倒计时，刷新页面后恢复完整队列。
        pendingPerms = room.PendingPerms.Values
            .Where(_ => includeHostPrivateData)
            .OrderBy(p => p.CreatedAt)
            .Select(p => new
            {
                p.ApplyId,
                p.MemberId,
                p.Name,
                p.ColorIndex,
                p.Perm,
                ExpiresAt = new DateTimeOffset(p.ExpiresAt).ToUnixTimeMilliseconds(),
            })
            .ToArray(),
        // 替换图纸申请队列（带过期时间）：房主端据此显示 1 分钟倒计时，刷新页面后恢复完整队列。
        pendingReplaces = room.PendingReplaces.Values
            .Where(_ => includeHostPrivateData)
            .OrderBy(r => r.CreatedAt)
            .Select(r => new
            {
                r.ApplyId,
                r.MemberId,
                r.Name,
                r.ColorIndex,
                ExpiresAt = new DateTimeOffset(r.ExpiresAt).ToUnixTimeMilliseconds(),
            })
            .ToArray(),
    };

    public static object? BuildSnapshotDto(CollabSnapshot? snapshot)
    {
        if (snapshot is null) return null;
        return new { snapshot.Width, snapshot.Height, snapshot.Cells, snapshot.Colors, snapshot.Title };
    }

    private static CollabSnapshot CloneSnapshot(CollabSnapshot snapshot) => new()
    {
        Width = snapshot.Width,
        Height = snapshot.Height,
        Cells = snapshot.Cells.ToArray(),
        Colors = snapshot.Colors.Select(color => new CollabColor
        {
            Id = color.Id,
            Brand = color.Brand,
            Code = color.Code,
            Name = color.Name,
            Hex = color.Hex,
            Rgb = color.Rgb.ToArray(),
            Lab = color.Lab.ToArray(),
            Source = color.Source,
            License = color.License,
        }).ToArray(),
        Title = snapshot.Title,
    };

    private static string NormalizeOperationId(string? operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId)) return NewToken(12);
        var value = operationId.Trim();
        if (value.Length > 64 || value.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_'))
            return NewToken(12);
        return value;
    }

    private static List<CollabHistoryOperation> GetHistoryStack(
        Dictionary<string, List<CollabHistoryOperation>> histories,
        string memberId)
    {
        if (!histories.TryGetValue(memberId, out var stack))
            histories[memberId] = stack = [];
        return stack;
    }

    private static bool HasCurrentLockedHistory(
        CollabRoom room,
        Dictionary<string, List<CollabHistoryOperation>> histories,
        string memberId,
        DateTime now)
    {
        if (!histories.TryGetValue(memberId, out var stack)) return false;
        return stack.Any(operation =>
            operation.Changes.Values.Any(change => IsCurrentLockedCell(room, memberId, change.Index, now)));
    }

    private static bool IsCurrentLockedCell(CollabRoom room, string memberId, int index, DateTime now)
    {
        if (index < 0 || index >= room.CellLastEditors.Length) return false;
        return room.Locks.TryGetValue(index, out var cellLock)
            && cellLock.ExpiresAt > now
            && string.Equals(cellLock.MemberId, memberId, StringComparison.Ordinal)
            && string.Equals(room.CellLastEditors[index], memberId, StringComparison.Ordinal);
    }

    private static CollabHistoryOperation GetOrCreateHistoryOperation(CollabRoom room, string memberId, string operationId)
    {
        var undo = GetHistoryStack(room.UndoHistory, memberId);
        var current = undo.Count > 0 && undo[^1].OperationId == operationId
            ? undo[^1]
            : null;
        if (current is null)
        {
            current = new CollabHistoryOperation { OperationId = operationId, MemberId = memberId };
            undo.Add(current);
            TrimHistory(undo);
            GetHistoryStack(room.RedoHistory, memberId).Clear();
        }
        return current;
    }

    private static void TrimHistory(List<CollabHistoryOperation> stack)
    {
        // 需求变更：放宽历史容量，配合「撤销本人所有编辑不受时间限制」；200 笔以内均可逐笔撤销/恢复。
        const int maxOperationsPerMember = 200;
        if (stack.Count > maxOperationsPerMember)
            stack.RemoveRange(0, stack.Count - maxOperationsPerMember);
    }

    private static void ResetHistory(CollabRoom room)
    {
        room.BoardRevision++;
        var cellCount = room.Snapshot?.Cells.Length ?? 0;
        room.CellVersions = new long[cellCount];
        room.CellLastEditors = new string?[cellCount];
        room.MutationVersion = 0;
        room.UndoHistory.Clear();
        room.RedoHistory.Clear();
    }

    private static void ValidateSnapshot(CollabSnapshot snapshot)
    {
        if (snapshot.Width is < 8 or > 160 || snapshot.Height is < 8 or > 160)
            throw new InvalidOperationException("豆板尺寸不正确，无法发起联机。");
        if (snapshot.Cells is null || snapshot.Cells.Length != snapshot.Width * snapshot.Height || snapshot.Cells.Length > MaxSnapshotCells)
            throw new InvalidOperationException("豆板数据不完整，无法发起联机。");
        if (snapshot.Colors is null || snapshot.Colors.Length == 0 || snapshot.Colors.Length > MaxSnapshotColors)
            throw new InvalidOperationException("色板数据数量不正确，无法发起联机。");
        if (snapshot.Title?.Length > 120)
            throw new InvalidOperationException("图纸名称过长，无法发起联机。");
        for (var index = 0; index < snapshot.Colors.Length; index++)
        {
            var color = snapshot.Colors[index];
            if (color is null
                || !IsBoundedText(color.Id, 80)
                || !IsBoundedText(color.Brand, 80)
                || !IsBoundedText(color.Code, 40)
                || !IsBoundedText(color.Name, 80)
                || !IsHexColor(color.Hex)
                || color.Source is null || color.Source.Length > 300
                || color.License is null || color.License.Length > 120
                || color.Rgb is null || color.Rgb.Length != 3 || color.Rgb.Any(value => !double.IsFinite(value) || value < 0 || value > 255)
                || color.Lab is null || color.Lab.Length != 3 || color.Lab.Any(value => !double.IsFinite(value) || Math.Abs(value) > 500))
                throw new InvalidOperationException($"色板第 {index + 1} 项格式不正确，无法发起联机。");
        }
        if (snapshot.Cells.Any(colorIndex => colorIndex < -1 || colorIndex >= snapshot.Colors.Length))
            throw new InvalidOperationException("豆板包含无效色号，无法发起联机。");
    }

    private static bool IsBoundedText(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;

    private static bool IsHexColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    // 成员名称 = 颜色 + 豆：索引 0 绿（房主系统色）、1 蓝、2 黄、3 紫、4 青，与前端 COLLAB_COLORS 颜色映射一致。
    private static readonly string[] MemberColorNames = new[] { "绿豆", "蓝豆", "黄豆", "紫豆", "青豆" };
    private static string MemberColorName(int colorIndex) =>
        colorIndex >= 0 && colorIndex < MemberColorNames.Length ? MemberColorNames[colorIndex] : "好友" + colorIndex;

    private static string NewToken(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string NewInviteCode()
    {
        // 6 位邀请码：用不易混淆的字符集（大写字母去 I/O，数字去 0/1），
        // 与前端输入框长度限制（6 位）保持一致，复制/输入/显示三处统一。
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> code = stackalloc char[6];
        for (var i = 0; i < 6; i++)
            code[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(code);
    }
}

/// <summary>一次编辑广播的负载：edits 为已接受变更，reverts 为冲突回滚（权威值），locks 为本次抢占的格子。</summary>
public sealed class CollabEditsEvent
{
    public CollabEditsEvent(long seq, CollabEditItem[] edits, CollabEditItem[] reverts, CollabLockEvent[] locks)
    {
        Type = "edits";
        Seq = seq;
        Edits = edits;
        Reverts = reverts;
        Locks = locks;
    }

    public string Type { get; }
    public long Seq { get; }
    public CollabEditItem[] Edits { get; }
    public CollabEditItem[] Reverts { get; }
    public CollabLockEvent[] Locks { get; }
}

/// <summary>某个成员本次编辑时抢占的格子集合（用于前端以成员颜色描框）。</summary>
public sealed class CollabLockEvent
{
    public CollabLockEvent(string memberId, int colorIndex, int[] cells)
    {
        MemberId = memberId;
        ColorIndex = colorIndex;
        Cells = cells;
    }

    public string MemberId { get; }
    public int ColorIndex { get; }
    public int[] Cells { get; }
}
