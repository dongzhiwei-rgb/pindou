// 文件：CollabSweeper.cs
// 用途：好友联机房间的后台定时清扫任务。
// 核心职责：周期调用 CollabService.SweepAsync——房主授权过期关闭房间、好友授权到期自动踢出、
//         清理过期格子锁与过期申请，防止内存无限增长。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-25

namespace Pindou.Api.Services;

public sealed class CollabSweeper : BackgroundService
{
    // 清扫周期 3 秒：超时申请/过期权限、离线成员等即时清理并广播，避免审批队列过期后延迟移除。
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    private readonly CollabService _collab;
    private readonly ILogger<CollabSweeper> _logger;

    public CollabSweeper(CollabService collab, ILogger<CollabSweeper> logger)
    {
        _collab = collab;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("好友联机房间清扫任务已启动：每 3 秒清理授权过期成员与过期锁。");
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await _collab.SweepAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    // 单轮清扫失败不中断任务，避免数据库瞬时异常导致联机清理永久停摆。
                    _logger.LogWarning(exception, "好友联机清扫任务本轮执行失败。");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 服务停止。
        }
    }
}
