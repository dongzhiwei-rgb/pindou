// 文件：KeyStateEventHub.cs
// 用途：维护后台管理页面的密钥状态长连接（SSE），密钥上线/下线/停用/删除/心跳状态变化时即时推送，
//       后台管理页收到推送后批量刷新当前页密钥状态，无需手动刷新。
// 核心职责：向全部订阅连接广播「密钥状态已变化」信号；事件本身不含敏感数据，仅作刷新触发。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-23

using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Pindou.Api.Services;

public sealed class KeyStateEventHub
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

    // 向所有订阅连接推送「密钥状态已变化」事件。
    public async Task BroadcastAsync(CancellationToken ct = default)
    {
        var message = "event: licenses-changed\ndata: {\"changed\":true}\n\n";
        foreach (var channel in _channels.Values)
            await channel.Writer.WriteAsync(message, ct);
    }
}
