using System.Security.Cryptography;
using System.Text;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Generators;

namespace GalA11yForge.Generators.Tests;

/// <summary>仓库内的固定位置：golden 快照、evidence 样例、Framework 源码。</summary>
internal static class TestEnv
{
    public const string GoldenUpdateVariable = "FORGE_UPDATE_GOLDEN";
    public const string EvidenceWriteVariable = "FORGE_WRITE_EVIDENCE";

    public static string RepoRoot { get; } = FindRepoRoot();

    public static string GoldenDir => Path.Combine(RepoRoot, "tests", "Forge.Generators.Tests", "golden");

    public static string EvidenceDir => Path.Combine(RepoRoot, "tests", "Forge.Generators.Tests", "evidence", "forge-sample");

    public static string FrameworkDir => Path.Combine(RepoRoot, "Framework");

    public static bool UpdateGolden => IsSet(GoldenUpdateVariable);

    public static bool WriteEvidence => IsSet(EvidenceWriteVariable);

    private static bool IsSet(string name)
        => string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal);

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var marker = Path.Combine(current.FullName, "src", "Forge.Generators", "Forge.Generators.csproj");
            if (File.Exists(marker))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录（src/Forge.Generators/Forge.Generators.csproj）；请从仓库内运行 dotnet test。");
    }
}

/// <summary>
/// 固定样例：两条 Hook + 一条非 Hook + 一条人工待办 + 一条风险。
/// 目标全部是虚构的测试命名空间（GameNS），与任何真实游戏无关。
/// </summary>
internal static class SamplePlans
{
    /// <summary>固定游戏路径：让 golden / evidence 不含本机临时路径，换机器也能逐字节比较。</summary>
    public const string GamePath = @"D:\Games\GalgameSample";

    public static PatchPlan Create(string slug = "forge-golden") => new()
    {
        GameSlug = slug,
        Route = PlanRoute.SemiAutomatic,
        Confidence = 0.72,
        Target = new PlanTarget
        {
            GamePath = GamePath,
            Engine = "unity_mono",
            EngineVersion = "2021.3.45f1",
            Bitness = 64,
        },
        PatchPoints = new List<PatchPoint>
        {
            new()
            {
                Id = "dialogue-set-text",
                Kind = PatchPointKind.Hook,
                Target = "GameNS.ADV.Dialogue:SetText",
                CandidateId = "candidate-1",
                Confidence = 0.86,
                Description = "台词 setter（这条描述只存在于 plan，不应进入生成物）。完整签名（Cecil）：[Assembly-CSharp.dll] System.Void GameNS.ADV.Dialogue::SetText(System.String text)。",
            },
            new()
            {
                Id = "advance-on-click",
                Kind = PatchPointKind.Hook,
                Target = "GameNS.ADV.Advance:OnClick(string)",
                CandidateId = "candidate-2",
                Confidence = 0.61,
            },
            new()
            {
                Id = "config-volume",
                Kind = PatchPointKind.Config,
                Target = "config/volume.json",
                Confidence = 0.4,
                Description = "配置补丁点：本生成器不实现",
            },
        },
        ManualTodo = new List<ManualTodoItem>
        {
            new() { Id = "todo-wire-speech", Title = "把 SetText 的参数接到 Repeat.Say", Blocking = true },
        },
        Risks = new List<RiskItem>
        {
            new() { Id = "risk-version", Severity = RiskSeverity.Medium, Description = "游戏更新可能改方法名" },
        },
    };
}

