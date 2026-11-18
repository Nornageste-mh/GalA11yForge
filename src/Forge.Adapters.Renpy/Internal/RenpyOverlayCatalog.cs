using GalA11yForge.Core.Models;

namespace GalA11yForge.Adapters.Renpy.Internal;

/// <summary>覆盖层模板与生成物的唯一目录表；生成、清单、校验都从这里取，避免三处各写一份。</summary>
internal static class RenpyOverlayCatalog
{
    public const string PlatformDirectory = "game/a11y_platform";

    public const string ManifestFileName = "a11y_manifest.json";

    public const string DeployPlanFileName = "deploy_plan.json";

    public static readonly string[] PlatformFileNames =
    {
        "00_contract.rpy",
        "01_speech.rpy",
        "02_dialogue.rpy",
        "03_menu.rpy",
        "04_focus.rpy",
        "05_keys.rpy",
    };

    /// <summary>9 个需要参数化渲染的内容文件；路径同时是“相对游戏根目录”的部署路径。</summary>
    public static readonly RenpyTemplateFile[] ContentFiles =
    {
        new(PlatformDirectory + "/00_contract.rpy", "Templates/platform/00_contract.rpy",
            "contract", "契约 / 日志 / 状态 / 版本兼容层", ArtifactKind.Script),
        new(PlatformDirectory + "/01_speech.rpy", "Templates/platform/01_speech.rpy",
            "speech", "语音后端链（NVDA→SAPI→日志）与 tts_function 接管", ArtifactKind.Script),
        new(PlatformDirectory + "/02_dialogue.rpy", "Templates/platform/02_dialogue.rpy",
            "dialogue", "config.say_menu_text_filter 对话/选项钩子", ArtifactKind.Script),
        new(PlatformDirectory + "/03_menu.rpy", "Templates/platform/03_menu.rpy",
            "menu", "菜单/选项整体播报与焦点文本提取", ArtifactKind.Script),
        new(PlatformDirectory + "/04_focus.rpy", "Templates/platform/04_focus.rpy",
            "focus", "焦点轮询、整屏朗读与 periodic_callbacks 心跳", ArtifactKind.Script),
        new(PlatformDirectory + "/05_keys.rpy", "Templates/platform/05_keys.rpy",
            "keys", "init 1200 键位登记与钩子重挂", ArtifactKind.Script),
        new("a11y_say.vbs", "Templates/root/a11y_say.vbs",
            "sapi-bridge", "SAPI 语音桥（wscript，参数经 UTF-16 临时文件传入）", ArtifactKind.Script),
        new("README-A11Y.txt", "Templates/root/README-A11Y.txt",
            "readme", "安装 / 卸载 / 按键 / 日志 / 可调项说明", ArtifactKind.Documentation),
        new("uninstall.ps1", "Templates/root/uninstall.ps1",
            "uninstall", "按清单 SHA256 核对后卸载，并清理 .rpyc 与空目录（带 BOM，兼容 Windows PowerShell 5.1）",
            ArtifactKind.Script, Utf8Bom: true),
    };

    public static IReadOnlyList<GeneratedArtifact> CreateArtifacts()
    {
        var artifacts = ContentFiles
            .Select(file => new GeneratedArtifact
            {
                Name = Path.GetFileName(file.RelativePath),
                Kind = file.Kind,
                RelativePath = file.RelativePath,
                Description = file.Description,
            })
            .ToList();

        artifacts.Add(new GeneratedArtifact
        {
            Name = ManifestFileName,
            Kind = ArtifactKind.Config,
            RelativePath = ManifestFileName,
            Description = "覆盖层清单：9 个内容文件的字节数与 SHA256，供卸载与校验",
        });
        artifacts.Add(new GeneratedArtifact
        {
            Name = DeployPlanFileName,
            Kind = ArtifactKind.Config,
            RelativePath = DeployPlanFileName,
            Description = "部署计划：mkdir/copy 步骤、冲突标记、只读 guard 与验收提示",
        });
        return artifacts;
    }

    public static bool IsKnownContentPath(string relativePath)
        => ContentFiles.Any(file => string.Equals(file.RelativePath, relativePath, StringComparison.Ordinal));
}

internal sealed record RenpyTemplateFile(
    string RelativePath,
    string EmbeddedName,
    string Role,
    string Description,
    ArtifactKind Kind,
    bool Utf8Bom = false);
