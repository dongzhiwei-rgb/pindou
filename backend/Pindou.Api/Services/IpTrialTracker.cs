// 文件：IpTrialTracker.cs
// 用途：记录无密钥访客的试用情况，试用时长按「活跃累计」计算：仅在页面前台在线时累加，关闭/后台/锁屏/断网自动暂停。
// 核心职责：按客户端 IP 累计活跃试用秒数与生成次数，超出试用窗口或次数上限时拒绝生成。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-25

using MySqlConnector;
using Pindou.Api.Models;

namespace Pindou.Api.Services;

public sealed class IpTrialTracker
{

    private readonly string _connectionString;
    private readonly TimeSpan _trialWindow;
    private readonly int _trialGenerations;

    public IpTrialTracker(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("MySql")
            ?? throw new InvalidOperationException("缺少 MySQL 连接字符串。");
        _trialWindow = TimeSpan.FromSeconds(configuration.GetValue("License:TrialSeconds", 600));
        _trialGenerations = configuration.GetValue("License:TrialGenerations", 1);
    }

    // 原子校验并占用一次试用生成：时间未到且次数未超时返回 true 并累加次数，否则返回拒绝原因。
    // 单条条件 UPDATE 完成「校验 + 扣减」，避免原先「先检查后记录」两步在并发下超发免费次数。
    public async Task<(bool Allowed, string? Reason)> TryConsumeGenerationAsync(string ip, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        // 确保记录存在（首次试用），不覆盖已存在的 first_seen_at（计时起点保持首次访问）。
        await using (var ensure = connection.CreateCommand())
        {
            ensure.CommandText = """
                INSERT INTO ip_trials (ip, first_seen_at, used_seconds, last_active_at, generate_count)
                VALUES (@ip, UTC_TIMESTAMP(), 0, UTC_TIMESTAMP(), 0)
                ON DUPLICATE KEY UPDATE last_active_at = UTC_TIMESTAMP()
                """;
            ensure.Parameters.AddWithValue("@ip", ip);
            await ensure.ExecuteNonQueryAsync(ct);
        }

        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE ip_trials
            SET generate_count = generate_count + 1
            WHERE ip = @ip
              AND generate_count < @limit
              AND UTC_TIMESTAMP() < DATE_ADD(first_seen_at, INTERVAL @window SECOND)
            """;
        update.Parameters.AddWithValue("@ip", ip);
        update.Parameters.AddWithValue("@limit", _trialGenerations);
        update.Parameters.AddWithValue("@window", (int)_trialWindow.TotalSeconds);
        if (await update.ExecuteNonQueryAsync(ct) > 0)
            return (true, null);

        // 定位拒绝原因（时间耗尽或次数用完）。
        var status = await ReadStatusAsync(connection, ip, ct);
        return (false, status.RemainingSeconds <= 0
            ? "试用时间已到，请获取密钥后继续使用。"
            : "免费生成次数已用完，请获取密钥后继续使用。");
    }

    // 页面加载时查询当前 IP 的试用状态；首次访问写入首访记录并初始化活跃起点。
    public async Task<TrialStatus> GetTrialStatusAsync(string ip, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO ip_trials (ip, first_seen_at, used_seconds, last_active_at, generate_count)
                VALUES (@ip, UTC_TIMESTAMP(), 0, UTC_TIMESTAMP(), 0)
                ON DUPLICATE KEY UPDATE last_active_at = IF(last_active_at IS NULL, UTC_TIMESTAMP(), last_active_at)
                """;
            insert.Parameters.AddWithValue("@ip", ip);
            await insert.ExecuteNonQueryAsync(ct);
        }

        return await ReadStatusAsync(connection, ip, ct);
    }

    // 试用心跳：仅在前台在线时由前端调用，把距上次活跃的时间差累计到试用时长（关闭/后台/锁屏/断网无心跳则不累计）。
    public async Task<TrialStatus> HeartbeatAsync(string ip, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var update = connection.CreateCommand();
        // 绝对时间模型：试用心跳不再累计时长（剩余按服务器时间消耗），仅刷新活跃时间用于在线状态判定。
        update.CommandText = """
            UPDATE ip_trials SET last_active_at = UTC_TIMESTAMP() WHERE ip = @ip
            """;
        update.Parameters.AddWithValue("@ip", ip);
        await update.ExecuteNonQueryAsync(ct);
        return await ReadStatusAsync(connection, ip, ct);
    }

    private async Task<TrialStatus> ReadStatusAsync(MySqlConnection connection, string ip, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT first_seen_at, generate_count FROM ip_trials WHERE ip = @ip LIMIT 1";
        command.Parameters.AddWithValue("@ip", ip);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new TrialStatus(0, 0, (int)TimeSpan.Zero.TotalMinutes, true);

        var firstSeen = ToOffset(reader.GetDateTime("first_seen_at"));
        var count = reader.GetInt32("generate_count");
        var windowSeconds = (int)_trialWindow.TotalSeconds;
        // 绝对时间模型：试用剩余按服务器时间自首次访问起消耗（离线/后台同样消耗）。
        var elapsed = (int)Math.Floor((DateTimeOffset.UtcNow - firstSeen).TotalSeconds);
        var remainingSeconds = Math.Max(0, windowSeconds - elapsed);
        var remainingGenerations = Math.Max(0, _trialGenerations - count);
        return new TrialStatus(remainingSeconds, remainingGenerations, windowSeconds / 60, remainingSeconds <= 0);
    }

    // MySQL 存 UTC_TIMESTAMP，读出的 DateTime 按 UTC 处理。
    private static DateTimeOffset ToOffset(DateTime value) => new(value, TimeSpan.Zero);

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