public abstract class GeneratorTestBase : IDisposable
{
    protected GeneratorTestBase()
    {
        Root = Path.Combine(Path.GetTempPath(), "forge-gen-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    protected string Root { get; }

    protected string Workspace => Path.Combine(Root, "workspace");

    protected string GameDir => Path.Combine(Root, "games", "GalgameSample");

    protected string OutDir => Path.Combine(Workspace, "work", "job-1", "project");

    protected GameContext Context(string slug = "forge-golden", bool dryRun = false)
    {
        var context = GameContext.Create(GameDir, Workspace, slug);
        context.DryRun = dryRun;
        return context;
    }

    protected PatchPlan Plan(params PatchPoint[] points) => PlanWith(GameDir, points);

    protected static PatchPlan PlanWith(string gamePath, params PatchPoint[] points) => new()
    {
        GameSlug = "forge-golden",
        Route = PlanRoute.SemiAutomatic,
        Confidence = 0.7,
        Target = new PlanTarget { GamePath = gamePath, Engine = "unity_mono", EngineVersion = "2021.3.45f1", Bitness = 64 },
        PatchPoints = points.ToList(),
        ManualTodo = new List<ManualTodoItem>
        {
            new() { Id = "todo-1", Title = "接线朗读", Detail = "人工把钩子接入 Repeat.Say", Blocking = true },
        },
        Risks = new List<RiskItem>
        {
            new() { Id = "risk-1", Severity = RiskSeverity.Medium, Description = "游戏更新后目标可能改名" },
        },
    };

    protected static PatchPoint Hook(
        string id,
        string target,
        string? candidate = null,
        string description = "分析说明：只存在于 plan，不应进入生成物。",
        double confidence = 0.8)
        => new()
        {
            Id = id,
            Kind = PatchPointKind.Hook,
            Target = target,
            CandidateId = candidate,
            Confidence = confidence,
            Description = description,
        };

    protected static PatchPoint NonHook(string id, PatchPointKind kind = PatchPointKind.Config)
        => new() { Id = id, Kind = kind, Target = "config/volume.json", Confidence = 0.3, Description = "非 Hook 补丁点" };

    /// <summary>默认用最小 stub 语音桥，测试不依赖仓库 Framework 的当前内容。</summary>
    protected static UnityMonoGenerator CreateGenerator(IVoiceBridgeSource? source = null, GeneratorOptions? options = null)
        => new(source ?? StubVoiceBridgeSource.Default(), options);

    protected static UnityMonoGenerator CreateFrameworkGenerator()
        => new(null, new GeneratorOptions { FrameworkDirectory = TestEnv.FrameworkDir });

    protected static void AssertError(ForgeResult<GenerationResult> result, string expectedCode, string? messageContains = null)
    {
        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Equal(expectedCode, result.Error!.Code);
        if (messageContains is not null)
        {
            Assert.Contains(messageContains, result.Error.Message);
        }
    }

    protected static SortedDictionary<string, byte[]> ReadTree(string root)
    {
        var map = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            map[relative] = File.ReadAllBytes(file);
        }

        return map;
    }

    protected static void AssertTreesEqual(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual,
        string label)
    {
        var missing = expected.Keys.Except(actual.Keys, StringComparer.Ordinal).ToList();
        var extra = actual.Keys.Except(expected.Keys, StringComparer.Ordinal).ToList();
        Assert.True(
            missing.Count == 0 && extra.Count == 0,
            $"{label} 文件集合不一致。缺少：{string.Join(", ", missing)}；多出：{string.Join(", ", extra)}");

        foreach (var pair in expected)
        {
            Assert.True(
                pair.Value.AsSpan().SequenceEqual(actual[pair.Key]),
                $"{label} 文件内容不一致：{pair.Key}");
        }
    }

    protected static void CopyTree(IReadOnlyDictionary<string, byte[]> tree, string destination)
    {
        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        foreach (var pair in tree)
        {
            var path = Path.Combine(destination, pair.Key.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(path, pair.Value);
        }
    }

    protected static string ReadText(string path) => File.ReadAllText(path);

    protected static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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
            // 清理失败不影响测试结论。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }

        GC.SuppressFinalize(this);
    }
}

internal sealed class StubVoiceBridgeSource : IVoiceBridgeSource
{
    private readonly List<VoiceBridgeFile> _files;

    public StubVoiceBridgeSource(params VoiceBridgeFile[] files) => _files = files.ToList();

    public string Id => "stub";

    public IReadOnlyList<VoiceBridgeFile> ReadFiles() => _files;

    public static StubVoiceBridgeSource Default() => new(
        new VoiceBridgeFile("Platform/Speech.cs", StubFiles.Speech, false, "stub/platform/Speech.cs", StubFiles.Sha(StubFiles.Speech)),
        new VoiceBridgeFile("Platform/A11yHost.cs", StubFiles.Host, true, "stub/contract/A11yHost.cs", StubFiles.Sha(StubFiles.Host)));
}

internal static class StubFiles
{
    public const string Speech = """
// 最小语音桥样本（测试用，不是 Framework 原文）
namespace A11yFramework
{
    internal static class Speech
    {
        internal static void Speak(string text, bool interrupt) { }
    }
}
""";

    public const string Host = """
// 最小契约层样本（测试用，不是 Framework 原文）
namespace A11yFramework
{
    internal static class A11yHost
    {
    }
}
""";

    public static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
