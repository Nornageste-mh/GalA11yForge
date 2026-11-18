using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Adapters.Il2Cpp.Internal;

namespace GalA11yForge.Adapters.Il2Cpp.Tests;

/// <summary>
/// 门面与门禁测试：识别、路径安全、诚实降级。
/// 这些是“不许假装成功”的护栏，任何一条退化成静默通过都会让生成器去写游戏目录。
/// </summary>
public sealed class AdapterAndBridgeTests : IDisposable
{
    private readonly string _root;
    private readonly string _game;
    private readonly string _workspace;
    private readonly Il2CppAdapter _adapter = new();

    public AdapterAndBridgeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gala11y-adapter-" + Guid.NewGuid().ToString("N"));
        _game = Path.Combine(_root, "SiliconHeart");
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(Path.Combine(_game, "SiliconHeart_Data", "il2cpp_data", "Metadata"));
        Directory.CreateDirectory(_workspace);
        File.WriteAllBytes(Path.Combine(_game, "GameAssembly.dll"), [0x4D, 0x5A, 0x00, 0x00]);
        WriteMetadata(Path.Combine(_game, "SiliconHeart_Data", "il2cpp_data", "Metadata", "global-metadata.dat"));
    }

    private static void WriteMetadata(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(0xFAB11BAF);
        writer.Write(29);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    [Fact]
    public async Task Detect_OnIl2CppDirectory_ReturnsUnityIl2CppFamily()
    {
        var context = GameContext.Create(_game, _workspace);

        var result = await _adapter.DetectAsync(context);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal("unity_il2cpp", result.Value!.Family);
        Assert.True(result.Value.Confidence >= 0.85);
        Assert.Contains(result.Value.Evidence, e => e.Contains("GameAssembly"));
    }

    [Fact]
    public async Task Detect_OnNonIl2CppDirectory_FailsWithUnsupported()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        var context = GameContext.Create(empty, _workspace);

        var result = await _adapter.DetectAsync(context);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.EngineUnsupported, result.Error!.Code);
    }

    [Fact]
    public async Task Detect_WithNullContext_IsArgumentInvalid()
    {
        var result = await _adapter.DetectAsync(null!);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.ArgumentInvalid, result.Error!.Code);
    }

    [Fact]
    public async Task Analyze_WithoutInteropOrDump_TriesAutoDumpThenFailsHonestly()
    {
        var context = GameContext.Create(_game, _workspace);

        var result = await _adapter.AnalyzeAsync(context, Detection());

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.DependencyMissing, result.Error!.Code);
        // 默认 autoDump=true：本机测试环境没有 Il2CppDumper，错误必须是“自动 dump 也没成功”，
        // 而不是假装分析完了。
        Assert.Contains("自动 dump", result.Error.Message);
        Assert.Contains("Il2CppDumper", result.Error.Suggestion ?? string.Empty);
        Assert.NotNull(result.Error.Details);
        Assert.Equal("attempted", result.Error.Details!["autoDump"]);
        Assert.Contains("Il2CppDumper", result.Error.Details["dumpError"]);
    }

    [Fact]
    public async Task Analyze_WithAutoDumpDisabled_KeepsInteropAndDumpInstructions()
    {
        var context = GameContext.Create(_game, _workspace);
        var profile = new ProfileDocument();
        profile.Options["il2cpp.autoDump"] = System.Text.Json.JsonSerializer.SerializeToElement(false);
        context.Profile = profile;

        var result = await _adapter.AnalyzeAsync(context, Detection());

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.DependencyMissing, result.Error!.Code);
        Assert.Contains("BepInEx", result.Error.Suggestion ?? string.Empty);
        Assert.DoesNotContain("自动 dump", result.Error.Message);
    }

    [Fact]
    public async Task Analyze_WithDumpFile_ProducesCandidatesAndUnverifiedNotes()
    {
        var dump = Path.Combine(_root, "dump.cs");
        File.WriteAllText(dump, """
            // Image 1: Elringus.Naninovel.Runtime.dll - 1
            // Namespace: Naninovel
            public class UITextPrinter
            {
            	// Methods
            	// RVA: 0x221540 Offset: 0x221540 VA: 0x180221540 Slot: 76
            	public void set_Text(string value) { }
            }
            """);
        var context = GameContext.Create(_game, _workspace);
        var profile = new ProfileDocument();
        profile.Options["il2cpp.dumpPath"] = System.Text.Json.JsonSerializer.SerializeToElement(dump);
        context.Profile = profile;

        var result = await _adapter.AnalyzeAsync(context, Detection());

        Assert.True(result.Ok, result.Error?.Message);
        Assert.NotEmpty(result.Value!.Candidates);
        Assert.NotEmpty(result.Value.Unverified);
        Assert.Contains(result.Value.Coverage.Covered, c => c.Contains("dump.cs"));
    }

    [Fact]
    public async Task Generate_OutputInsideGameDirectory_IsDenied()
    {
        var context = GameContext.Create(_game, _workspace);
        var plan = EmptyPlan();
        var output = Path.Combine(_game, "BepInEx", "plugins", "patched");

        var result = await _adapter.GenerateAsync(context, plan, output);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.GameWriteDenied, result.Error!.Code);
    }

    [Fact]
    public async Task Generate_OutputOutsideWorkspace_IsDenied()
    {
        var context = GameContext.Create(_game, _workspace);
        var outside = Path.Combine(_root, "elsewhere", "out");

        var result = await _adapter.GenerateAsync(context, EmptyPlan(), outside);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.PathUnsafe, result.Error!.Code);
    }

    [Fact]
    public async Task Generate_WithWorkspaceItselfAsOutput_IsDenied()
    {
        var context = GameContext.Create(_game, _workspace);

        var result = await _adapter.GenerateAsync(context, EmptyPlan(), _workspace);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.PathUnsafe, result.Error!.Code);
    }

    [Fact]
    public async Task Generate_PlanWithoutUsableHooks_DowngradesToPartial()
    {
        // 只有 dump.cs、没有 interop：能写出骨架，但不能宣称“补丁已生成”。
        var dump = Path.Combine(_root, "dump.cs");
        File.WriteAllText(dump, "// Image 1: Game.dll - 1\n");
        var profile = new ProfileDocument();
        profile.Options["il2cpp.dumpPath"] = System.Text.Json.JsonSerializer.SerializeToElement(dump);
        var context = GameContext.Create(_game, _workspace);
        context.Profile = profile;

        var result = await _adapter.GenerateAsync(context, EmptyPlan(), Path.Combine(_workspace, "skeleton"));

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.Partial, result.Error!.Code);
        Assert.True(Directory.Exists(Path.Combine(_workspace, "skeleton")));
        Assert.Contains("不会朗读", result.Error.Message);
    }

    [Fact]
    public async Task Generate_RelativeOutput_ResolvesUnderWorkspace()
    {
        var context = GameContext.Create(_game, _workspace);

        var result = await _adapter.GenerateAsync(context, EmptyPlan(), "out/relative");

        Assert.True(Directory.Exists(Path.Combine(_workspace, "out", "relative")));
        Assert.Equal(ForgeErrorCodes.Partial, result.Error?.Code);
    }

    [Fact]
    public void AutoDumper_ReusesExistingFreshDump_WithoutDumperExecutable()
    {
        // 0.2.17：同一工作区第二次 analyze/plan 不该重新 dump。
        // 这里刻意不放 Il2CppDumper，只要已有新鲜 dump.cs 就应当成功复用。
        var context = GameContext.Create(_game, _workspace, "siliconheart");
        var options = Il2CppOptions.From(context);
        var layout = Il2CppLayoutProbe.Probe(_game, options.AsDictionary());
        var dumpDirectory = Path.Combine(_workspace, "games", "siliconheart", "il2cpp-dump");
        Directory.CreateDirectory(dumpDirectory);
        var dumpPath = Path.Combine(dumpDirectory, "dump.cs");
        File.WriteAllText(dumpPath, new string('x', 150 * 1024));

        var attempt = Il2CppAutoDumper.Produce(context, options, layout, CancellationToken.None);

        Assert.True(attempt.Attempted);
        Assert.Null(attempt.Error);
        Assert.Equal(dumpPath, attempt.DumpPath);
        Assert.Contains("复用", attempt.Note);
    }

    private static EngineDetection Detection() => new()
    {
        Family = "unity_il2cpp",
        Version = "2021.3.15f1",
        Bitness = 64,
        Confidence = 0.95,
        Evidence = ["GameAssembly.dll"],
    };

    private static PatchPlan EmptyPlan() => new()
    {
        GameSlug = "sh",
        Route = PlanRoute.SemiAutomatic,
        Target = new PlanTarget { Engine = "unity_il2cpp", GamePath = "x" },
        Confidence = 0,
    };
}

