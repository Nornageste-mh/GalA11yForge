using GalA11yForge.Adapters.UnityMono.Internal;
using GalA11yForge.Core.Abstractions;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Generators;

namespace GalA11yForge.Adapters.UnityMono.Tests;

public sealed class GenerateTests
{
    [Fact]
    public async Task Bridge_RelativeOutput_ResolvesUnderWorkspace_AndDelegates()
    {
        using var bed = new TestBed();
        var recorder = new RecordingGenerator();
        var bridge = new UnityMonoGenerationBridge(recorder);

        var result = await bridge.GenerateAsync(
            bed.Context(),
            SimplePlan(bed),
            Path.Combine("games", "fake-game", "05-generate"));

        Assert.True(result.Ok, result.Error?.ToString());
        Assert.Equal(1, recorder.Calls);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(bed.Workspace, "games", "fake-game", "05-generate")),
            recorder.LastOutput);
        Assert.Equal(bed.GameDirectory, recorder.LastContext!.GameDirectory);
    }

    [Fact]
    public async Task Bridge_RelativeEscape_IsRejectedBeforeGenerator()
    {
        using var bed = new TestBed();
        var recorder = new RecordingGenerator();
        var bridge = new UnityMonoGenerationBridge(recorder);

        var result = await bridge.GenerateAsync(bed.Context(), SimplePlan(bed), Path.Combine("..", "escape"));

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.PathUnsafe, result.Error!.Code);
        Assert.Equal(0, recorder.Calls);
    }

    [Fact]
    public async Task Bridge_GameDirectory_IsRejected()
    {
        using var bed = new TestBed();
        var recorder = new RecordingGenerator();
        var bridge = new UnityMonoGenerationBridge(recorder);

        var result = await bridge.GenerateAsync(
            bed.Context(),
            SimplePlan(bed),
            Path.Combine(bed.GameDirectory, "patch-output"));

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.GameWriteDenied, result.Error!.Code);
        Assert.Equal(0, recorder.Calls);
    }

    [Fact]
    public async Task Bridge_WorkspaceRoot_IsRejected()
    {
        using var bed = new TestBed();
        var recorder = new RecordingGenerator();
        var bridge = new UnityMonoGenerationBridge(recorder);

        var result = await bridge.GenerateAsync(bed.Context(), SimplePlan(bed), bed.Workspace);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.ArgumentInvalid, result.Error!.Code);
        Assert.Equal(0, recorder.Calls);
    }

    [Fact]
    public async Task Bridge_GeneratorException_BecomesForgeError()
    {
        using var bed = new TestBed();
        var bridge = new UnityMonoGenerationBridge(new ThrowingGenerator());

        var result = await bridge.GenerateAsync(
            bed.Context(),
            SimplePlan(bed),
            Path.Combine("games", "fake-game", "05-generate"));

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.Internal, result.Error!.Code);
        Assert.Contains("boom", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adapter_DelegatesAllFourInterfacesToInjectedImplementations()
    {
        using var bed = new TestBed();
        var detector = new FakeDetector();
        var analyzer = new FakeAnalyzer();
        var planner = new FakePlanner();
        var generator = new RecordingGenerator();
        var adapter = new UnityMonoAdapter(detector, analyzer, planner, generator);
        var context = bed.Context();
        var detection = TestBed.UnityMonoDetection();
        var analysis = new AnalysisReport { Detection = detection };
        var plan = SimplePlan(bed);

        var detected = await adapter.DetectAsync(context);
        var analyzed = await adapter.AnalyzeAsync(context, detection);
        var planned = await adapter.PlanAsync(context, analysis);
        var generated = await adapter.GenerateAsync(context, plan, Path.Combine("games", "fake-game", "05-generate"));

        Assert.True(detected.Ok);
        Assert.True(analyzed.Ok);
        Assert.True(planned.Ok);
        Assert.True(generated.Ok);
        Assert.Equal(1, detector.Calls);
        Assert.Equal(1, analyzer.Calls);
        Assert.Equal(1, planner.Calls);
        Assert.Equal(1, generator.Calls);
    }

    [Fact]
    public async Task RealGenerator_IdempotentOnSameOutputDirectory()
    {
        using var bed = new TestBed();
        bed.InstallTestAssemblyAsGame();
        var analysis = await Analyze(bed);
        var planResult = await new UnityMonoPlanner().PlanAsync(bed.Context(), analysis);
        Assert.True(planResult.Ok, planResult.Error?.ToString());

        var framework = FindFrameworkDirectory();
        Assert.NotNull(framework);
        var generator = new UnityMonoGenerator(
            null,
            new GeneratorOptions { FrameworkDirectory = framework });
        var bridge = new UnityMonoGenerationBridge(generator);
        var output = Path.Combine(bed.Workspace, "games", "fake-game", "05-generate");

        var first = await bridge.GenerateAsync(bed.Context(), planResult.Value!, output);
        Assert.True(first.Ok, first.Error?.ToString());
        Assert.True(File.Exists(Path.Combine(output, "forge.manifest.json")));
        Assert.True(File.Exists(Path.Combine(output, "Plugin.cs")));
        Assert.True(Directory.Exists(Path.Combine(output, "Hooks")));
        var firstArtifacts = first.Value!.Artifacts.Select(a => a.RelativePath).ToArray();

        var second = await bridge.GenerateAsync(bed.Context(), planResult.Value!, output);
        Assert.True(second.Ok, second.Error?.ToString());
        var secondArtifacts = second.Value!.Artifacts.Select(a => a.RelativePath).ToArray();
        Assert.Equal(firstArtifacts, secondArtifacts);
    }

    private static async Task<AnalysisReport> Analyze(TestBed bed)
    {
        var result = await new UnityMonoAnalyzer().AnalyzeAsync(bed.Context(), TestBed.UnityMonoDetection());
        Assert.True(result.Ok, result.Error?.ToString());
        return result.Value!;
    }

    private static string? FindFrameworkDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && directory is not null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Framework");
            if (File.Exists(Path.Combine(candidate, "platform", "Speech.cs")))
            {
                return candidate;
            }
        }

        return null;
    }

    private static PatchPlan SimplePlan(TestBed bed) => SimplePlan(bed.GameDirectory);

    private static PatchPlan SimplePlan(string gameDirectory) => new()
    {
        GameSlug = "fake-game",
        Route = PlanRoute.AutomaticWithConfirmation,
        Target = new PlanTarget
        {
            GamePath = gameDirectory,
            Engine = UnityMonoAdapter.EngineId,
            EngineVersion = "6000.0.49f1",
            Bitness = 64,
        },
        Confidence = 0.9,
        PatchPoints = new List<PatchPoint>
        {
            new()
            {
                Id = "hook-001",
                Kind = PatchPointKind.Hook,
                Target = "Assembly-CSharp.dll!/FakeGame.UIText:AnimateText",
                CandidateId = "tc-001",
                Confidence = 0.9,
                Description = "读取 FakeGame.UIText::AnimateText 的 System.String text 参数；测试用。",
            },
        },
    };

    private sealed class FakeDetector : IEngineDetector
    {
        public int Calls { get; private set; }

        public Task<ForgeResult<EngineDetection>> DetectAsync(GameContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(ForgeResult<EngineDetection>.Success(TestBed.UnityMonoDetection()));
        }
    }

    private sealed class FakeAnalyzer : IEngineAnalyzer
    {
        public int Calls { get; private set; }

        public Task<ForgeResult<AnalysisReport>> AnalyzeAsync(
            GameContext context,
            EngineDetection detection,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(ForgeResult<AnalysisReport>.Success(new AnalysisReport { Detection = detection }));
        }
    }

    private sealed class FakePlanner : IPlanner
    {
        public int Calls { get; private set; }

        public Task<ForgeResult<PatchPlan>> PlanAsync(
            GameContext context,
            AnalysisReport analysis,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(ForgeResult<PatchPlan>.Success(SimplePlan(context.GameDirectory)));
        }
    }
}
