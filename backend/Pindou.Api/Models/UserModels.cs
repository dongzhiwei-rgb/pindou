// 文件：UserModels.cs
// 用途：声明设备账号注册、画布存档与密钥操作接口的请求契约。
// 核心职责：统一前后端在这些接口上的 JSON 字段，保证设备 UUID 作为账号主键贯穿全链路。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-22

namespace Pindou.Api.Models;

// 画布存档写入的请求体；Key 为密钥（密钥即账号）。
public sealed record SaveRequest(string Key, string Payload);

// 密钥会话/心跳共用的请求体；SessionToken 用于「单设备在线」校验。
public sealed record LicenseActionRequest(string Key, string? SessionToken);

// 管理后台登录请求体：提交管理密钥，服务端校验后签发 HttpOnly 会话 Cookie。
public sealed record AdminLoginRequest(string Token);

// SSE 票据申请请求体：Purpose 为 kick（用户会话）/ states（管理）/ switches（公开）。
public sealed record SseTicketRequest(string Purpose);