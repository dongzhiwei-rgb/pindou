// 文件：SessionEventHub.cs
// 用途：维护各设备的长连接（SSE），新设备登录时向旧设备即时推送下线通知。
// 按「会话令牌」区分连接，同一账号多设备各自订阅，只通知已被替换的旧会话。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-22

using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Pindou.Api.Services;

public sealed class SessionEventHub
{
    private readonly ConcurrentDictionary<string, Channel<string>> _channels = new();

    public void Subscribe(string sessionToken, Channel<string> channel)
    {
        _channels[sessionToken] = channel;
    }

    public void Unsubscribe(string sessionToken, Channel<string> channel)
    {
        if (_channels.TryGetValue(sessionToken, out var current) && ReferenceEquals(current, channel))
            _channels.TryRemove(sessionToken, out _);
    }

    // 向已失效的旧会话推送即时下线事件；设备不在线时静默忽略，后续心跳和业务请求仍会拒绝旧令牌。
    public async Task NotifySessionKickedAsync(string sessionToken, CancellationToken ct = default)
    {
        if (_channels.TryGetValue(sessionToken, out var channel))
            await channel.Writer.WriteAsync("event: session-kicked\ndata: {\"message\":\"该账号已在其他设备登录，本设备已下线\"}\n\n", ct);
    }
}