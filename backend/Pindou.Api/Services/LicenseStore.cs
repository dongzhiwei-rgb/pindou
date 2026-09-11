// 文件：LicenseStore.cs
// 用途：管理商用授权的密钥库；密钥即账号，支持生成、批量生成、校验、扣减次数、分页查询、批量吊销与批量删除。
// 核心职责：以密钥作为唯一标识实现单点登录（同一密钥仅一台设备在线，新登录即时踢旧会话）。
// 吊销（逻辑失效，保留记录）与删除（物理移除）分离：吊销后密钥仍存在，但任何校验都会返回 revoked 状态。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-25

using System.Security.Cryptography;
using MySqlConnector;
using Pindou.Api.Models;

namespace Pindou.Api.Services;

public sealed class LicenseStore
{
    // 去掉 0、1、I、L、O 等易混淆字符，方便用户手动输入。
    private const string KeyAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    // 在线判定窗口（秒）：心跳每 5 秒一次；关闭浏览器后心跳停止，后台最多 20 秒判定为离线；后台运行/切换标签持续心跳视为在线。
    private const int OnlineWindowSeconds = 20;

    private readonly string _connectionString;
    private readonly UserStore _userStore;
    private readonly SessionEventHub _events;
    private readonly KeyStateEventHub _keyEvents;

    public LicenseStore(IConfiguration configuration, UserStore userStore, SessionEventHub events, KeyStateEventHub keyEvents)
    {
        _connectionString = configuration.GetConnectionString("MySql")
            ?? throw new InvalidOperationException("缺少 MySQL 连接字符串。");
        _userStore = userStore;
        _events = events;
        _keyEvents = keyEvents;
    }

