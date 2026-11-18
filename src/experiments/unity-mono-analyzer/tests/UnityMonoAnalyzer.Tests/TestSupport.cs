using System.Text;
using UnityMonoAnalyzer;
using UnityMonoAnalyzer.Analysis;
using UnityMonoAnalyzer.Reporting;
using Xunit;

namespace UnityMonoAnalyzer.Tests;

/// <summary>临时目录：测试里造假的 Unity 目录结构，用完即删（只在系统临时目录写）。</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir(string label = "uma")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "uma-tests", label + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Dir(params string[] parts)
    {
        var p = System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());
        Directory.CreateDirectory(p);
        return p;
    }

    public string File(string relative, string content = "")
    {
        var p = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content, Encoding.UTF8);
        return p;
    }

    public string Bytes(string relative, byte[] content)
    {
        var p = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllBytes(p, content);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* 临时目录清理失败不影响结论 */ }
    }
}

/// <summary>本机真游戏路径（集成测试只读；缺失则 Skip）。</summary>
internal static class Games
{
    public const string Root = @"D:\Steam\steamapps\common";

    public const string SevenDaysRoot = Root + @"\我与你共度的七日";
    public const string SevenDaysGameDir = SevenDaysRoot + @"\game";

    public const string Needy = Root + @"\NEEDY GIRL OVERDOSE";
    public const string GalMaster = Root + @"\GalMaster";
    public const string ToTheMoonRoot = Root + @"\To the Moon";

    /// <summary>游戏不在本机 → 整个测试 Skip（而不是失败）。</summary>
    public static void Require(string path)
    {
        Skip.If(!Directory.Exists(path), $"本机没有安装该游戏：{path}");
    }

    public static void RequireFile(string path)
    {
        Skip.If(!System.IO.File.Exists(path), $"缺少文件：{path}");
    }
}

internal static class Fixtures
{
    /// <summary>一个"像对话写入点"的特征集：框架 + 语义命名 + 直接写入 + CJK + 动态来源 + 多调用者 = 90 分。</summary>
    public static CandidateFeatures DialogueWriter() => new()
    {
        TypeFullName = "GameProject.StoryWindow",
        TypeName = "StoryWindow",
        MethodName = "ShowDialogue",
        MethodFullName = "System.Void GameProject.StoryWindow::ShowDialogue(System.String)",
        ParameterCount = 1,
        HasStringParameter = true,
        OnKnownFrameworkType = true,
        FrameworkId = "naninovel",
        IsSemanticNamed = true,
        IsDialogueNamedType = true,
        TextSinkCalls = 1,
        SinkApis = new List<string> { "TMPro.TMP_Text::set_text" },
        DynamicSinkCalls = 1,
        ConstantSinkCalls = 0,
        CjkLiteralCount = 1,
        LiteralCount = 3,
        NonDialogueLiteralCount = 1,
        TextFieldCount = 2,
        CallerCount = 2,
        GameHasCjk = true,
    };

    public static DiscoveryCandidate Candidate(string dataDirName, QuickMetrics metrics, string exePath = "", int depth = 1)
    {
        return new DiscoveryCandidate
        {
            DataDir = System.IO.Path.Combine(@"C:\fake", dataDirName),
            DataDirName = dataDirName,
            GameDir = @"C:\fake",
            AssemblyPath = System.IO.Path.Combine(@"C:\fake", dataDirName, "Managed", "Assembly-CSharp.dll"),
            ExePath = exePath,
            Depth = depth,
            QuickMetrics = metrics,
        };
    }

    public static QuickMetrics Metrics(int sinks = 10, int frameworks = 0, int dialogueTypes = 0, int cjkMethods = 0, int launcherTypes = 0, int types = 100, int methods = 500)
    {
        return new QuickMetrics
        {
            Types = types,
            Methods = methods,
            TextSinkCalls = sinks,
            TextSinkCallerMethods = Math.Max(1, sinks / 2),
            CjkLiteralMethods = cjkMethods,
            KnownFrameworkTypes = frameworks,
            DialogueNamedTypes = dialogueTypes,
            LauncherSuspicionTypes = launcherTypes,
            Scanned = true,
        };
    }

