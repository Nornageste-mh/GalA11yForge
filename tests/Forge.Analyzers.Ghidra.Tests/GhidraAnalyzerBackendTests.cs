using System.Text.Json;
using GalA11yForge.Core.Models;

namespace GalA11yForge.Analyzers.Ghidra.Tests;

public sealed class GhidraAnalyzerBackendTests
{
    private sealed class Setup
    {
        public required GhidraAnalyzerBackend Backend { get; init; }
        public required GhidraLocation Location { get; init; }
        public required string GameDirectory { get; init; }
        public required string Executable { get; init; }
        public required GhidraBackendOptions Options { get; init; }
    }

    private static Setup Create(
        TempDir temp,
        Func<GhidraRunRequest, CancellationToken, Task<GhidraRunResult>>? runner,
        bool enabled = true,
        string? ghidraPath = null)
    {
        var home = FakeGhidra.CreateHome(temp.Sub("ghidra"));
        var gameDirectory = temp.Sub("game");
        var executable = Path.Combine(gameDirectory, "game.exe");
        File.WriteAllText(executable, "MZ");

        var options = new GhidraBackendOptions
        {
            WorkspaceRoot = temp.Sub("ws"),
            GhidraPath = ghidraPath,
            Enabled = enabled,
            TimeoutSeconds = 30,
        };
        var location = new GhidraLocation
        {
            Available = true,
            HomeDirectory = home,
            AnalyzeHeadlessPath = Path.Combine(home, "support", "analyzeHeadless.bat"),
            Version = "12.1.4",
            Source = "test",
            Probes = new[] { $"{home}（已找到）" },
        };

        return new Setup
        {
            Backend = new GhidraAnalyzerBackend(options, location, runner),
            Location = location,
            GameDirectory = gameDirectory,
            Executable = executable,
            Options = options,
        };
    }

    private static GhidraRunResult Result(
        GhidraRunRequest request,
        int exitCode = 0,
        bool timedOut = false,
        bool canceled = false,
        bool startFailed = false,
        string stdout = "",
        string stderr = "",
        TimeSpan? elapsed = null)
    {
        var command = GhidraCommandBuilder.Build(new GhidraCommandRequest
        {
            AnalyzeHeadlessPath = request.AnalyzeHeadlessPath,
            ProjectDirectory = request.ProjectDirectory,
            ProjectName = request.ProjectName,
            ImportPath = request.ImportPath,
            ScriptDirectory = request.ScriptDirectory,
            OutputDirectory = request.OutputDirectory,
            PostScript = request.PostScript,
            AnalysisTimeoutSeconds = request.AnalysisTimeoutSeconds,
            MaxCpu = request.MaxCpu,
            DeleteProject = request.DeleteProject,
        });

        return new GhidraRunResult
        {
            Command = command,
            StandardOutputPath = request.StandardOutputPath ?? Path.Combine(request.ProjectDirectory, "stdout.log"),
            StandardErrorPath = request.StandardErrorPath ?? Path.Combine(request.ProjectDirectory, "stderr.log"),
            Started = !startFailed,
            ExitCode = exitCode,
            Elapsed = elapsed ?? TimeSpan.FromSeconds(1.5),
            StandardOutput = stdout,
            StandardError = stderr,
            StartError = startFailed ? "模拟启动失败" : null,
            TimedOut = timedOut,
            Canceled = canceled,
            StartFailed = startFailed,
        };
    }

    private static AnalyzerBackendRequest Request(Setup setup) => new()
    {
        GameDirectory = setup.GameDirectory,
        ExecutablePath = setup.Executable,
        Advanced = true,
        TimeoutSeconds = 30,
    };

