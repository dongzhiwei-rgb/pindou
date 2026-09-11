// 文件：CanvasSaveStore.cs
// 用途：按设备 UUID 保存和恢复画布工程数据。
// 核心职责：以整表 upsert 方式持久化工程 JSON，供换设备/换浏览器后按账号恢复。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-25

using MySqlConnector;

namespace Pindou.Api.Services;

public sealed class CanvasSaveStore
{
    private readonly string _connectionString;

    public CanvasSaveStore(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("MySql")
            ?? throw new InvalidOperationException("缺少 MySQL 连接字符串。");
    }

    public async Task<string?> GetAsync(string userId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM canvas_saves WHERE user_id = @userId LIMIT 1";
        command.Parameters.AddWithValue("@userId", userId);
        var result = await command.ExecuteScalarAsync(ct);
        return result as string;
    }

    public async Task SaveAsync(string userId, string payload, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO canvas_saves (user_id, payload, updated_at) VALUES (@userId, @payload, UTC_TIMESTAMP())
            ON DUPLICATE KEY UPDATE payload = @payload, updated_at = UTC_TIMESTAMP()
            """;
        command.Parameters.AddWithValue("@userId", userId);
        command.Parameters.AddWithValue("@payload", payload);
        await command.ExecuteNonQueryAsync(ct);
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
