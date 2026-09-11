// 文件：BrowserHandoffStore.cs
// 用途：短期保存跨浏览器接力所需的拼豆图纸快照。
// 核心职责：生成高强度随机令牌、限制快照大小、过期清理，并保证数据只能读取一次；
//           设置条目数 / 单 IP 条目数 / 全局总字节容量上限，防止内存耗尽攻击（DoS）。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-24

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pindou.Api.Models;

namespace Pindou.Api.Services;

public sealed class BrowserHandoffStore
{
    private const int MaximumPayloadBytes = 2 * 1024 * 1024;
    private const int MaxEntries = 2000;
    private const int MaxEntriesPerIp = 100;
    private const long MaxTotalBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, HandoffEntry> _entries = new(StringComparer.Ordinal);
    private long _totalBytes;

    public BrowserHandoffCreated Create(JsonElement project, string? clientIp = null)
    {
        if (project.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("图纸接力数据格式不正确。");

        var projectJson = project.GetRawText();
        if (Encoding.UTF8.GetByteCount(projectJson) > MaximumPayloadBytes)
            throw new InvalidDataException("当前图纸数据过大，无法进行跨浏览器接力。");

        RemoveExpiredEntries();
        // 容量上限：条目数 / 单 IP 条目数 / 全局总字节，超过即拒绝，避免持续提交占满内存。
        if (_entries.Count >= MaxEntries)
            throw new InvalidOperationException("接力数据已达容量上限，请稍后重试。");
        var ipKey = clientIp ?? "unknown";
        if (!string.Equals(ipKey, "unknown", StringComparison.Ordinal))
        {
            var ipCount = 0;
            foreach (var item in _entries.Values)
                if (string.Equals(item.IpKey, ipKey, StringComparison.Ordinal)) ipCount++;
            if (ipCount >= MaxEntriesPerIp)
                throw new InvalidOperationException("当前网络发送的接力数据过多，请稍后重试。");
        }
        var bytes = Encoding.UTF8.GetByteCount(projectJson);
        if (Interlocked.Add(ref _totalBytes, bytes) > MaxTotalBytes)
        {
            Interlocked.Add(ref _totalBytes, -bytes);
            throw new InvalidOperationException("接力数据已达容量上限，请稍后重试。");
        }
        var token = CreateToken();
        var expiresAt = DateTimeOffset.UtcNow.Add(Lifetime);
        _entries[token] = new HandoffEntry(projectJson, expiresAt, ipKey);
        return new BrowserHandoffCreated(token, expiresAt);
    }

    public string? Take(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;
        if (!_entries.TryRemove(token, out var entry)) return null;
        Interlocked.Add(ref _totalBytes, -Encoding.UTF8.GetByteCount(entry.ProjectJson));
        return entry.ExpiresAt > DateTimeOffset.UtcNow ? entry.ProjectJson : null;
    }

    private void RemoveExpiredEntries()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in _entries)
        {
            if (item.Value.ExpiresAt <= now && _entries.TryRemove(item.Key, out var removed))
                Interlocked.Add(ref _totalBytes, -Encoding.UTF8.GetByteCount(removed.ProjectJson));
        }
    }

    private static string CreateToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private sealed record HandoffEntry(string ProjectJson, DateTimeOffset ExpiresAt, string IpKey);
}
