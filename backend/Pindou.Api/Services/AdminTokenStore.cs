// 文件：AdminTokenStore.cs
// 用途：运行时管理密钥（X-Admin-Token）存储，支持每日定时轮换，无需修改配置文件或重启。
// 核心职责：以 appsettings.json 的 License:AdminToken 作为初始值；每日 5:00 自动轮换后将新密钥
//           持久化到 admin-token.txt，服务重启后继续沿用轮换后的密钥，避免回退到配置初值。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-26

using System.Security.Cryptography;

namespace Pindou.Api.Services;

public sealed class AdminTokenStore
{
    private readonly object _lock = new();
    private readonly string _filePath;
    private readonly ILogger<AdminTokenStore> _logger;
    private string _token;

    public AdminTokenStore(IConfiguration configuration, IHostEnvironment environment, ILogger<AdminTokenStore> logger)
    {
        _logger = logger;
        // 优先使用配置的令牌文件路径（Docker 部署指向持久化卷）。本地默认写到用户应用数据目录，
        // 避免运行时密钥落进源码、构建产物或发布压缩包。
        _filePath = configuration["Data:AdminTokenFile"] ?? "";
        if (string.IsNullOrWhiteSpace(_filePath))
            _filePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PindouStudio",
                "admin-token.txt");
        // 优先读取已持久化的轮换密钥（轮换后重启不回落），否则使用配置文件初始值。
        var persisted = File.Exists(_filePath) ? File.ReadAllText(_filePath).Trim() : "";
        var configToken = configuration["License:AdminToken"] ?? "";
        // 高-01 修复：文件仍保存旧默认密钥而配置已换成强密钥时，视为旧文件残留，
        // 以新配置为准并覆写文件，避免旧默认值静默覆盖新配置导致密钥回退。
        if (!string.IsNullOrWhiteSpace(persisted) &&
            persisted == "change-this-admin-token" &&
            !string.IsNullOrWhiteSpace(configToken) &&
            configToken != "change-this-admin-token")
        {
            _token = configToken;
            Persist(configToken);
        }
        else
        {
            _token = !string.IsNullOrWhiteSpace(persisted) ? persisted : configToken;
            if (string.IsNullOrWhiteSpace(_token) || IsPlaceholder(_token))
            {
                _token = NewToken();
                Persist(_token);
            }
            else if (string.IsNullOrWhiteSpace(persisted))
            {
                // 首次启动：把配置初值写入文件，保证重启后有唯一的密钥来源。
                Persist(_token);
            }
        }
    }

    // 当前生效的管理密钥。
    public string Token { get { lock (_lock) return _token; } }

    // 最近一次轮换时间（UTC）；从未轮换过则为 null。
    public DateTimeOffset? LastRotatedAt { get; private set; }

    // 生成新的随机管理密钥并持久化，返回新密钥。
    public string Rotate()
    {
        var next = NewToken();
        lock (_lock)
        {
            _token = next;
            LastRotatedAt = DateTimeOffset.UtcNow;
        }
        Persist(next);
        return next;
    }

    private void Persist(string token)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_filePath, token);
        }
        catch (Exception exception)
        {
            // 写入失败记录警告（可能是目录不可写/卷未挂载），避免静默导致重启后回退旧令牌。
            _logger.LogWarning(exception, "管理令牌文件写入失败：{Path}", _filePath);
        }
    }

    // 32 位十六进制随机密钥（16 字节），足够随机且便于复制输入。
    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static bool IsPlaceholder(string token) =>
        token.Equals("change-this-admin-token", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith("CHANGE-ME", StringComparison.OrdinalIgnoreCase);
}