    /// <summary>报告测试用的假结果：不碰磁盘、不调 Cecil。</summary>
    public static AnalysisResult Result()
    {
        var stats = new ScanStats
        {
            Types = 120,
            Methods = 800,
            MethodsWithBody = 700,
            LdstrTotal = 900,
            CjkLiteralMethods = 40,
            CjkGame = true,
            TextFieldTypes = 12,
            TextSinkCallSites = 30,
            TextSinkCallerMethods = 18,
            UpstreamCandidateMethods = 3,
            CandidatesScored = 21,
            CandidatesIncluded = 18,
            TierA = 3,
            TierB = 6,
            TierC = 9,
            BelowThreshold = 3,
        };

        var result = new AnalysisResult
        {
            Run = new RunInfo { StartedAtUtc = "2026-11-14T00:00:00Z", CommandLine = "test" },
            Game = new GameInfo
            {
                Name = "测试游戏",
                InputPath = @"C:\fake",
                GameDir = @"C:\fake",
                DataDir = @"C:\fake\Test_Data",
                DataDirName = "Test_Data",
                ExePath = @"C:\fake\Test.exe",
                ExeReason = "与数据目录 Test_Data 同名",
                Deployed = false,
            },
            Unity = new UnityInfo { Version = "2021.3.31f1", VersionSource = "globalgamemanagers" },
            Assembly = new AssemblyInfo { Path = @"C:\fake\Test_Data\Managed\Assembly-CSharp.dll", SizeBytes = 123456, Sha256 = "abc", RuntimeVersion = "v4.0.30319" },
            Stats = stats,
            Coverage = Coverage.Build(stats, cjkGame: true, upstreamCount: stats.UpstreamCandidateMethods),
        };
        result.Discovery.Candidates.Add(Candidate("Test_Data", Fixtures.Metrics(), @"C:\fake\Test.exe"));
        result.Discovery.Candidates[0].Selected = true;
        result.Discovery.SelectionMode = "single";

        result.TextSinks.Add(new TextSinkApi { Api = "TMPro.TMP_Text::set_text", Calls = 20, CallerMethods = 12, Role = "primary" });
        result.TextSinks.Add(new TextSinkApi { Api = "UnityEngine.UI.Text::set_text", Calls = 10, CallerMethods = 6, Role = "primary" });
        result.TextSinkHotspots.Add(new TextSinkHotspot { TypeFullName = "GameProject.StoryWindow", Calls = 12, Methods = 3, SampleMethod = "ShowDialogue" });

        result.FrameworkTraces.Add(new FrameworkTrace
        {
            Id = "naninovel",
            DisplayName = "Naninovel",
            Category = "known",
            TypeCount = 20,
            SampleTypes = new List<string> { "Naninovel.ScriptPlayer" },
            AffectsScoring = true,
        });

        result.Candidates.Add(new TextCandidate
        {
            Rank = 1,
            Kind = "dialogue",
            Score = 90,
            Tier = "A",
            Confidence = "high",
            MethodFullName = "System.Void GameProject.StoryWindow::ShowDialogue(System.String)",
            TypeFullName = "GameProject.StoryWindow",
            MethodName = "ShowDialogue",
            Signature = "System.Void ShowDialogue(System.String text)",
            DiscoveredBy = "direct-text-setter",
            TextSinkCalls = 12,
            TextSinkApis = new List<string> { "TMPro.TMP_Text::set_text" },
            CjkLiteralCount = 4,
            CallerCount = 2,
            Flags = new List<string> { "multi-write" },
            Evidence = new List<string> { "S1 命中 Naninovel" },
        });
        result.Candidates.Add(new TextCandidate
        {
            Rank = 2,
            Kind = "debug",
            Score = 35,
            Tier = "C",
            Confidence = "low",
            MethodFullName = "System.Void GameProject.DebugPanel::LogFps",
            TypeFullName = "GameProject.DebugPanel",
            MethodName = "LogFps",
            Signature = "System.Void LogFps()",
            DiscoveredBy = "direct-text-setter",
            TextSinkCalls = 1,
            Flags = new List<string> { "system-ui" },
        });

        result.Warnings.Add("未识别 Unity 版本");
        result.Notes.Add("已部署 BepInEx");
        return result;
    }
}
