using GalA11yForge.Cli;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Serialization;

namespace GalA11yForge.Core.Tests;

/// <summary>
/// 没有内置适配器的引擎（例如 KiriKiri）也要能导出分析报告：
/// 复用 analyze --advanced 留下的 01-analyze/analysis.json，方案用“不支持自动生成”的诚实骨架。
/// </summary>
public sealed class AdapterlessReportTests
{
    [Fact]
    public async Task Report_WithCachedAnalysis_ForAdapterlessEngine_WritesHonestUnsupportedReport()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-adapterless-" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(root, "Game");
        var workspace = Path.Combine(root, "ws");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(workspace);
        File.WriteAllBytes(Path.Combine(game, "data.xp3"), new byte[64]);
        File.WriteAllBytes(Path.Combine(game, "game.exe"), new byte[64]);

        var analysis = new AnalysisReport
        {
            GamePath = game,
            Detection = new EngineDetection { Family = "kirikiriz", Confidence = 0.9, Bitness = 32 },
        };
        analysis.Coverage.Covered.Add("Ghidra 静态分析：318 个函数、0 个文本 API 候选");
        analysis.Coverage.NotCovered.Add("引擎没有内置分析适配器，未执行适配器分析。");
        analysis.Coverage.ManualTodo.Add("在 x64dbg/Textractor 中定位文本函数后人工接钩子。");

        var analyzeDirectory = Path.Combine(workspace, "games", "game", "01-analyze");
        Directory.CreateDirectory(analyzeDirectory);
        File.WriteAllText(
            Path.Combine(analyzeDirectory, "analysis.json"),
            ForgeJson.Serialize(analysis));

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            var exitCode = await CliApplication.RunAsync(
                ["report", "--game", game, "--workspace", workspace, "--json"], stdout, stderr);

            Assert.Equal(0, exitCode);
            var output = stdout.ToString();
            Assert.Contains("\"ok\":true", output);
            Assert.Contains("Unsupported", output);
            Assert.Contains("humanTodoCount", output);
            Assert.Contains("blockingTodos", output);
            Assert.Contains("suggestedTodos", output);
            Assert.Contains("M-A01", output);

            var reportDirectory = Path.Combine(workspace, "reports", "game");
            Assert.True(File.Exists(Path.Combine(reportDirectory, "report.md")));
            var todos = File.ReadAllText(Path.Combine(reportDirectory, "human-todos.md"));
            Assert.Contains("阻断", todos);
            Assert.Contains("没有内置适配器", todos);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
