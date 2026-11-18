using System.Text;
using System.Text.Json;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Serialization;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Core.Tests;

internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "gaforge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Sub(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public string File(string name, string content)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class TestData
{
    public static ProfileDocument Profile() => new()
    {
        Slug = "sample-game",
        GamePath = "D:\\Games\\Sample",
        Engine = "unity_mono",
        MinConfidence = 0.5,
        Advanced = false,
        UniversalCapture = false,
        Voice = "sapi",
        Keymap = new Dictionary<string, string> { ["advance"] = "Space", ["menu"] = "Tab" },
        Options = new Dictionary<string, JsonElement>
        {
            ["plugin"] = JsonSerializer.SerializeToElement(new { mode = "safe", retries = 3 }),
        },
        LastResult = "detected",
    };

    public static AnalysisReport Analysis() => new()
    {
        GamePath = "D:\\Games\\Sample",
        Detection = new EngineDetection
        {
            Family = "unity_mono",
            Version = "2021.3.16f1",
            Bitness = 64,
            Confidence = 0.92,
            Evidence = ["file:Sample_Data/Managed/Assembly-CSharp.dll", "file:Sample_Data/globalgamemanagers"],
        },
        Candidates =
        [
            new TextCandidate
            {
                Id = "tc-001",
                Kind = TextCandidateKind.Dialogue,
                Source = "assembly:Assembly-CSharp.dll!/DialogueManager::ShowText",
                Score = 85,
                Evidence = ["method signature (string, string)"],
            },
            new TextCandidate
            {
                Id = "tc-002",
                Kind = TextCandidateKind.Choice,
                Source = "assembly:Assembly-CSharp.dll!/ChoiceManager::Show",
                Score = 60,
                Evidence = ["low confidence: name match only"],
            },
        ],
        Coverage = new CoverageReport
        {
            Covered = ["Mono 运行时识别", "对话文本候选"],
            NotCovered = ["选择支 UI", "存读档菜单"],
            ManualTodo = ["确认 ChoiceManager 是否被实际调用"],
        },
        Risks =
        [
            new RiskItem
            {
                Id = "r-001",
                Severity = RiskSeverity.High,
                Description = "游戏目录写入可能触发完整性校验",
                Mitigation = "部署前备份并按账本回滚",
            },
        ],
        Unverified = ["未在真机运行验证朗读顺序"],
    };

    public static PatchPlan Plan() => new()
    {
        GameSlug = "sample-game",
        Route = PlanRoute.SemiAutomatic,
        Target = new PlanTarget
        {
            GamePath = "D:\\Games\\Sample",
            Engine = "unity_mono",
            EngineVersion = "2021.3.16f1",
            Bitness = 64,
        },
        Confidence = 0.7,
        PatchPoints =
        [
            new PatchPoint
            {
                Id = "pp-001",
                Kind = PatchPointKind.Hook,
                Target = "DialogueManager::ShowText",
                CandidateId = "tc-001",
                Confidence = 0.8,
                Description = "在对话显示前捕获文本",
            },
        ],
        Artifacts =
        [
            new GeneratedArtifact
            {
                Name = "A11yPlugin",
                Kind = ArtifactKind.Project,
                RelativePath = "src/A11yPlugin/A11yPlugin.csproj",
                Description = "BepInEx 插件工程",
            },
        ],
        Risks =
        [
            new RiskItem
            {
                Id = "r-001",
                Severity = RiskSeverity.High,
                Description = "游戏目录写入可能触发完整性校验",
            },
        ],
        ManualTodo =
        [
            new ManualTodoItem
            {
                Id = "mt-001",
                Title = "确认选择支钩子",
                Detail = "需要人工在游戏中触发一次选择支",
                Blocking = true,
            },
        ],
    };

    public static ReportDocument Report()
    {
        var analysis = Analysis();
        var plan = Plan();
        var report = ReportDocument.FromAnalysis(analysis, plan);
        report.GameSlug = "sample-game";
        report.GeneratedAt = "2026-11-14T08:00:00.0000000+00:00";
        return report;
    }

    public static DeployManifest DeployManifest(string backupRoot) => new()
    {
        DeploymentId = "dep-test-001",
        GamePath = "D:\\Games\\Sample",
        BackupRoot = backupRoot,
        CreatedAt = "2026-11-14T08:00:00Z",
        Files =
        [
            new DeployFileEntry
            {
                RelativePath = "BepInEx/plugins/A11yPlugin.dll",
                Action = DeployFileAction.Add,
                ExistedBefore = false,
                Sha256After = new string('a', 64),
            },
            new DeployFileEntry
            {
                RelativePath = "doorstop_config.ini",
                Action = DeployFileAction.Replace,
                ExistedBefore = true,
                BackupPath = Path.Combine(backupRoot, "doorstop_config.ini"),
                Sha256Before = new string('b', 64),
                Sha256After = new string('c', 64),
            },
        ],
    };
}
