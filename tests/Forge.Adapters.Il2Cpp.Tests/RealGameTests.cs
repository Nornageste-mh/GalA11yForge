using GalA11yForge.Adapters.Il2Cpp.Internal;

namespace GalA11yForge.Adapters.Il2Cpp.Tests;

/// <summary>
/// 本机真实原料集成测试（R18 遗留的 SiliconHeart IL2CPP 工程）。
/// 机器上没有 <c>D:\_re</c> 时整组自动跳过，CI 上不会红。
/// </summary>
public sealed class RealGameTests
{
    private const string GameCopy = @"D:\_re\sh";
    private const string RawDump = @"D:\_re\tools\il2cppdumper\dump.cs";

    [Fact]
    public void InteropScan_RealSiliconHeart_FindsNaninovelTextPipeline()
    {
        var interop = Path.Combine(GameCopy, "BepInEx", "interop");
        if (!Directory.Exists(interop))
        {
            return;
        }

        var layout = new Il2CppLayout
        {
            GameDirectory = GameCopy,
            GameAssemblyPath = Path.Combine(GameCopy, "GameAssembly.dll"),
            InteropDirectory = interop,
            InteropAssemblyCount = Directory.GetFiles(interop, "*.dll").Length,
        };

        var scan = new Il2CppInteropScanner().Scan(layout, CancellationToken.None);

        Assert.Equal(Il2CppSourceKind.Interop, scan.Source);
        Assert.True(scan.TypeCount > 1000);
        Assert.True(scan.MethodCount > 10000);
        Assert.True(scan.Types.ContainsKey("Naninovel.TextPrinterManager"));
        Assert.True(scan.Types["Naninovel.ITextPrinterManager"].IsInterface);
        Assert.Contains(scan.Methods, m => m.TypeFullName == "Naninovel.TextPrinterManager" && m.Name == "PrintTextAsync");
        Assert.Contains(scan.Methods, m => m.Name == "set_Text" && m.TypeFullName == "Naninovel.UI.RevealableUIText");
    }

    [Fact]
    public void Analyze_RealInteropScan_RanksSafeTextSinksFirst()
    {
        var interop = Path.Combine(GameCopy, "BepInEx", "interop");
        if (!Directory.Exists(interop))
        {
            return;
        }

        var game = GameCopy;
        var workspace = Path.Combine(Path.GetTempPath(), "gala11y-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var context = Facts.Context(game, workspace);
            var layout = new Il2CppLayout
            {
                GameDirectory = game,
                GameAssemblyPath = Path.Combine(game, "GameAssembly.dll"),
                InteropDirectory = interop,
                InteropAssemblyCount = Directory.GetFiles(interop, "*.dll").Length,
            };
            var scan = new Il2CppInteropScanner().Scan(layout, CancellationToken.None);
            var detection = new GalA11yForge.Core.Models.EngineDetection
            {
                Family = "unity_il2cpp",
                Version = "2021.3.15f1",
                Bitness = 64,
                Confidence = 0.95,
            };

            var report = new Il2CppAnalyzer().Analyze(
                context,
                detection,
                Il2CppOptions.From(context),
                scan,
                null,
                CancellationToken.None);

            Assert.NotEmpty(report.Candidates);
            // R21：async 的 PrintTextAsync 被压到 60 分以下，第一名应是安全的 set_Text 出口。
            Assert.Contains("set_Text", report.Candidates[0].Source, StringComparison.Ordinal);
            Assert.True(report.Candidates[0].Score >= 95);
            // 真实游戏候选有数量上限，async 方法可能不在前 N 里；只要出现，就绝不能是高分自动候选。
            Assert.DoesNotContain(
                report.Candidates,
                c => c.Source.Contains("PrintTextAsync", StringComparison.Ordinal) && c.Score > 60);
            Assert.DoesNotContain(report.Candidates, c => c.Source.Contains("ITextPrinterManager", StringComparison.Ordinal));
            Assert.Contains(report.Unverified, u => u.Contains("运行时"));
        }
        finally
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
                // 清理失败忽略。
            }
        }
    }

    [Fact]
    public void DumpParse_RealIl2CppDumperOutput_CompletesUnderTimeout()
    {
        if (!File.Exists(RawDump))
        {
            return;
        }

        var layout = new Il2CppLayout { GameDirectory = GameCopy, DumpPath = RawDump };
        var outcome = new Il2CppDumpParser().Parse(layout, CancellationToken.None);

        Assert.True(outcome.TypeCount > 5000);
        Assert.True(outcome.MethodCount > 50000);
        Assert.True(outcome.Types.ContainsKey("Naninovel.TextPrinterManager"));
        Assert.Contains(outcome.Methods, m => m.Name == "PrintTextAsync" && m.TypeFullName == "Naninovel.TextPrinterManager");
    }
}
