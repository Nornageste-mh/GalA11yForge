using UnityMonoAnalyzer;
using UnityMonoAnalyzer.Analysis;
using Xunit;

namespace UnityMonoAnalyzer.Tests;

/// <summary>
/// 对本机 4 款未装补丁的 Unity Mono 游戏跑真实分析（只读，不写游戏目录）。
/// 数字全部来自 2026-11-14 的实测（见 evidence\4games-summary.csv）。
/// </summary>
public class IntegrationTests
{
    [SkippableFact]
    public void SevenDays_FindsNodeCanvasAndRealTextPipeline()
    {
        Games.Require(Games.SevenDaysRoot);

        var r = Analyzer.Run(new AnalyzeRequest { GameInput = Games.SevenDaysRoot });

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("2021.3.31f1", r.Unity.Version);
        Assert.Equal(325, r.Stats.Types);
        Assert.Equal(1602, r.Stats.Methods);
        // 启动器 37 处 > 真游戏 26 处：选错就会把启动器当目标
        Assert.Equal(26, r.Stats.TextSinkCallSites);
        Assert.EndsWith("The seven days i spent with you.exe", r.Game.ExePath);
        Assert.Equal(3, r.Stats.TierA);

        Assert.Contains(r.FrameworkTraces, f => f.Id == "nodecanvas.dialoguetrees" && f.TypeCount == 20);

        var top = r.Candidates[0];
        Assert.Contains("OnMultipleChoiceRequest", top.MethodFullName);
        Assert.True(top.OnKnownFrameworkType);
        Assert.Equal("known", top.FrameworkCategory);
        Assert.Contains("sample-code", top.Flags);   // NodeCanvas 自带示例代码，不能直接当目标

        Assert.Contains(r.Candidates, c => c.MethodFullName.Contains("SpeecController::GetStringLine"));
        Assert.True(r.Game.Deployed, "game\\ 子目录已装 BepInEx，应只记录为环境事实");
        Assert.Contains(r.Notes, n => n.Contains("BepInEx") || n.Contains("补丁"));
    }

    [SkippableFact]
    public void Needy_TextComesFromCellsAndTooltips_NotWindowClasses()
    {
        Games.Require(Games.Needy);

        var r = Analyzer.Run(new AnalyzeRequest { GameInput = Games.Needy });

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("2022.3.22f1", r.Unity.Version);
        Assert.Equal(2508, r.Stats.Types);
        Assert.Equal(10962, r.Stats.Methods);
        Assert.Equal(562, r.Stats.TextSinkCallSites);

        Assert.Equal(402, r.TextSinks.Single(s => s.Api == "TMPro.TMP_Text::set_text").Calls);
        Assert.Equal(160, r.TextSinks.Single(s => s.Api == "UnityEngine.UI.Text::set_text").Calls);
        Assert.EndsWith("Windose.exe", r.Game.ExePath);

        // 热点：真正的文本写入集中在 cell/tooltip 组件上
        Assert.Contains(r.TextSinkHotspots, h => h.TypeFullName == "ngov3.StatusTooltip" && h.Calls == 49);
        Assert.Contains(r.TextSinkHotspots, h => h.TypeFullName == "ngov3.PoketterCell");
        Assert.Contains(r.TextSinkHotspots, h => h.TypeFullName.StartsWith("ngov3.JineCell"));

        // ngov3.Window 系只是容器，静态看没有 A 级文本写入点
        Assert.DoesNotContain(r.Candidates, c => c.Tier == "A" && c.TypeFullName.StartsWith("ngov3.Window"));
    }

    [SkippableFact]
    public void GalMaster_TopCandidateIsDialogueHandle()
    {
        Games.Require(Games.GalMaster);

        var r = Analyzer.Run(new AnalyzeRequest { GameInput = Games.GalMaster });

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("2022.3.62f3", r.Unity.Version);
        Assert.Equal(235, r.Stats.Types);
        Assert.Equal(2108, r.Stats.Methods);
        Assert.Equal(31, r.Stats.TextSinkCallSites);

        var top = r.Candidates[0];
        Assert.Contains("DialogueHandle::ShowDialogue", top.MethodFullName);
        Assert.Equal(105, top.Score);
        Assert.Equal("A", top.Tier);
        Assert.Equal("dialogue", top.Kind);
        Assert.Contains(r.Candidates, c => c.MethodFullName.Contains("SelectionAction::Open"));
    }

    [SkippableFact]
    public void ToTheMoonRoot_PicksMainGameAmongThreeNestedUnityGames()
    {
        Games.Require(Games.ToTheMoonRoot);

        var r = Analyzer.Run(new AnalyzeRequest { GameInput = Games.ToTheMoonRoot });

        Assert.Equal(0, r.ExitCode);
        Assert.Equal("To the Moon_Data", r.Game.DataDirName);
        Assert.Equal("2022.3.28f1", r.Unity.Version);
        Assert.Equal(1318, r.Stats.Types);
        Assert.Equal(7477, r.Stats.Methods);
        Assert.Equal(245, r.Stats.TextSinkCallSites);

        Assert.Contains(r.FrameworkTraces, f => f.Id == "scenarioforge" && f.TypeCount == 446);
        Assert.Contains(r.Candidates, c => c.MethodFullName.Contains("ScenarioForge.TextTyper::SetContents"));
    }

    [SkippableFact]
    public void NonUnityEngine_SiblingDirectories_AreRejectedWithExit4()
    {
        Games.Require(Games.ToTheMoonRoot);
        var mkxp = Path.Combine(Games.ToTheMoonRoot, "To the Moon");
        Games.Require(mkxp);

        var ex = Assert.Throws<AnalyzeException>(() => Analyzer.Run(new AnalyzeRequest { GameInput = mkxp }));

        Assert.Equal(4, ex.ExitCode);
        Assert.Contains("不是 Unity Mono", ex.Message);
    }

    [Fact]
    public void MissingPath_IsAUsageError()
    {
        var ex = Assert.Throws<AnalyzeException>(() =>
            Analyzer.Run(new AnalyzeRequest { GameInput = Path.Combine(Path.GetTempPath(), "uma-definitely-missing-" + Guid.NewGuid().ToString("N")) }));

        Assert.Equal(2, ex.ExitCode);
    }

    [SkippableFact]
    public void TwoIdenticalCandidates_AreReportedAsAmbiguous_PartialSuccess()
    {
        Games.RequireFile(Path.Combine(Games.GalMaster, "GAL PRO MASTER_Data", "Managed", "Assembly-CSharp.dll"));

        using var tmp = new TempDir("ambiguous");
        var dll = Path.Combine(Games.GalMaster, "GAL PRO MASTER_Data", "Managed", "Assembly-CSharp.dll");
        tmp.Dir("A_Data", "Managed");
        tmp.Dir("B_Data", "Managed");
        File.Copy(dll, Path.Combine(tmp.Path, "A_Data", "Managed", "Assembly-CSharp.dll"), overwrite: true);
        File.Copy(dll, Path.Combine(tmp.Path, "B_Data", "Managed", "Assembly-CSharp.dll"), overwrite: true);

        var r = Analyzer.Run(new AnalyzeRequest { GameInput = tmp.Path });

        Assert.Equal(5, r.ExitCode);
        Assert.Contains(r.Warnings, w => w.Contains("并列"));
        Assert.Equal(2, r.Discovery.Candidates.Count);
    }
}
