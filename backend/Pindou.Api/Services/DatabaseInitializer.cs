// 文件：DatabaseInitializer.cs
// 用途：确保 MySQL 中商用授权所需的表结构存在。
// 核心职责：在服务启动时带重试地连接 MySQL 并创建数据表，避免数据库未就绪导致首启失败；同时为「自助购买」补齐套餐与订单表并写入默认套餐。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-23

using MySqlConnector;

namespace Pindou.Api.Services;

public sealed class DatabaseInitializer
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(IConfiguration configuration, ILogger<DatabaseInitializer> logger)
    {
        _connectionString = configuration.GetConnectionString("MySql")
            ?? throw new InvalidOperationException("缺少 MySQL 连接字符串。");
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        const int maxAttempts = 10;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var connection = new MySqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken);

                await using (var licenses = connection.CreateCommand())
                {
                    licenses.CommandText = """
                        CREATE TABLE IF NOT EXISTS licenses (
                            license_key VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
                            user_id VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NULL,
                            total_seconds BIGINT NOT NULL,
                            used_seconds BIGINT NOT NULL DEFAULT 0,
                            last_heartbeat_at DATETIME(3) NULL,
                            remaining_count INT NOT NULL,
                            is_revoked TINYINT(1) NOT NULL DEFAULT 0,
                            created_at DATETIME(3) NOT NULL
                        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
                        """;
                    await licenses.ExecuteNonQueryAsync(cancellationToken);
                }

                // 旧库迁移：密钥区分「吊销（逻辑失效，保留记录）」与「删除（物理移除）」，补充 is_revoked 列。
                await AddColumnIfMissingAsync(connection, cancellationToken,
                    "ALTER TABLE licenses ADD COLUMN is_revoked TINYINT(1) NOT NULL DEFAULT 0 AFTER remaining_count");
                // 绝对时间模型迁移：首次登录设置计时起点（started_at），剩余时长按服务器时间消耗（离线/后台同样消耗）。
                await AddColumnIfMissingAsync(connection, cancellationToken,
                    "ALTER TABLE licenses ADD COLUMN started_at DATETIME(3) NULL AFTER created_at");

                await using (var users = connection.CreateCommand())
                {
                    users.CommandText = """
                        CREATE TABLE IF NOT EXISTS users (
                            user_id VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
                            last_ip VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NULL,
                            session_token VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NULL,
                            previous_session_token VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NULL,
                            previous_session_expires_at DATETIME(3) NULL,
                            kick_notice_at DATETIME(3) NULL,
                            created_at DATETIME(3) NOT NULL,
                            last_seen_at DATETIME(3) NOT NULL,
                            last_login_at DATETIME(3) NULL,
                            last_logout_at DATETIME(3) NULL
                        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
                        """;
                    await users.ExecuteNonQueryAsync(cancellationToken);
                }

                // 旧库迁移：为「同一账号仅一设备在线」补充会话令牌列；列已存在时忽略重复列错误。
                await AddColumnIfMissingAsync(connection, cancellationToken,
                    "ALTER TABLE users ADD COLUMN session_token VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NULL AFTER last_ip");
                // 历史兼容字段继续保留，避免破坏已部署数据库；即时下线模式不再读取这些字段。
                await AddColumnIfMissingAsync(connection, cancellationToken,
                    "ALTER TABLE users ADD COLUMN previous_session_token VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NULL AFTER session_token");
                await AddColumnIfMissingAsync(connection, cancellationToken,
                    "ALTER TABLE users ADD COLUMN previous_session_expires_at DATETIME(3) NULL AFTER previous_session_token");
                await AddColumnIfMissingAsync(connection, cancellationToken,
                    "ALTER TABLE users ADD COLUMN kick_notice_at DATETIME(3) NULL AFTER previous_session_expires_at");
                // 上线/下线时间：用于密钥使用情况展示。
                await AddColumnIfMissingAsync(connection, cancellationToken,
                    "ALTER TABLE users ADD COLUMN last_login_at DATETIME(3) NULL AFTER last_seen_at");
                await AddColumnIfMissingAsync(connection, cancellationToken,
                    "ALTER TABLE users ADD COLUMN last_logout_at DATETIME(3) NULL AFTER last_login_at");

                await using (var saves = connection.CreateCommand())
                {
                    saves.CommandText = """
                        CREATE TABLE IF NOT EXISTS canvas_saves (
                            user_id VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
                            payload MEDIUMTEXT NOT NULL,
                            updated_at DATETIME(3) NOT NULL
                        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
                        """;
                    await saves.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var trials = connection.CreateCommand())
                {
                    trials.CommandText = """
                        CREATE TABLE IF NOT EXISTS ip_trials (
                            ip VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
                            first_seen_at DATETIME(3) NOT NULL,
                            used_seconds INT NOT NULL DEFAULT 0,
                            last_active_at DATETIME(3) NULL,
                            generate_count INT NOT NULL
                        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
                        """;
                    await trials.ExecuteNonQueryAsync(cancellationToken);
                }

                // 旧库迁移：试用时长改为活跃累计，补充 used_seconds / last_active_at 列；列已存在时忽略。
                await AddColumnIfMissingAsync(connection, cancellationToken,
                    "ALTER TABLE ip_trials ADD COLUMN used_seconds INT NOT NULL DEFAULT 0 AFTER first_seen_at");
                await AddColumnIfMissingAsync(connection, cancellationToken,
                    "ALTER TABLE ip_trials ADD COLUMN last_active_at DATETIME(3) NULL AFTER used_seconds");

                // 自助购买：可售套餐表。
                await using (var purchasePackages = connection.CreateCommand())
                {
                    purchasePackages.CommandText = """
                        CREATE TABLE IF NOT EXISTS purchase_packages (
                            id INT AUTO_INCREMENT PRIMARY KEY,
                            name VARCHAR(64) NOT NULL,
                            total_seconds INT NOT NULL,
                            generate_count INT NOT NULL,
                            price DECIMAL(10,2) NOT NULL,
                            description VARCHAR(255) NULL,
                            active TINYINT(1) NOT NULL DEFAULT 1,
                            sort_order INT NOT NULL DEFAULT 0,
                            created_at DATETIME(3) NOT NULL
                        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
                        """;
                    await purchasePackages.ExecuteNonQueryAsync(cancellationToken);
                }

                // 自助购买：订单表（pending→review→delivered/cancelled）。
                await using (var purchaseOrders = connection.CreateCommand())
                {
                    purchaseOrders.CommandText = """
                        CREATE TABLE IF NOT EXISTS purchase_orders (
                            order_no VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
                            package_id INT NOT NULL,
                            amount DECIMAL(10,2) NOT NULL,
                            status VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT 'pending',
                            transaction_no VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NULL,
                            contact VARCHAR(128) NULL,
                            note VARCHAR(255) NULL,
                            buyer_ip VARCHAR(64) NULL,
                            license_key VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NULL,
                            created_at DATETIME(3) NOT NULL,
                            updated_at DATETIME(3) NULL,
                            delivered_at DATETIME(3) NULL,
                            INDEX idx_orders_status_created (status, created_at)
                        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
                        """;
                    await purchaseOrders.ExecuteNonQueryAsync(cancellationToken);
                }

                await SeedPackagesAsync(connection, cancellationToken);

                return;
            }
            catch (Exception exception) when (exception is MySqlException or InvalidOperationException or TimeoutException)
            {
                lastError = exception;
                if (attempt < maxAttempts)
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }

        // 数据表初始化失败时不阻断服务启动：本地或数据库未就绪时仍可运行，授权接口会因连接失败返回错误。
        _logger.LogError(lastError, "无法连接 MySQL 并初始化授权数据表，授权功能暂不可用。");
    }

    // 首次启动时写入默认可售套餐；已有套餐时不做任何改动，便于后续人工调整价格与时长。
    private static async Task SeedPackagesAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM purchase_packages";
            var count = Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken));
            if (count > 0) return;
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO purchase_packages (name, total_seconds, generate_count, price, description, active, sort_order, created_at) VALUES
            ('周卡 · 7天 100次', 604800, 100, 9.90, '7 天累计在线时长，可生成 100 次图纸', 1, 10, UTC_TIMESTAMP()),
            ('月卡 · 30天 500次', 2592000, 500, 29.90, '30 天累计在线时长，可生成 500 次图纸', 1, 20, UTC_TIMESTAMP()),
            ('季卡 · 90天 2000次', 7776000, 2000, 79.90, '90 天累计在线时长，可生成 2000 次图纸', 1, 30, UTC_TIMESTAMP())
            """;
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AddColumnIfMissingAsync(MySqlConnection connection, CancellationToken cancellationToken, string alterStatement)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = alterStatement;
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number == 1060)
        {
            // 列已存在，忽略。
        }
    }
}
