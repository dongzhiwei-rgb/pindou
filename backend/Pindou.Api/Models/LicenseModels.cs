// 文件：LicenseModels.cs
// 用途：声明商用授权的密钥记录、校验状态与管理员管理契约。
// 核心职责：定义密钥的持久化结构，以及生成、查询、校验时前后端传递的数据。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-23

namespace Pindou.Api.Models;

// 持久化存储的密钥记录；IsRevoked 表示密钥已被管理员吊销（逻辑失效、保留记录），吊销后任何使用都会返回 revoked 状态。
public sealed class LicenseEntry
{
    public string Key { get; init; } = "";
    public string? UserId { get; init; }
    public long TotalSeconds { get; init; }
    public long UsedSeconds { get; set; }
    // 绝对时间模型下由服务器计算的剩余秒数（按 started_at 起算，离线也持续消耗）。
    public long RemainingSeconds { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public int RemainingCount { get; set; }
    public bool IsRevoked { get; init; }
    // 列表状态：unused 未使用 / online 在线中 / offline 已离线 / revoked 已停用。
    public string Status { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
}

// 管理员生成密钥的请求体；TotalHours 为期限小时数（工具/接口按小时生成，自动转为秒），TotalSeconds 兼容精确秒数，Count 为每个密钥的可生成次数，Number 为本次批量生成的密钥数量（默认 1，上限 100）。
public sealed record CreateLicenseRequest(int? TotalSeconds, int? TotalHours, int Count, int? Number = null);

// 批量吊销/删除密钥的请求体；Keys 为待处理的密钥列表（单次最多 100 个）。
public sealed record RevokeLicenseRequest(IReadOnlyList<string> Keys);

// 前端激活/查询密钥时返回的校验状态；KickInSeconds 是兼容字段，即时下线模式下始终为 null。
public sealed record LicenseStatus(
    string Status,
    int? RemainingSeconds,
    int? RemainingCount,
    string? Message,
    int? KickInSeconds = null);

// 登录（密钥即账号）的结果；IsNewUser 区分该密钥首次登录还是老用户重登，HasSave 表示是否存在可同步的云端存档，SessionToken 为本次登录的会话令牌（用于单设备在线校验）。
public sealed record LoginResult(
    string Status,
    int? RemainingSeconds,
    int? RemainingCount,
    string? Message,
    bool IsNewUser,
    bool HasSave,
    string? SessionToken);

// 无密钥访客的试用状态；RemainingSeconds 为试用窗口剩余秒数，归零后 Expired 为 true。
public sealed record TrialStatus(
    int RemainingSeconds,
    int RemainingGenerations,
    int TotalMinutes,
    bool Expired);

// 密钥使用详情：包含上次登录 IP、最后活跃时间与在线状态（由管理员接口返回）。
public sealed record LicenseDetail(
    string Key,
    string? UserId,
    long TotalSeconds,
    long UsedSeconds,
    long RemainingSeconds,
    DateTimeOffset? LastHeartbeatAt,
    int RemainingCount,
    bool IsRevoked,
    DateTimeOffset CreatedAt,
    string? LastIp,
    DateTimeOffset? LastSeenAt,
    bool Online);

// 管理员修改运行时特性开关的请求体；授权（含试用）由单个 EnableAuth 参数控制，缺省时保持原状态。
public sealed record FeatureSwitchRequest(bool? EnableAuth);