    // 只读校验，不扣减次数；密钥即账号，无需额外设备标识；被吊销的密钥返回 revoked 状态。
    public async Task<LicenseStatus> InspectAsync(string? key, CancellationToken ct = default)
    {
        key = Normalize(key);
        if (key.Length == 0) return new LicenseStatus("invalid", null, null, "请输入密钥。");

        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT total_seconds, remaining_count, is_revoked, started_at FROM licenses WHERE license_key = @key LIMIT 1";
        command.Parameters.AddWithValue("@key", key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new LicenseStatus("invalid", null, null, "密钥无效，请核对后重试。");

        var isRevoked = reader.GetBoolean("is_revoked");
        if (isRevoked)
            return new LicenseStatus("revoked", null, null, "密钥已失效，请获取新密钥。");

        var totalSeconds = reader.GetInt64(0);
        var remainingCount = reader.GetInt32(1);
        var startedAt = reader.IsDBNull(reader.GetOrdinal("started_at"))
            ? (DateTimeOffset?)null
            : ToOffset(reader.GetDateTime("started_at"));
        // 绝对时间模型：剩余时长按服务器时间从 started_at 起消耗（离线/后台同样消耗）。
        var remainingSeconds = ComputeRemaining(totalSeconds, startedAt, DateTimeOffset.UtcNow);
        return Evaluate(totalSeconds, remainingSeconds, remainingCount);
    }

    // 登录：密钥即账号，校验密钥 -> 踢旧会话 -> 认领新会话 -> 返回云端存档状态。
    public async Task<LoginResult> LoginAsync(string? key, string ip, string? sessionToken, CancellationToken ct = default)
    {
        key = Normalize(key);
        if (key.Length == 0) return new LoginResult("invalid", null, null, "请输入密钥。", false, false, null);

        var keyStatus = await InspectAsync(key, ct);
        if (keyStatus.Status != "active" && keyStatus.Status != "time_expired")
            return new LoginResult(keyStatus.Status, keyStatus.RemainingSeconds, keyStatus.RemainingCount, keyStatus.Message, false, false, null);

        // 踢掉该密钥旧会话：向旧会话连接即时推送「被踢下线」事件（无需等浏览器刷新或心跳）。
        var oldToken = await _userStore.GetSessionTokenAsync(key, ct);
        if (!string.IsNullOrEmpty(oldToken))
            await _events.NotifySessionKickedAsync(oldToken, ct);

        // 注册/更新账号（user_id = 密钥）并写入唯一有效令牌。
        var (isNewUser, hasSave) = await _userStore.LoginAsync(key, ip, ct);
        var token = Guid.NewGuid().ToString("N");
        await _userStore.ClaimSessionAsync(key, token, ct);
        // 绝对时间模型：首次登录设置计时起点（started_at），之后剩余时长按服务器时间消耗。
        await using (var startConn = await OpenAsync(ct))
        await using (var startCmd = startConn.CreateCommand())
        {
            startCmd.CommandText = "UPDATE licenses SET started_at = COALESCE(started_at, UTC_TIMESTAMP()) WHERE license_key = @key";
            startCmd.Parameters.AddWithValue("@key", key);
            await startCmd.ExecuteNonQueryAsync(ct);
        }
        // 登录成功（上线）：推送密钥状态变化，后台管理页即时刷新。
        await _keyEvents.BroadcastAsync(ct);
        return new LoginResult(keyStatus.Status, keyStatus.RemainingSeconds, keyStatus.RemainingCount, null, isNewUser, hasSave, token);
    }

    // 会话开始：将计时起点重置为当前时刻，使关闭浏览器期间的时长不被累加；同时校验单点会话。
    public async Task<LicenseStatus> BeginSessionAsync(string? key, string? sessionToken, CancellationToken ct = default)
    {
        key = Normalize(key);
        if (key.Length == 0) return new LicenseStatus("invalid", null, null, "请输入密钥。");
        var sessionValid = await _userStore.CheckSessionAsync(key, sessionToken, ct);
        if (!sessionValid)
            return new LicenseStatus("device_conflict", null, null, "该账号已在其他设备登录，本设备已下线。");

        await using var connection = await OpenAsync(ct);
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE licenses SET last_heartbeat_at = UTC_TIMESTAMP() WHERE license_key = @key";
        update.Parameters.AddWithValue("@key", key);
        await update.ExecuteNonQueryAsync(ct);
        return await InspectAsync(key, ct);
    }

    // 心跳：按距上次心跳的时间差累加在线时长并刷新计时起点。
    // 单次多表 UPDATE 一次完成「会话校验（WHERE u.session_token）+ 时长累计 + 活跃时间刷新」，
    // 替代原先三次数据库访问；心跳为最高频接口（每 5 秒），多用户时收益明显，且不改变前台可见语义。
    public async Task<LicenseStatus> HeartbeatAsync(string? key, string? sessionToken, CancellationToken ct = default)
    {
        key = Normalize(key);
        if (key.Length == 0) return new LicenseStatus("invalid", null, null, "请输入密钥。");

        await using var connection = await OpenAsync(ct);
        await using var update = connection.CreateCommand();
        // 绝对时间模型：心跳不再累计在线时长（剩余时长按服务器时间消耗），仅刷新心跳/活跃时间用于在线状态判定；
        // 首次心跳补设计时起点（首次使用即开始计时），兼容重启前已存在的会话。
        update.CommandText = """
            UPDATE licenses l
            JOIN users u ON u.user_id = l.license_key
            SET l.last_heartbeat_at = UTC_TIMESTAMP(),
                l.started_at = COALESCE(l.started_at, UTC_TIMESTAMP()),
                u.last_seen_at = UTC_TIMESTAMP()
            WHERE l.license_key = @key AND u.session_token = @token
            """;
        update.Parameters.AddWithValue("@key", key);
        update.Parameters.AddWithValue("@token", sessionToken ?? "");
        // 无行更新说明会话令牌不匹配（已在其他设备登录被踢），保持既有 device_conflict 语义。
        if (await update.ExecuteNonQueryAsync(ct) == 0)
            return new LicenseStatus("device_conflict", null, null, "该账号已在其他设备登录，本设备已下线。");
        return await InspectAsync(key, ct);
    }

    // 生成图纸时调用：原子地校验并扣减一次，避免并发请求重复扣减；同时校验单点会话与被吊销状态。
    public async Task<LicenseStatus> ConsumeAsync(string? key, string? sessionToken, CancellationToken ct = default)
    {
        key = Normalize(key);
        if (key.Length == 0) return new LicenseStatus("invalid", null, null, "请输入密钥。");
        if (!await _userStore.IsActiveSessionAsync(key, sessionToken, ct))
            return new LicenseStatus("device_conflict", null, null, "该账号已在其他设备登录，本设备已下线。");

        await using var connection = await OpenAsync(ct);
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE licenses SET remaining_count = remaining_count - 1
            WHERE license_key = @key AND remaining_count > 0 AND is_revoked = 0
            """;
        update.Parameters.AddWithValue("@key", key);
        if (await update.ExecuteNonQueryAsync(ct) > 0)
            return await InspectAsync(key, ct);

        // 未扣减成功，说明密钥无效、次数用尽或被吊销，复用 Inspect 定位原因（吊销会返回 revoked）。
        return await InspectAsync(key, ct);
    }

    public async Task<string> CreateAsync(int totalSeconds, int count, CancellationToken ct = default)
    {
        var entry = new LicenseEntry
        {
            Key = CreateKey(),
            TotalSeconds = totalSeconds,
            RemainingCount = count,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO licenses (license_key, user_id, total_seconds, used_seconds, last_heartbeat_at, remaining_count, is_revoked, created_at)
            VALUES (@key, NULL, @totalSeconds, 0, NULL, @count, 0, @createdAt)
            """;
        command.Parameters.AddWithValue("@key", entry.Key);
        command.Parameters.AddWithValue("@totalSeconds", entry.TotalSeconds);
        command.Parameters.AddWithValue("@count", entry.RemainingCount);
        command.Parameters.AddWithValue("@createdAt", entry.CreatedAt.UtcDateTime);
        await command.ExecuteNonQueryAsync(ct);

        return entry.Key;
    }

    // 批量生成密钥：单次请求、单条多值 INSERT 一次写入多把密钥，返回全部新密钥；避免客户端多次往返。
    public async Task<IReadOnlyList<string>> CreateManyAsync(int totalSeconds, int count, int number, CancellationToken ct = default)
    {
        if (number <= 0) return Array.Empty<string>();

        var createdAt = DateTimeOffset.UtcNow;
        var keys = new string[number];
        for (var i = 0; i < number; i++)
            keys[i] = CreateKey();

        await using var connection = await OpenAsync(ct);
        var values = string.Join(",", keys.Select(_ => "(?, NULL, ?, 0, NULL, ?, 0, ?)"));
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO licenses (license_key, user_id, total_seconds, used_seconds, last_heartbeat_at, remaining_count, is_revoked, created_at)
            VALUES {values}
            """;
        var index = 0;
        foreach (var key in keys)
        {
            command.Parameters.AddWithValue($"@k{index}", key);
            command.Parameters.AddWithValue($"@s{index}", totalSeconds);
            command.Parameters.AddWithValue($"@c{index}", count);
            command.Parameters.AddWithValue($"@t{index}", createdAt.UtcDateTime);
            index++;
        }
        await command.ExecuteNonQueryAsync(ct);
        return keys;
    }

    // 分页查询密钥列表，返回当前页条目与总条数；支持按状态筛选（unused/online/offline/revoked）。
    public async Task<(IReadOnlyList<LicenseEntry> Items, int Total)> ListPagedAsync(int page, int pageSize, string? status = null, CancellationToken ct = default)
    {
        var offset = (page - 1) * pageSize;
        var result = new List<LicenseEntry>();
        await using var connection = await OpenAsync(ct);

        // 在线判断基于「登录/最后活跃」与「授权心跳」中较新者在 120 秒内。
        var where = "";
        if (!string.IsNullOrWhiteSpace(status))
        {
            where = status.ToLowerInvariant() switch
            {
                "revoked" => "WHERE l.is_revoked = 1",
                "online" => "WHERE l.is_revoked = 0 AND u.user_id IS NOT NULL AND l.last_heartbeat_at > UTC_TIMESTAMP() - INTERVAL 20 SECOND",
                "offline" => "WHERE l.is_revoked = 0 AND u.user_id IS NOT NULL AND (l.last_heartbeat_at IS NULL OR l.last_heartbeat_at <= UTC_TIMESTAMP() - INTERVAL 20 SECOND)",
                "unused" => "WHERE l.is_revoked = 0 AND u.user_id IS NULL",
                _ => ""
            };
        }

        int total;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = $"SELECT COUNT(*) FROM licenses l LEFT JOIN users u ON u.user_id = l.license_key {where}";
            total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(ct));
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT l.license_key, l.user_id, l.total_seconds, l.used_seconds, l.last_heartbeat_at,
                   l.remaining_count, l.is_revoked, l.created_at, l.started_at, u.last_seen_at
            FROM licenses l
            LEFT JOIN users u ON u.user_id = l.license_key
            {where}
            ORDER BY l.created_at DESC LIMIT @pageSize OFFSET @offset
            """;
        command.Parameters.AddWithValue("@pageSize", pageSize);
        command.Parameters.AddWithValue("@offset", offset);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var isRevoked = reader.GetBoolean("is_revoked");
            var lastSeen = reader.IsDBNull(reader.GetOrdinal("last_seen_at"))
                ? (DateTimeOffset?)null
                : ToOffset(reader.GetDateTime("last_seen_at"));
            var lastHeartbeat = reader.IsDBNull(reader.GetOrdinal("last_heartbeat_at"))
                ? (DateTimeOffset?)null
                : ToOffset(reader.GetDateTime("last_heartbeat_at"));
            result.Add(new LicenseEntry
            {
                Key = reader.GetString("license_key"),
                UserId = reader.IsDBNull(reader.GetOrdinal("user_id")) ? null : reader.GetString("user_id"),
                TotalSeconds = reader.GetInt64("total_seconds"),
                UsedSeconds = reader.GetInt64("used_seconds"),
                RemainingCount = reader.GetInt32("remaining_count"),
                RemainingSeconds = ComputeRemaining(
                    reader.GetInt64("total_seconds"),
                    reader.IsDBNull(reader.GetOrdinal("started_at")) ? (DateTimeOffset?)null : ToOffset(reader.GetDateTime("started_at")),
                    DateTimeOffset.UtcNow),
                IsRevoked = isRevoked,
                Status = ComputeListStatus(isRevoked, lastSeen, lastHeartbeat),
                CreatedAt = ToOffset(reader.GetDateTime("created_at")),
            });
        }

        return (result, total);
    }

