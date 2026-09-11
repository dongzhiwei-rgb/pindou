// 文件：AdminSessionStore.cs
// 用途：后台管理 HttpOnly Cookie 会话存储（短期、服务端内存、容量受限）。
// 核心职责：登录时签发随机会话令牌（8 小时有效），鉴权时校验；替代前端 localStorage 长期存储管理令牌。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-24

using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Pindou.Api.Services;

public sealed class AdminSessionStore
{
    private const int MaxSessions = 5000;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);

    public string Create()
    {
        RemoveExpired();
        if (_sessions.Count >= MaxSessions)
            throw new InvalidOperationException("管理会话数已达上限。");
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        _sessions[token] = DateTimeOffset.UtcNow.Add(SessionLifetime);
        return token;
    }

    public bool IsValid(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (!_sessions.TryGetValue(token, out var expiresAt)) return false;
        if (expiresAt <= DateTimeOffset.UtcNow) { _sessions.TryRemove(token, out _); return false; }
        return true;
    }

    public void Revoke(string token)
    {
        if (!string.IsNullOrEmpty(token)) _sessions.TryRemove(token, out _);
    }

    public void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _sessions)
            if (pair.Value <= now) _sessions.TryRemove(pair.Key, out _);
    }
}
