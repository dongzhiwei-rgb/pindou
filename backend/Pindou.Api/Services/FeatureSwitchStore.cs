// 文件：FeatureSwitchStore.cs
// 用途：运行时特性开关存储：授权（含试用）可由管理员接口动态切换，无需修改配置文件或重启。
// 核心职责：以 appsettings.json 的 License:EnableAuth 作为初始值，支持通过 /api/admin/switches 接口在运行期读写。
// 版权：@董志伟-联系方式-makabak1204
// 最后修改：2026-08-23

namespace Pindou.Api.Services;

public sealed class FeatureSwitchStore
{
    private readonly object _lock = new();
    private bool _enableAuth;

    public FeatureSwitchStore(IConfiguration configuration)
    {
        _enableAuth = configuration.GetValue("License:EnableAuth", true);
    }

    // 授权（含试用）总开关：false = 免授权，所有用户可用全部功能；true = 有密钥按密钥校验，无密钥按 IP 试用。
    public bool EnableAuth
    {
        get { lock (_lock) return _enableAuth; }
        set { lock (_lock) _enableAuth = value; }
    }

    public object Snapshot() =>
        new { enableAuth = EnableAuth };

    public void Apply(bool? enableAuth)
    {
        lock (_lock)
        {
            if (enableAuth.HasValue) _enableAuth = enableAuth.Value;
        }
    }
}
