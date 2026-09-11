// 文件：PurchaseStore.cs
// 用途：管理「自助购买密钥」的套餐与订单，支持创建订单、提交支付单号、核验发货并生成密钥。
// 核心职责：以订单号为主键跟踪购买流程（pending→review→delivered/cancelled），发货时调用 LicenseStore 生成密钥。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-25

using System.Security.Cryptography;
using MySqlConnector;
using Pindou.Api.Models;

namespace Pindou.Api.Services;

public sealed class PurchaseStore
{
    // 去掉 0、1、I、L、O 等易混淆字符，生成人类可读的订单号后缀。
    private const string OrderAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly string _connectionString;
    private readonly IConfiguration _configuration;
    private readonly LicenseStore _licenses;

    public PurchaseStore(IConfiguration configuration, LicenseStore licenses)
    {
        _connectionString = configuration.GetConnectionString("MySql")
            ?? throw new InvalidOperationException("缺少 MySQL 连接字符串。");
        _configuration = configuration;
        _licenses = licenses;
    }

    // 读取收款配置（二维码、收款方信息、付款须知）。
    public PurchasePaymentConfig GetPaymentConfig()
    {
        var section = _configuration.GetSection("Payment");
        return new PurchasePaymentConfig(
            section["QrImageUrl"] ?? "",
            section["PayeeName"] ?? "开发者收款",
            section["PayeeAccount"] ?? "",
            section["Note"] ?? "付款后请填写支付单号并提交，等待人工核验发货。");
    }

