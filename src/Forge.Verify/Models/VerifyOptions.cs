using GalA11yForge.Core.Models;
using GalA11yForge.Verify.Abstractions;
using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify.Models;

public enum VerifyMode
{
    /// <summary>安装后检查：文件/哈希/BepInEx/日志/环境。</summary>
    Install = 0,

    /// <summary>卸载回滚后检查：原文件还原、新增文件删除、备份完整性。</summary>
    Rollback = 1,
}

public enum VerifyInstallerKind
{
    /// <summary>根据账本与游戏目录自动判断是否 BepInEx 补丁。</summary>
    Auto = 0,

    BepInEx = 1,

    /// <summary>覆盖式补丁（例如直接替换 data.xp3），不检查 BepInEx 布局。</summary>
    Overlay = 2,

    Unknown = 3,
}

/// <summary>
/// 一次离线验证的输入。所有路径都会在服务内部做安全校验。
/// </summary>
public sealed class VerifyOptions
{
    public string GameDirectory { get; set; } = string.Empty;

    public string WorkspaceRoot { get; set; } = string.Empty;

    public string? Slug { get; set; }

    /// <summary>显式指定账本路径；为空时按工作区约定查找。</summary>
    public string? ManifestPath { get; set; }

    /// <summary>调用方已加载的账本；非空时优先于 ManifestPath。</summary>
    public DeployManifest? Manifest { get; set; }

    /// <summary>显式指定基线路径；为空时使用 05-deploy/baseline.json。</summary>
    public string? BaselinePath { get; set; }

    /// <summary>调用方已加载的基线；非空时优先于 BaselinePath。</summary>
    public GameBaseline? Baseline { get; set; }

    public VerifyMode Mode { get; set; } = VerifyMode.Install;

    public VerifyInstallerKind InstallerKind { get; set; } = VerifyInstallerKind.Auto;

    /// <summary>是否做只读语音后端探测。默认 true；探测不会发声。</summary>
    public bool ProbeEnvironment { get; set; } = true;

    /// <summary>测试或上层注入的环境探测实现；为空时使用 WindowsEnvironmentProbe。</summary>
    public IEnvironmentProbe? EnvironmentProbe { get; set; }

    /// <summary>BepInEx 日志路径；为空时使用 &lt;游戏目录&gt;/BepInEx/LogOutput.log。</summary>
    public string? LogPath { get; set; }

    public int LogTailLines { get; set; } = 400;

    /// <summary>
    /// 语音日志路径：给“安装后到底有没有发声”检查用。为空时自动找
    /// BepInEx/LogOutput.log、游戏根/a11y_speech.log 或 游戏根/game/a11y_speech.log。
    /// </summary>
    public string? SpeechLogPath { get; set; }

    /// <summary>语音日志最多读取的字节数（超长只读尾部）；默认 4 MB。</summary>
    public int SpeechLogMaxBytes { get; set; } = 4 * 1024 * 1024;
}
