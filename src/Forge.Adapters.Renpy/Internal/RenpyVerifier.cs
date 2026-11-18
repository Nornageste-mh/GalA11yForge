using System.Text.Json;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Adapters.Renpy.Internal;

/// <summary>
/// 步骤五：离线静态校验入口。只读输出目录与游戏目录，不启动游戏、不写游戏目录。
/// </summary>
internal sealed class RenpyVerifier
{
    private readonly RenpyStaticLinter _linter = new();

    public Task<ForgeResult<VerificationReport>> VerifyAsync(
        GameContext context,
        DeployResult? deployResult,
        CancellationToken cancellationToken)
    {
        var output = RenpyPaths.DefaultGeneratedDirectory(context);
        return VerifyDirectoryAsync(context, output, cancellationToken);
    }

    public Task<ForgeResult<VerificationReport>> VerifyDirectoryAsync(
        GameContext context,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.GameDirectory))
        {
            return Task.FromResult(ForgeResult<VerificationReport>.Failure(
                ForgeErrorCodes.ArgumentInvalid, "游戏目录为空，无法校验补丁。", "用 --game 指定游戏根目录。"));
        }

        var requested = string.IsNullOrWhiteSpace(outputDirectory)
            ? RenpyPaths.DefaultGeneratedDirectory(context)
            : outputDirectory;

        if (string.IsNullOrWhiteSpace(requested))
        {
            return Task.FromResult(ForgeResult<VerificationReport>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                "没有可校验的生成目录。",
                "先运行 generate，或用 --out 指定生成目录。"));
        }

        string root;
        try
        {
            root = Path.GetFullPath(requested);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(ForgeResult<VerificationReport>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                $"生成目录不是合法路径：{requested}",
                "使用工作区内的绝对路径。"));
        }

        if (!Directory.Exists(root))
        {
            return Task.FromResult(ForgeResult<VerificationReport>.Failure(
                ForgeErrorCodes.ArtifactMissing,
                $"生成目录不存在：{root}",
                "先运行 generate 生成 11 个文件，再运行 verify。"));
        }

        var gameDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.GameDirectory));
        var linterResult = _linter.Run(root, expectedGameRoot: gameDirectory);
        var checks = linterResult.Checks.ToList();

        var contractPath = Path.Combine(root, "game", "a11y_platform", "00_contract.rpy");
        if (File.Exists(contractPath))
        {
            var contract = RenpySupport.ReadTextOrNull(contractPath) ?? string.Empty;
            var hasMarker = contract.Contains("覆盖层已加载", StringComparison.Ordinal)
                            || contract.Contains("a11y_platform", StringComparison.Ordinal);
            checks.Add(new VerificationCheck
            {
                Name = "D7 覆盖层加载日志标记存在",
                Passed = hasMarker,
                Detail = hasMarker ? "00_contract.rpy 含加载日志标记" : "未找到加载日志标记",
            });
        }

        var manifest = ReadManifest(root);
        var manualTodo = new List<ManualTodoItem>
        {
            new()
            {
                Id = "mt-v01",
                Title = "真机验收：真实启动游戏并接 NVDA",
                Detail = "静态检查只能证明文件齐全与结构存在，不能证明 Ren'Py 运行时真的加载、朗读顺序正确。"
                         + "请在测试机上启动游戏，检查 a11y_speech.log 与对话/选项/菜单/存读档。",
                Blocking = true,
            },
            new()
            {
                Id = "mt-v02",
                Title = "在游戏副本上演练卸载",
                Detail = "运行 uninstall.ps1，确认只删除清单内且哈希一致的文件；确认不会误删游戏自身资源。",
                Blocking = false,
            },
        };

        if (manifest?.Game?.Root is { Length: > 0 } manifestRoot
            && manifestRoot.Contains("steamapps", StringComparison.OrdinalIgnoreCase))
        {
            manualTodo.Add(new ManualTodoItem
            {
                Id = "mt-v03",
                Title = "Steam 目录只读：不要直接在 steamapps 内安装",
                Detail = "清单记录的安装根目录位于 steamapps。请复制到可写目录或按 README-A11Y.txt 手工安装。",
                Blocking = false,
            });
        }

        if (!linterResult.Passed)
        {
            manualTodo.Add(new ManualTodoItem
            {
                Id = "mt-v04",
                Title = "修复失败的静态检查项",
                Detail = "按 checks 中 Failed 的条目修正生成物，然后重新运行 verify；不要带病安装。",
                Blocking = true,
            });
        }

        var report = new VerificationReport
        {
            Passed = linterResult.Passed && checks.All(c => c.Passed),
            Checks = checks,
            ManualTodo = manualTodo,
            Unverified = new List<string>
            {
                "未启动游戏：无法验证覆盖层被真实 Ren'Py 运行时加载（未真机启动=未验证）",
                "未在 NVDA/SAPI 环境实测：只能确认后端调用链存在，不能确认实际发声与延迟",
                "链式 say_menu_text_filter 与游戏自有 filter 的运行时顺序未实测",
                ".rpa 内文本与图片不在补丁范围：未解析归档，也未替换任何资源",
                "Ren'Py 6.99 与 8.x 的运行时差异（get_screen/_tts_all/键位）只按签名回退处理，未逐版本实测",
                "纯语音、存读档界面、CG 图片无替代文本等场景仍需人工确认",
            },
        };

        return Task.FromResult(ForgeResult<VerificationReport>.Success(report));
    }

    private static RenpyOverlayManifest? ReadManifest(string root)
    {
        var path = Path.Combine(root, RenpyOverlayCatalog.ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = RenpySupport.ReadTextOrNull(path);
            return string.IsNullOrWhiteSpace(text)
                ? null
                : RenpyManifestJson.Deserialize<RenpyOverlayManifest>(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