    public async Task<IReadOnlyList<PurchasePackage>> ListPackagesAsync(CancellationToken ct = default)
    {
        var result = new List<PurchasePackage>();
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, total_seconds, generate_count, price, description, active, sort_order
            FROM purchase_packages
            WHERE active = 1
            ORDER BY sort_order ASC, price ASC
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new PurchasePackage
            {
                Id = reader.GetInt32("id"),
                Name = reader.GetString("name"),
                TotalSeconds = reader.GetInt32("total_seconds"),
                Count = reader.GetInt32("generate_count"),
                Price = reader.GetDecimal("price"),
                Description = reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString("description"),
                Active = reader.GetBoolean("active"),
                SortOrder = reader.GetInt32("sort_order"),
            });
        }
        return result;
    }

    // 创建订单：校验套餐有效后生成唯一订单号；创建后状态为 pending（待支付并提交单号）。
    public async Task<(PurchaseOrderResult? Order, string? Error)> CreateOrderAsync(
        int packageId, string? contact, string? note, string? ip, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        PurchasePackage package;
        await using (var packageCmd = connection.CreateCommand())
        {
            packageCmd.CommandText = """
                SELECT id, name, total_seconds, generate_count, price
                FROM purchase_packages WHERE id = @id AND active = 1 LIMIT 1
                """;
            packageCmd.Parameters.AddWithValue("@id", packageId);
            await using var reader = await packageCmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return (null, "所选套餐不存在或已下架。");
            package = new PurchasePackage
            {
                Id = reader.GetInt32("id"),
                Name = reader.GetString("name"),
                TotalSeconds = reader.GetInt32("total_seconds"),
                Count = reader.GetInt32("generate_count"),
                Price = reader.GetDecimal("price"),
            };
        }

        var orderNo = await CreateUniqueOrderNoAsync(connection, ct);
        var createdAt = DateTimeOffset.UtcNow;
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO purchase_orders (order_no, package_id, amount, status, contact, note, buyer_ip, created_at, updated_at)
                VALUES (@orderNo, @packageId, @amount, 'pending', @contact, @note, @ip, @createdAt, @createdAt)
                """;
            insert.Parameters.AddWithValue("@orderNo", orderNo);
            insert.Parameters.AddWithValue("@packageId", package.Id);
            insert.Parameters.AddWithValue("@amount", package.Price);
            insert.Parameters.AddWithValue("@contact", (object?)contact ?? DBNull.Value);
            insert.Parameters.AddWithValue("@note", (object?)note ?? DBNull.Value);
            insert.Parameters.AddWithValue("@ip", (object?)ip ?? DBNull.Value);
            insert.Parameters.AddWithValue("@createdAt", createdAt.UtcDateTime);
            await insert.ExecuteNonQueryAsync(ct);
        }

        return (new PurchaseOrderResult(orderNo, package.Id, package.Name, package.TotalSeconds, package.Count,
            package.Price, "pending", null, contact, null, createdAt), null);
    }

    // 提交支付单号：仅在 pending/review 状态允许写入，提交后进入 review（待人工核验）。
    public async Task<(PurchaseOrderResult? Order, string? Error)> SubmitPaymentAsync(
        string orderNo, string transactionNo, CancellationToken ct = default)
    {
        orderNo = Normalize(orderNo);
        transactionNo = (transactionNo ?? "").Trim();
        if (orderNo.Length == 0 || transactionNo.Length == 0)
            return (null, "订单号与支付单号不能为空。");

        await using var connection = await OpenAsync(ct);
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE purchase_orders
            SET transaction_no = @txn, status = 'review', updated_at = UTC_TIMESTAMP()
            WHERE order_no = @orderNo AND status IN ('pending', 'review')
            """;
        update.Parameters.AddWithValue("@txn", transactionNo);
        update.Parameters.AddWithValue("@orderNo", orderNo);
        if (await update.ExecuteNonQueryAsync(ct) == 0)
            return (null, "订单状态不允许提交单号。");

        return (await GetOrderAsync(orderNo, ct), null);
    }

    // 查询订单（含套餐名称、时长、次数、价格、状态与已发货密钥）。
    public async Task<PurchaseOrderResult?> GetOrderAsync(string orderNo, CancellationToken ct = default)
    {
        orderNo = Normalize(orderNo);
        if (orderNo.Length == 0) return null;

        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.order_no, o.package_id, p.name AS package_name, p.total_seconds, p.generate_count,
                   o.amount, o.status, o.transaction_no, o.contact, o.license_key, o.created_at
            FROM purchase_orders o
            JOIN purchase_packages p ON p.id = o.package_id
            WHERE o.order_no = @orderNo LIMIT 1
            """;
        command.Parameters.AddWithValue("@orderNo", orderNo);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new PurchaseOrderResult(
            reader.GetString("order_no"),
            reader.GetInt32("package_id"),
            reader.GetString("package_name"),
            reader.GetInt32("total_seconds"),
            reader.GetInt32("generate_count"),
            reader.GetDecimal("amount"),
            reader.GetString("status"),
            reader.IsDBNull(reader.GetOrdinal("transaction_no")) ? null : reader.GetString("transaction_no"),
            reader.IsDBNull(reader.GetOrdinal("contact")) ? null : reader.GetString("contact"),
            reader.IsDBNull(reader.GetOrdinal("license_key")) ? null : reader.GetString("license_key"),
            ToOffset(reader.GetDateTime("created_at")));
    }

    // 管理端：核验通过后发货，生成密钥并绑定到订单。
    public async Task<(PurchaseOrderResult? Order, string? Error)> DeliverAsync(string orderNo, CancellationToken ct = default)
    {
        orderNo = Normalize(orderNo);
        if (orderNo.Length == 0) return (null, "订单号不能为空。");

        await using var connection = await OpenAsync(ct);

        int totalSeconds, count;
        string status;
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT o.status, p.total_seconds, p.generate_count
                FROM purchase_orders o
                JOIN purchase_packages p ON p.id = o.package_id
                WHERE o.order_no = @orderNo LIMIT 1
                """;
            query.Parameters.AddWithValue("@orderNo", orderNo);
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return (null, "订单不存在。");
            status = reader.GetString("status");
            totalSeconds = reader.GetInt32("total_seconds");
            count = reader.GetInt32("generate_count");
        }

        if (status == "delivered") return (null, "订单已发货，请勿重复操作。");
        if (status == "cancelled") return (null, "订单已取消，无法发货。");

        var key = await _licenses.CreateAsync(totalSeconds, count, ct);
        await using (var update = connection.CreateCommand())
        {
            update.CommandText = """
                UPDATE purchase_orders
                SET status = 'delivered', license_key = @key, delivered_at = UTC_TIMESTAMP(), updated_at = UTC_TIMESTAMP()
                WHERE order_no = @orderNo
                """;
            update.Parameters.AddWithValue("@key", key);
            update.Parameters.AddWithValue("@orderNo", orderNo);
            await update.ExecuteNonQueryAsync(ct);
        }

        return (await GetOrderAsync(orderNo, ct), null);
    }

    // 管理端：取消订单（仅未发货的订单可取消）。
    public async Task<(PurchaseOrderResult? Order, string? Error)> CancelAsync(string orderNo, CancellationToken ct = default)
    {
        orderNo = Normalize(orderNo);
        if (orderNo.Length == 0) return (null, "订单号不能为空。");

        await using var connection = await OpenAsync(ct);
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE purchase_orders
            SET status = 'cancelled', updated_at = UTC_TIMESTAMP()
            WHERE order_no = @orderNo AND status IN ('pending', 'review')
            """;
        update.Parameters.AddWithValue("@orderNo", orderNo);
        if (await update.ExecuteNonQueryAsync(ct) == 0)
            return (null, "订单状态不允许取消。");

        return (await GetOrderAsync(orderNo, ct), null);
    }

    // 管理端：列出全部订单（含状态与交易单号）。
    public async Task<IReadOnlyList<PurchaseOrderResult>> ListOrdersAsync(CancellationToken ct = default)
    {
        var result = new List<PurchaseOrderResult>();
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.order_no, o.package_id, p.name AS package_name, p.total_seconds, p.generate_count,
                   o.amount, o.status, o.transaction_no, o.contact, o.license_key, o.created_at
            FROM purchase_orders o
            JOIN purchase_packages p ON p.id = o.package_id
            ORDER BY o.created_at DESC
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new PurchaseOrderResult(
                reader.GetString("order_no"),
                reader.GetInt32("package_id"),
                reader.GetString("package_name"),
                reader.GetInt32("total_seconds"),
                reader.GetInt32("generate_count"),
                reader.GetDecimal("amount"),
                reader.GetString("status"),
                reader.IsDBNull(reader.GetOrdinal("transaction_no")) ? null : reader.GetString("transaction_no"),
                reader.IsDBNull(reader.GetOrdinal("contact")) ? null : reader.GetString("contact"),
                reader.IsDBNull(reader.GetOrdinal("license_key")) ? null : reader.GetString("license_key"),
                ToOffset(reader.GetDateTime("created_at"))));
        }
        return result;
    }

    private async Task<string> CreateUniqueOrderNoAsync(MySqlConnection connection, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var orderNo = "PB" + DateTime.UtcNow.ToString("yyMMddHHmmss") + CreateSuffix(4);
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT EXISTS(SELECT 1 FROM purchase_orders WHERE order_no = @no)";
            check.Parameters.AddWithValue("@no", orderNo);
            var exists = (long)(await check.ExecuteScalarAsync(ct) ?? 0L) > 0;
            if (!exists) return orderNo;
        }
        throw new InvalidOperationException("生成订单号失败，请重试。");
    }

    private static string CreateSuffix(int length)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        return string.Concat(bytes.Select(b => OrderAlphabet[b % OrderAlphabet.Length]));
    }

    private static string Normalize(string? orderNo)
    {
        if (string.IsNullOrWhiteSpace(orderNo)) return "";
        return new string(orderNo.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
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
