// 文件：SseTicketStore.cs
// 用途：SSE 长连接的一次性短期票据存储，避免把授权密钥/会话令牌/管理令牌放入 URL。
// 核心职责：登录态校验后签发 60 秒有效、单次使用的票据；SSE 端点凭票据建立连接，票据可取关联数据。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-24

using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Pindou.Api.Services;

public sealed class SseTicketStore
{
    private const int MaxTickets = 5000;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, SseTicket> _tickets = new(StringComparer.Ordinal);

    // 签发一次性票据；extra 用于携带关联数据（如会话令牌），SSE 建立时恢复。
    public string Create(string purpose, string? extra = null)
    {
        RemoveExpired();
        if (_tickets.Count >= MaxTickets)
            throw new InvalidOperationException("请求过于频繁，请稍后重试。");
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        _tickets[token] = new SseTicket(purpose, extra, DateTimeOffset.UtcNow.Add(Lifetime));
        return token;
    }

    // 校验并消费票据（单次使用，取走即作废）；返回票据携带的关联数据。
    public (bool Ok, string? Extra) Consume(string token, string purpose)
    {
        if (string.IsNullOrEmpty(token)) return (false, null);
        if (!_tickets.TryRemove(token, out var ticket)) return (false, null);
        if (ticket.Purpose != purpose || ticket.ExpiresAt <= DateTimeOffset.UtcNow) return (false, null);
        return (true, ticket.Extra);
    }

    private void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _tickets)
            if (pair.Value.ExpiresAt <= now) _tickets.TryRemove(pair.Key, out _);
    }

    private sealed record SseTicket(string Purpose, string? Extra, DateTimeOffset ExpiresAt);
}
