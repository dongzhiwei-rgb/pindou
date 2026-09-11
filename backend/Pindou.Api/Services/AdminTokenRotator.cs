// 文件：AdminTokenRotator.cs
// 用途：后台定时任务，每天本地时间 5:00 自动轮换管理密钥（X-Admin-Token）。
// 核心职责：计算到下一个 5:00 的等待时长，到点后轮换密钥并持久化到 admin-token.txt，
//           同时把新密钥写入日志，供运维在后台管理页离线时获取。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-24

using System.Security.Cryptography;
using System.Text;

namespace Pindou.Api.Services;

public sealed class AdminTokenRotator : BackgroundService
{
    private readonly AdminTokenStore _store;
    private readonly ILogger<AdminTokenRotator> _logger;

    public AdminTokenRotator(AdminTokenStore store, ILogger<AdminTokenRotator> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("管理密钥自动轮换已启用：每天本地时间 5:00 更新，新密钥写入 admin-token.txt");
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = DelayToNextRotation();
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var newToken = _store.Rotate();
            // 安全：日志只记录令牌指纹，不输出令牌正文，避免日志读取权限等同于管理权限。
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(newToken)))[..12].ToLowerInvariant();
            _logger.LogWarning("管理密钥已按计划更新（本地时间 {Now:yyyy-MM-dd HH:mm:ss}），令牌指纹：{Fingerprint}", DateTime.Now, fingerprint);
        }
    }

    // 距离下一个本地时间 5:00 的等待时长；若系统时钟被调整，循环每次重新计算。
    private static TimeSpan DelayToNextRotation()
    {
        var now = DateTime.Now;
        var next = now.Date.AddHours(5);
        if (next <= now) next = next.AddDays(1);
        return next - now;
    }
}