    [Fact]
    public async Task Analyze_Completed_MapsCandidatesCoverageAndEvidence()
    {
        using var temp = new TempDir();
        var setup = Create(temp, async (request, _) =>
        {
            Directory.CreateDirectory(request.OutputDirectory);
            var jsonPath = Path.Combine(request.OutputDirectory, "candidates.json");
            await File.WriteAllTextAsync(jsonPath, Fixtures.CandidatesJson());
            return Result(
                request,
                stdout: $"INFO noise\nWROTE {jsonPath} bytes=100 candidates=1 cjk_strings=3 packed=false\n");
        });

        var outcome = await setup.Backend.AnalyzeDetailedAsync(Request(setup));

        Assert.Equal(GhidraAnalysisStatus.Completed, outcome.Status);
        Assert.True(outcome.Available);
        Assert.True(outcome.Succeeded);
        Assert.False(outcome.IsHardFailure);
        Assert.Equal("12.1.4", outcome.ToolVersion);
        Assert.Equal(4_583_168, outcome.ExecutableSizeBytes);
        Assert.Equal(64, outcome.ExecutableSha256!.Length);

        var candidate = Assert.Single(outcome.Candidates);
        Assert.Equal(TextCandidateKind.NativeApi, candidate.Kind);
        Assert.StartsWith("native-api:ghidra!0x401000", candidate.Source);
        Assert.Equal(47, candidate.Score, 1);
        Assert.Contains(candidate.Evidence, e => e.StartsWith("符号：FUN_00401000 @ 0x401000", StringComparison.Ordinal));
        Assert.Contains(candidate.Evidence, e => e.StartsWith("引用：调用者 2 个", StringComparison.Ordinal));
        Assert.Contains(candidate.Evidence, e => e.StartsWith("文本 API：TextOutW", StringComparison.Ordinal));
        Assert.Contains(candidate.Evidence, e => e.StartsWith("挂钩建议：", StringComparison.Ordinal));
        var samples = candidate.Evidence.Where(e => e.StartsWith("样例：", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, samples.Count);
        Assert.All(samples, e => Assert.True(e.Length <= "样例：".Length + 51));

        Assert.Contains(outcome.Coverage.Covered, c => c.Contains("318 个函数", StringComparison.Ordinal) || c.Contains("420 个函数", StringComparison.Ordinal));
        Assert.Contains(outcome.Coverage.Covered, c => c.Contains("导入表文本 API 命中 3 个", StringComparison.Ordinal));
        Assert.Contains(outcome.Coverage.Covered, c => c.Contains("中日文字符串引用 13 条", StringComparison.Ordinal));
        Assert.Contains(outcome.Unverified, u => u.Contains("静态推断", StringComparison.Ordinal));
        Assert.Contains(outcome.Evidence, e => e.Contains("Ghidra 12.1.4", StringComparison.Ordinal));
        Assert.Contains(outcome.Evidence, e => e.Contains("SHA-256", StringComparison.Ordinal));
        Assert.StartsWith("Ghidra 分析完成：1 个候选", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains(outcome.Coverage.ManualTodo, m => m.Contains("静态候选不构成成品补丁", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Analyze_PackedBinary_RaisesPackedRiskAndUnverified()
    {
        using var temp = new TempDir();
        var setup = Create(temp, async (request, _) =>
        {
            Directory.CreateDirectory(request.OutputDirectory);
            var jsonPath = Path.Combine(request.OutputDirectory, "candidates.json");
            await File.WriteAllTextAsync(jsonPath, Fixtures.CandidatesJson(packed: true));
            return Result(request, stdout: $"WROTE {jsonPath} bytes=1 candidates=1 cjk_strings=0 packed=true");
        });

        var outcome = await setup.Backend.AnalyzeDetailedAsync(Request(setup));

        var risk = Assert.Single(outcome.Risks, r => r.Id == "ghidra-packed");
        Assert.Equal(RiskSeverity.High, risk.Severity);
        Assert.Contains(outcome.Unverified, u => u.Contains("packed_suspected=true", StringComparison.Ordinal));
        Assert.Contains(outcome.Coverage.NotCovered, c => c.Contains("加壳", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Analyze_DynamicResolution_AddsMediumRisk()
    {
        using var temp = new TempDir();
        var setup = Create(temp, async (request, _) =>
        {
            Directory.CreateDirectory(request.OutputDirectory);
            var jsonPath = Path.Combine(request.OutputDirectory, "candidates.json");
            await File.WriteAllTextAsync(jsonPath, Fixtures.CandidatesJson(dynamicApi: true));
            return Result(request, stdout: $"WROTE {jsonPath} bytes=1 candidates=1 cjk_strings=0 packed=false");
        });

        var outcome = await setup.Backend.AnalyzeDetailedAsync(Request(setup));

        var risk = Assert.Single(outcome.Risks, r => r.Id == "ghidra-dynamic-api");
        Assert.Equal(RiskSeverity.Medium, risk.Severity);
        Assert.Contains(outcome.Unverified, u => u.Contains("GetProcAddress", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Analyze_ReportedCountMismatch_IsWrittenAsUnverified()
    {
        using var temp = new TempDir();
        var setup = Create(temp, async (request, _) =>
        {
            Directory.CreateDirectory(request.OutputDirectory);
            var jsonPath = Path.Combine(request.OutputDirectory, "candidates.json");
            await File.WriteAllTextAsync(jsonPath, Fixtures.CandidatesJson());
            return Result(request, stdout: $"WROTE {jsonPath} bytes=1 candidates=5 cjk_strings=0 packed=false");
        });

        var outcome = await setup.Backend.AnalyzeDetailedAsync(Request(setup));

        Assert.Contains(outcome.Unverified, u => u.Contains("不一致", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Analyze_WrotePathOutsideWorkDirectory_IsRejected()
    {
        using var temp = new TempDir();
        var outside = Path.Combine(temp.Root, "outside.json");
        var setup = Create(temp, (request, _) =>
            Task.FromResult(Result(request, stdout: $"WROTE {outside} bytes=1 candidates=1 cjk_strings=0 packed=false")));

        var outcome = await setup.Backend.AnalyzeDetailedAsync(Request(setup));

        Assert.Equal(GhidraAnalysisStatus.InvalidOutput, outcome.Status);
        Assert.Equal("E_PATH_UNSAFE", outcome.ErrorCode);
        Assert.True(outcome.IsHardFailure);
        Assert.True(outcome.Available);
    }

    [Fact]
    public async Task Analyze_SuccessExitWithoutArtifact_IsInvalidOutput()
    {
        using var temp = new TempDir();
        var setup = Create(temp, (request, _) => Task.FromResult(Result(request, exitCode: 0)));

        var outcome = await setup.Backend.AnalyzeDetailedAsync(Request(setup));

        Assert.Equal(GhidraAnalysisStatus.InvalidOutput, outcome.Status);
        Assert.Equal("E_ARTIFACT_MISSING", outcome.ErrorCode);
    }

    [Fact]
    public async Task Analyze_Timeout_ReturnsPartialAndCleansProjectDirectory()
    {
        using var temp = new TempDir();
        GhidraRunRequest? captured = null;
        var setup = Create(temp, (request, _) =>
        {
            captured = request;
            Directory.CreateDirectory(request.ProjectDirectory);
            File.WriteAllText(Path.Combine(request.ProjectDirectory, "gala11y.lock"), "lock");
            return Task.FromResult(Result(request, exitCode: -1, timedOut: true, elapsed: TimeSpan.FromSeconds(30)));
        });

        var outcome = await setup.Backend.AnalyzeDetailedAsync(Request(setup));

        Assert.Equal(GhidraAnalysisStatus.Timeout, outcome.Status);
        Assert.Equal("E_PARTIAL", outcome.ErrorCode);
        Assert.True(outcome.IsHardFailure);
        Assert.Contains("超时", outcome.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("ghidra.timeoutSeconds", outcome.Suggestion, StringComparison.Ordinal);
        Assert.NotNull(captured);
        Assert.NotNull(outcome.WorkDirectory);
        Assert.False(Directory.Exists(Path.Combine(outcome.WorkDirectory!, "project")));
        Assert.Contains(outcome.Unverified, u => u.Contains("不能据此判断", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Analyze_GhidraNotFound_IsHonestUnavailableNotFailure()
    {
        using var temp = new TempDir();
        var options = new GhidraBackendOptions
        {
            WorkspaceRoot = temp.Sub("ws"),
            GhidraPath = Path.Combine(temp.Root, "no-ghidra"),
        };
        var backend = new GhidraAnalyzerBackend(options);

        var outcome = await backend.AnalyzeDetailedAsync(new AnalyzerBackendRequest
        {
            GameDirectory = temp.Sub("game"),
        });

        Assert.Equal(GhidraAnalysisStatus.NotAvailable, outcome.Status);
        Assert.False(outcome.Available);
        Assert.False(outcome.Succeeded);
        Assert.False(outcome.IsHardFailure);
        Assert.Equal("未启用高级分析（未找到 Ghidra）。", outcome.Summary);
        Assert.Equal("E_DEPENDENCY_MISSING", outcome.ErrorCode);
        Assert.Empty(outcome.Candidates);
        Assert.Contains(outcome.Unverified, u => u.Contains("不能据本次结果判断", StringComparison.Ordinal));
        Assert.True(outcome.Coverage.ManualTodo.Count >= 2);
        Assert.NotEmpty(outcome.Evidence);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Location?.Error));
    }

    [Fact]
    public async Task Analyze_Disabled_DoesNotTouchFilesystem()
    {
        using var temp = new TempDir();
        var setup = Create(temp, (_, _) => throw new InvalidOperationException("不应启动 runner"), enabled: false);

        var outcome = await setup.Backend.AnalyzeDetailedAsync(Request(setup));

        Assert.Equal(GhidraAnalysisStatus.Disabled, outcome.Status);
        Assert.Equal("已禁用 Ghidra 高级分析（ghidra.enabled=false）。", outcome.Summary);
        Assert.Null(outcome.ErrorCode);
        Assert.False(outcome.IsHardFailure);
    }

    [Fact]
    public async Task Analyze_NoExecutableInGameDirectory_IsNoTarget()
    {
        using var temp = new TempDir();
        var setup = Create(temp, (_, _) => throw new InvalidOperationException("不应启动 runner"));
        var emptyGame = temp.Sub("empty-game");
        File.Delete(setup.Executable);

        var outcome = await setup.Backend.AnalyzeDetailedAsync(new AnalyzerBackendRequest
        {
            GameDirectory = emptyGame,
        });

        Assert.Equal(GhidraAnalysisStatus.NoTarget, outcome.Status);
        Assert.Equal("E_ARTIFACT_MISSING", outcome.ErrorCode);
        Assert.Contains("未找到可分析的可执行文件", outcome.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeAsync_Unavailable_IsSuccessResultWithUnverified()
    {
        using var temp = new TempDir();
        var backend = new GhidraAnalyzerBackend(new GhidraBackendOptions
        {
            WorkspaceRoot = temp.Sub("ws"),
            GhidraPath = Path.Combine(temp.Root, "no-ghidra"),
        });

        var result = await backend.AnalyzeAsync(new AnalyzerBackendRequest { GameDirectory = temp.Sub("game") });

        Assert.True(result.Ok);
        Assert.NotNull(result.Value);
        Assert.Equal("ghidra", result.Value!.BackendId);
        Assert.Empty(result.Value.Candidates);
        Assert.Contains(result.Value.Unverified, u => u.Contains("未启用高级分析", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsync_Timeout_IsFailureWithPartialCode()
    {
        using var temp = new TempDir();
        var setup = Create(temp, (request, _) =>
        {
            Directory.CreateDirectory(request.ProjectDirectory);
            return Task.FromResult(Result(request, exitCode: -1, timedOut: true));
        });

        var result = await setup.Backend.AnalyzeAsync(Request(setup));

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Equal("E_PARTIAL", result.Error!.Code);
        Assert.Contains("超时", result.Error.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(result.Error.Suggestion));
    }

    [Fact]
    public void TargetSelector_SkipsInstallersAndPicksLargestExe()
    {
        using var temp = new TempDir();
        var game = temp.Sub("game");
        File.WriteAllBytes(Path.Combine(game, "game.exe"), new byte[64]);
        File.WriteAllBytes(Path.Combine(game, "big.exe"), new byte[128]);
        File.WriteAllBytes(Path.Combine(game, "setup.exe"), new byte[256]);
        File.WriteAllBytes(Path.Combine(game, "unins000.exe"), new byte[512]);
        File.WriteAllBytes(Path.Combine(game, "vcredist_x86.exe"), new byte[1024]);

        var selected = GhidraTargetSelector.FindPrimaryExecutable(game);

        Assert.Equal(Path.Combine(game, "big.exe"), selected);
    }

    [Fact]
    public void TargetSelector_FallsBackToSubdirectory_WhenRootHasNoExe()
    {
        using var temp = new TempDir();
        var game = temp.Sub("game");
        var bin = Directory.CreateDirectory(Path.Combine(game, "bin")).FullName;
        File.WriteAllBytes(Path.Combine(bin, "MyGame.exe"), new byte[640]);

        var selected = GhidraTargetSelector.FindPrimaryExecutable(game);

        Assert.Equal(Path.Combine(bin, "MyGame.exe"), selected);
    }

    [Fact]
    public void TargetSelector_RootExeWinsOverSubdirectoryExe()
    {
        using var temp = new TempDir();
        var game = temp.Sub("game");
        File.WriteAllBytes(Path.Combine(game, "game.exe"), new byte[64]);
        var bin = Directory.CreateDirectory(Path.Combine(game, "bin")).FullName;
        File.WriteAllBytes(Path.Combine(bin, "game.exe"), new byte[4096]);

        var selected = GhidraTargetSelector.FindPrimaryExecutable(game);

        Assert.Equal(Path.Combine(game, "game.exe"), selected);
    }

    [Fact]
    public void TargetSelector_PrefersExeNamedLikeGameFolder_OverLargerSibling()
    {
        using var temp = new TempDir();
        var game = temp.Sub("MyGame");
        var inner = Directory.CreateDirectory(Path.Combine(game, "MyGame")).FullName;
        File.WriteAllBytes(Path.Combine(inner, "MyGame.exe"), new byte[100]);
        File.WriteAllBytes(Path.Combine(inner, "engine_big.exe"), new byte[4096]);

        var selected = GhidraTargetSelector.FindPrimaryExecutable(game);

        Assert.Equal(Path.Combine(inner, "MyGame.exe"), selected);
    }

    [Fact]
    public void TargetSelector_IgnoresExecutablesDeeperThanTwoLevels()
    {
        using var temp = new TempDir();
        var game = temp.Sub("game");
        var deep = Directory.CreateDirectory(Path.Combine(game, "a", "b", "c")).FullName;
        File.WriteAllBytes(Path.Combine(deep, "Game.exe"), new byte[1024]);

        var selected = GhidraTargetSelector.FindPrimaryExecutable(game);

        Assert.Null(selected);
    }

    [Fact]
    public void Options_DefaultTimeout_MatchesClassDefaults()
    {
        using var temp = new TempDir();

        var options = GhidraBackendOptions.FromProfile(null, temp.Root);

        Assert.Equal(600, options.TimeoutSeconds);
        Assert.Equal(900, options.AnalysisTimeoutSeconds);
    }

    [Fact]
    public void Options_ExecutablePath_ExistingFile_IsResolved()
    {
        using var temp = new TempDir();
        var exe = Path.Combine(temp.Root, "custom.exe");
        File.WriteAllBytes(exe, new byte[16]);

        var options = GhidraBackendOptions.FromProfile(
            new Dictionary<string, JsonElement>
            {
                [GhidraBackendOptions.ExecutableKey] = JsonSerializer.SerializeToElement(exe),
            },
            temp.Sub("ws"));

        Assert.Equal(Path.GetFullPath(exe), options.ExecutablePath);
        Assert.Empty(options.Warnings);
    }

    [Fact]
    public void Options_ExecutablePath_MissingFile_WarnsAndFallsBackToAuto()
    {
        using var temp = new TempDir();
        var missing = Path.Combine(temp.Root, "nope.exe");

        var options = GhidraBackendOptions.FromProfile(
            new Dictionary<string, JsonElement>
            {
                [GhidraBackendOptions.ExecutableKey] = JsonSerializer.SerializeToElement(missing),
            },
            temp.Sub("ws"));

        Assert.Null(options.ExecutablePath);
        Assert.Contains(options.Warnings, warning => warning.Contains("已忽略", StringComparison.Ordinal));
    }

    [Fact]
    public void TargetSelector_FindCandidates_ListsRootAndSubdirectoryExes_Ordered()
    {
        using var temp = new TempDir();
        var game = temp.Sub("MyGame");
        var inner = Directory.CreateDirectory(Path.Combine(game, "MyGame")).FullName;
        File.WriteAllBytes(Path.Combine(game, "game.exe"), new byte[512]);
        File.WriteAllBytes(Path.Combine(inner, "MyGame.exe"), new byte[100]);
        File.WriteAllBytes(Path.Combine(inner, "engine_big.exe"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(game, "setup.exe"), new byte[8192]);
        File.WriteAllBytes(Path.Combine(inner, "UnityCrashHandler64.exe"), new byte[8192]);

        var candidates = GhidraTargetSelector.FindCandidates(game);

        Assert.Equal(Path.Combine(inner, "MyGame.exe"), candidates[0]);
        Assert.Contains(Path.Combine(game, "game.exe"), candidates);
        Assert.DoesNotContain(candidates, path => path.EndsWith("setup.exe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(candidates, path => path.EndsWith("UnityCrashHandler64.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Analyze_AutoTarget_WithMultipleCandidates_RecordsCandidatesAndHint()
    {
        using var temp = new TempDir();
        GhidraRunRequest? captured = null;
        var setup = Create(temp, async (request, _) =>
        {
            captured = request;
            Directory.CreateDirectory(request.OutputDirectory);
            var jsonPath = Path.Combine(request.OutputDirectory, "candidates.json");
            await File.WriteAllTextAsync(jsonPath, Fixtures.CandidatesJson());
            return Result(request, stdout: $"WROTE {jsonPath} bytes=100 candidates=1 cjk_strings=3 packed=false\n");
        });
        File.WriteAllBytes(Path.Combine(setup.GameDirectory, "extra_tool.exe"), new byte[256]);

        var outcome = await setup.Backend.AnalyzeDetailedAsync(new AnalyzerBackendRequest
        {
            GameDirectory = setup.GameDirectory,
            Advanced = true,
            TimeoutSeconds = 30,
        });

        Assert.Equal(GhidraAnalysisStatus.Completed, outcome.Status);
        Assert.NotNull(captured);
        Assert.Equal(2, outcome.ExecutableCandidates.Count);
        Assert.Contains(outcome.Unverified, line => line.Contains("候选可执行文件", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Analyze_ExplicitExecutable_OverridesAutoTarget()
    {
        using var temp = new TempDir();
        GhidraRunRequest? captured = null;
        var setup = Create(temp, async (request, _) =>
        {
            captured = request;
            Directory.CreateDirectory(request.OutputDirectory);
            var jsonPath = Path.Combine(request.OutputDirectory, "candidates.json");
            await File.WriteAllTextAsync(jsonPath, Fixtures.CandidatesJson());
            return Result(request, stdout: $"WROTE {jsonPath} bytes=100 candidates=1 cjk_strings=3 packed=false\n");
        });
        File.WriteAllBytes(Path.Combine(setup.GameDirectory, "other.exe"), new byte[256]);
        var explicitExe = Path.Combine(setup.GameDirectory, "other.exe");

        var outcome = await setup.Backend.AnalyzeDetailedAsync(new AnalyzerBackendRequest
        {
            GameDirectory = setup.GameDirectory,
            ExecutablePath = explicitExe,
            Advanced = true,
            TimeoutSeconds = 30,
        });

        Assert.Equal(GhidraAnalysisStatus.Completed, outcome.Status);
        Assert.NotNull(captured);
        Assert.Equal(Path.GetFullPath(explicitExe), Path.GetFullPath(captured.ImportPath));
        Assert.Empty(outcome.ExecutableCandidates);
    }
}
