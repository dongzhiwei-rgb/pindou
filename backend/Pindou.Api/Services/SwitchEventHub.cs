// 文件：SwitchEventHub.cs
// 用途：维护所有前端的开关状态长连接（SSE），管理员接口修改授权开关时即时推送变化，前端无需刷新即可更新。
// 核心职责：向全部订阅连接广播开关变化事件；无订阅者时静默。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-23

using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Pindou.Api.Services;

public sealed class SwitchEventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _channels = new();

    public Guid Subscribe(Channel<string> channel)
    {
        var id = Guid.NewGuid();
        _channels[id] = channel;
        return id;
    }

    public void Unsubscribe(Guid id)
    {
        _channels.TryRemove(id, out _);
    }

    // 向所有订阅连接推送开关变化事件。
    public async Task BroadcastAsync(bool enableAuth, CancellationToken ct = default)
    {
        var payload = $"{{\"enableAuth\":{enableAuth.ToString().ToLowerInvariant()}}}";
        var message = $"event: switch-changed\ndata: {payload}\n\n";
        foreach (var channel in _channels.Values)
            await channel.Writer.WriteAsync(message, ct);
    }
}