    // 列表状态：已停用 > 未使用（从未登录/心跳）> 在线中（近 120 秒有心跳）> 已离线。
    // 在线状态只依赖心跳时间：登出即清空心跳，密钥立即离线；最近活跃时间仍以 last_seen_at 为准。
    private static string ComputeListStatus(bool isRevoked, DateTimeOffset? lastSeen, DateTimeOffset? lastHeartbeat)
    {
        if (isRevoked) return "revoked";
        if (!lastSeen.HasValue && !lastHeartbeat.HasValue) return "unused";
        if (lastHeartbeat.HasValue && (DateTimeOffset.UtcNow - lastHeartbeat.Value).TotalSeconds <= OnlineWindowSeconds) return "online";
        return "offline";
    }

    // 批量吊销密钥：逻辑失效（is_revoked=1），记录保留，之后任何校验都返回 revoked。
    public async Task<int> RevokeManyAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        if (keys is null || keys.Count == 0) return 0;
        var normalized = keys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(Normalize)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalized.Count == 0) return 0;

        await using var connection = await OpenAsync(ct);
        var placeholders = string.Join(",", normalized.Select((_, i) => $"@k{i}"));
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE licenses SET is_revoked = 1 WHERE license_key IN ({placeholders})";
        for (var i = 0; i < normalized.Count; i++)
            command.Parameters.AddWithValue($"@k{i}", normalized[i]);
        var affected = await command.ExecuteNonQueryAsync(ct);
        if (affected > 0) await _keyEvents.BroadcastAsync(ct);
        return affected;
    }

    // 批量删除密钥：物理移除记录，单条 IN 子句一次删除多把。
    public async Task<int> DeleteManyAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        if (keys is null || keys.Count == 0) return 0;
        var normalized = keys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(Normalize)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalized.Count == 0) return 0;

        await using var connection = await OpenAsync(ct);
        var placeholders = string.Join(",", normalized.Select((_, i) => $"@k{i}"));
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM licenses WHERE license_key IN ({placeholders})";
        for (var i = 0; i < normalized.Count; i++)
            command.Parameters.AddWithValue($"@k{i}", normalized[i]);
        var affected = await command.ExecuteNonQueryAsync(ct);
        if (affected > 0) await _keyEvents.BroadcastAsync(ct);
        return affected;
    }

    // 密钥使用详情：关联用户表返回上次登录 IP、最后活跃时间与在线状态（近 2 分钟活跃视为在线）。
    public async Task<LicenseDetail?> GetDetailAsync(string? key, CancellationToken ct = default)
    {
        key = Normalize(key);
        if (key.Length == 0) return null;

        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT l.license_key, l.user_id, l.total_seconds, l.used_seconds, l.last_heartbeat_at,
                   l.remaining_count, l.is_revoked, l.created_at, l.started_at,
                   u.last_ip, u.last_seen_at, u.session_token
            FROM licenses l
            LEFT JOIN users u ON u.user_id = l.license_key
            WHERE l.license_key = @key
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@key", key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var lastSeen = reader.IsDBNull(reader.GetOrdinal("last_seen_at"))
            ? (DateTimeOffset?)null
            : ToOffset(reader.GetDateTime("last_seen_at"));
        var lastHeartbeat = reader.IsDBNull(reader.GetOrdinal("last_heartbeat_at"))
            ? (DateTimeOffset?)null
            : ToOffset(reader.GetDateTime("last_heartbeat_at"));
        // 最近活跃取「最近活跃」与「授权心跳」中较新者；心跳每 5 秒刷新 last_seen_at，时间准确。
        var lastActive = lastSeen.HasValue && lastHeartbeat.HasValue
            ? (lastSeen > lastHeartbeat ? lastSeen : lastHeartbeat)
            : (lastSeen ?? lastHeartbeat);
        // 在线状态只依赖心跳：登出即清空心跳，密钥立即离线。
        var online = lastHeartbeat.HasValue && (DateTimeOffset.UtcNow - lastHeartbeat.Value).TotalSeconds <= OnlineWindowSeconds;

        return new LicenseDetail(
            Key: reader.GetString("license_key"),
            UserId: reader.IsDBNull(reader.GetOrdinal("user_id")) ? null : reader.GetString("user_id"),
            TotalSeconds: reader.GetInt64("total_seconds"),
            UsedSeconds: reader.GetInt64("used_seconds"),
            RemainingSeconds: ComputeRemaining(
                reader.GetInt64("total_seconds"),
                reader.IsDBNull(reader.GetOrdinal("started_at")) ? (DateTimeOffset?)null : ToOffset(reader.GetDateTime("started_at")),
                DateTimeOffset.UtcNow),
            LastHeartbeatAt: reader.IsDBNull(reader.GetOrdinal("last_heartbeat_at"))
                ? (DateTimeOffset?)null
                : ToOffset(reader.GetDateTime("last_heartbeat_at")),
            RemainingCount: reader.GetInt32("remaining_count"),
            IsRevoked: reader.GetBoolean("is_revoked"),
            CreatedAt: ToOffset(reader.GetDateTime("created_at")),
            LastIp: reader.IsDBNull(reader.GetOrdinal("last_ip")) ? null : reader.GetString("last_ip"),
            LastSeenAt: lastActive,
            Online: online);
    }

    // 登出（退出登录）：清除会话令牌并把最后活跃时间推到过去，使密钥立即变为离线状态。
    // 登出：必须同时匹配密钥与当前会话令牌，防止知道密钥的人强制他人下线。
    // 返回是否成功清除会话；会话不匹配返回 false（调用方返回 409 DEVICE_CONFLICT）。
    public async Task<bool> LogoutAsync(string? key, string? sessionToken, CancellationToken ct = default)
    {
        key = Normalize(key);
        if (key.Length == 0) return false;

        await using var connection = await OpenAsync(ct);
        await using (var userCommand = connection.CreateCommand())
        {
            userCommand.CommandText = """
                UPDATE users SET session_token = NULL,
                    last_seen_at = UTC_TIMESTAMP()
                WHERE user_id = @key AND session_token = @session
                """;
            userCommand.Parameters.AddWithValue("@key", key);
            userCommand.Parameters.AddWithValue("@session", sessionToken ?? "");
            // 没有匹配到当前会话，拒绝登出，避免影响其他会话。
            if (await userCommand.ExecuteNonQueryAsync(ct) == 0) return false;
        }
        await using (var licenseCommand = connection.CreateCommand())
        {
            licenseCommand.CommandText = "UPDATE licenses SET last_heartbeat_at = NULL WHERE license_key = @key";
            licenseCommand.Parameters.AddWithValue("@key", key);
            await licenseCommand.ExecuteNonQueryAsync(ct);
        }
        // 登出（下线）：推送密钥状态变化。
        await _keyEvents.BroadcastAsync(ct);
        return true;
    }

    // 删除单个密钥（物理移除）。
    public async Task<bool> DeleteAsync(string? key, CancellationToken ct = default)
    {
        key = Normalize(key);
        if (key.Length == 0) return false;

        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM licenses WHERE license_key = @key";
        command.Parameters.AddWithValue("@key", key);
        var affected = await command.ExecuteNonQueryAsync(ct);
        if (affected > 0) await _keyEvents.BroadcastAsync(ct);
        return affected > 0;
    }

    // 绝对时间模型：剩余秒数 = 总时长 - 已流逝时间（自首次登录 started_at 起，按服务器时间），离线/后台同样消耗。
    private static long ComputeRemaining(long totalSeconds, DateTimeOffset? startedAt, DateTimeOffset now)
    {
        if (totalSeconds <= 0) return 0; // 无期限仅次数密钥：时间视为 0（等同到期，保留生成次数）。
        if (startedAt is null) return totalSeconds; // 尚未使用，剩余即总额。
        return Math.Max(0, totalSeconds - (long)Math.Floor((now - startedAt.Value).TotalSeconds));
    }

    private static LicenseStatus Evaluate(long totalSeconds, long remainingSeconds, int remainingCount)
    {
        // 次数用完完全失效；期限到但次数仍有剩余时，仍可生成/查看/导出，仅锁定画布编辑。
        if (remainingCount <= 0)
            return new LicenseStatus("exhausted", (int)Math.Max(0, remainingSeconds), 0, "生成次数已用完，请联系获取新密钥。");
        if (remainingSeconds <= 0)
            return new LicenseStatus("time_expired", 0, remainingCount, "使用期限已到，画布编辑已锁定；仍可生成图纸、查看熨烫效果与导出。");
        return new LicenseStatus("active", (int)remainingSeconds, remainingCount, null);
    }

    private static string Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";
        return new string(key.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    }

    private static string CreateKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(15);
        var body = string.Concat(bytes.Select(b => KeyAlphabet[b % KeyAlphabet.Length]));
        return "PD" + body;
    }

    private static DateTimeOffset ToOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
