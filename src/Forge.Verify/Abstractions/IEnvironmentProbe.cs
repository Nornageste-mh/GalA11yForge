using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify.Abstractions;

/// <summary>
/// 只读语音后端探测。实现必须：
/// 1) 不启动游戏；
/// 2) 不加载 Tolk、不调用 NVDA/争渡/SAPI 的朗读 API；
/// 3) 不产生任何声音；
/// 4) 只返回进程、文件、注册表等可复核事实。
/// </summary>
public interface IEnvironmentProbe
{
    Task<EnvironmentSnapshot> ProbeAsync(string gameDirectory, CancellationToken cancellationToken = default);
}
