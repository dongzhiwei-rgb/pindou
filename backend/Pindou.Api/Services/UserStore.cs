// 文件：UserStore.cs
// 用途：以「密钥」作为账号唯一标识，管理会话令牌与单点登录在线状态。
// 核心职责：维护单点登录的会话令牌，支持新设备登录时旧会话立即失效（SSE 推送）。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-25

using MySqlConnector;

namespace Pindou.Api.Services;

public sealed class UserStore
{
    private readonly string _connectionString;

    public UserStore(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("MySql")
            ?? throw new InvalidOperationException("缺少 MySQL 连接字符串。");
    }

    // 密钥登录时注册/更新账号（user_id = 密钥）；返回是否为该密钥首次登录以及是否存在云端存档。
    public async Task<(bool IsNewUser, bool HasSave)> LoginAsync(string key, string ip, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        bool isNewUser;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText = "SELECT EXISTS(SELECT 1 FROM users WHERE user_id = @key)";
            lookup.Parameters.AddWithValue("@key", key);
            isNewUser = (long)(await lookup.ExecuteScalarAsync(ct) ?? 0L) == 0;
        }

        await using (var upsert = connection.CreateCommand())
        {
            upsert.CommandText = """
                INSERT INTO users (user_id, last_ip, created_at, last_seen_at, last_login_at)
                VALUES (@key, @ip, UTC_TIMESTAMP(), UTC_TIMESTAMP(), UTC_TIMESTAMP())
                ON DUPLICATE KEY UPDATE last_ip = @ip, last_seen_at = UTC_TIMESTAMP(), last_login_at = UTC_TIMESTAMP()
                """;
            upsert.Parameters.AddWithValue("@key", key);
            upsert.Parameters.AddWithValue("@ip", ip);
            await upsert.ExecuteNonQueryAsync(ct);
        }

        await using (var save = connection.CreateCommand())
        {
            save.CommandText = "SELECT EXISTS(SELECT 1 FROM canvas_saves WHERE user_id = @key)";
            save.Parameters.AddWithValue("@key", key);
            var hasSave = (long)(await save.ExecuteScalarAsync(ct) ?? 0L) > 0;
            return (isNewUser, hasSave);
        }
    }

    // 读取密钥当前会话令牌，用于定位需要被踢下线的旧设备连接。
    public async Task<string?> GetSessionTokenAsync(string key, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_token FROM users WHERE user_id = @key LIMIT 1";
        command.Parameters.AddWithValue("@key", key);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    // 清除密钥的会话令牌。
    public async Task ClearSessionAsync(string key, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE users SET session_token = NULL, previous_session_token = NULL, previous_session_expires_at = NULL, kick_notice_at = NULL WHERE user_id = @key";
        command.Parameters.AddWithValue("@key", key);
        await command.ExecuteNonQueryAsync(ct);
    }

    // 登录成功后认领唯一会话：新令牌写入即生效。
    public async Task ClaimSessionAsync(string key, string sessionToken, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE users
            SET session_token = @token,
                previous_session_token = NULL,
                previous_session_expires_at = NULL,
                kick_notice_at = NULL
            WHERE user_id = @key
            """;
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@token", sessionToken);
        await command.ExecuteNonQueryAsync(ct);
    }

    // 只有数据库中的当前会话令牌有效；新设备登录覆盖令牌后，旧会话立即失效。
    public async Task<bool> IsActiveSessionAsync(string key, string? sessionToken, CancellationToken ct = default)
    {
        return await CheckSessionAsync(key, sessionToken, ct);
    }

    // 校验唯一有效会话：仅当传入令牌与库中当前令牌一致时才有效；新设备登录覆盖令牌后旧会话立即失效。
    public async Task<bool> CheckSessionAsync(string key, string? sessionToken, CancellationToken ct = default)
    {
        var incoming = sessionToken ?? "";
        if (incoming.Length == 0) return false;
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_token FROM users WHERE user_id = @key LIMIT 1";
        command.Parameters.AddWithValue("@key", key);
        var current = await command.ExecuteScalarAsync(ct) as string;
        return string.Equals(current, incoming, StringComparison.Ordinal);
    }

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
