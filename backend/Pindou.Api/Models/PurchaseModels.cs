// 文件：PurchaseModels.cs
// 用途：声明「自助购买密钥」的套餐、订单与前后端交互契约。
// 核心职责：定义可售套餐的持久化结构，以及创建订单、提交支付单号、核验发货时传递的数据。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-23

namespace Pindou.Api.Models;

// 持久化存储的可售套餐；TotalSeconds 为密钥时长（自动转为累计在线秒数），Count 为可生成次数。
public sealed class PurchasePackage
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public int TotalSeconds { get; init; }
    public int Count { get; init; }
    public decimal Price { get; init; }
    public string? Description { get; init; }
    public bool Active { get; init; }
    public int SortOrder { get; init; }
}

// 持久化存储的购买订单；状态流转：pending（已创建待支付）→ review（已提交单号待核验）→ delivered（已发货出密钥）/ cancelled（已取消）。
public sealed class PurchaseOrder
{
    public string OrderNo { get; init; } = "";
    public int PackageId { get; init; }
    public decimal Amount { get; init; }
    public string Status { get; set; } = "pending";
    public string? TransactionNo { get; set; }
    public string? Contact { get; init; }
    public string? Note { get; init; }
    public string? BuyerIp { get; init; }
    public string? LicenseKey { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}

// 创建订单的请求体；PackageId 指定所选套餐，Contact/Note 为可选联系方式与备注。
public sealed record CreatePurchaseRequest(int? PackageId, string? Contact, string? Note);

// 提交支付单号的请求体；TransactionNo 为微信/支付宝交易流水号，便于人工核验。
public sealed record SubmitPurchasePaymentRequest(string OrderNo, string TransactionNo);

// 返回给前端/管理端的订单快照；携带套餐信息，便于展示价格、时长、次数与已发货的密钥。
public sealed record PurchaseOrderResult(
    string OrderNo,
    int PackageId,
    string PackageName,
    int TotalSeconds,
    int Count,
    decimal Amount,
    string Status,
    string? TransactionNo,
    string? Contact,
    string? LicenseKey,
    DateTimeOffset CreatedAt);

// 收款配置：二维码、收款方名称与付款须知；从 appsettings 的 Payment 节读取。
public sealed record PurchasePaymentConfig(
    string QrImageUrl,
    string PayeeName,
    string PayeeAccount,
    string Note);

// 套餐列表接口返回结果：收款配置 + 可售套餐列表。
public sealed record PurchasePackagesResult(
    PurchasePaymentConfig Payment,
    IReadOnlyList<PurchasePackage> Packages);
