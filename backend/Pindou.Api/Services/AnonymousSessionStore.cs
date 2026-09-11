// 文件：AnonymousSessionStore.cs
// 用途：匿名访问者的短期 HttpOnly 会话存储（阶段2/3 认证重构：替代前端 HMAC 签名提供请求身份）。
// 核心职责：为每个访问者自动签发 24 小时会话 Cookie；会话不阻断访问，业务鉴权仍由各接口自身的
//           密钥/会话/管理 Cookie 承担。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-24

using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Pindou.Api.Services;

public sealed class AnonymousSessionStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private const int MaxSessions = 100000;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);

    public string Create()
    {
        RemoveExpired();
        if (_sessions.Count >= MaxSessions)
            throw new InvalidOperationException("匿名会话数已达上限。");
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        _sessions[token] = DateTimeOffset.UtcNow.Add(Lifetime);
        return token;
    }

    public bool IsValid(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (!_sessions.TryGetValue(token, out var expiresAt)) return false;
        if (expiresAt <= DateTimeOffset.UtcNow) { _sessions.TryRemove(token, out _); return false; }
        return true;
    }

    public void RemoveExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _sessions)
            if (pair.Value <= now) _sessions.TryRemove(pair.Key, out _);
    }
}
