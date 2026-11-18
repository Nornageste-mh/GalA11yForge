using System.Text.Json;
using GalA11yForge.Adapters.UnityMono.Internal;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Adapters.UnityMono.Tests;

public sealed class DiscoveryAndAnalyzeTests
{
    [Fact]
    public async Task Analyze_EmptyGameDirectory_ReturnsEngineUnsupported()
    {
        using var bed = new TestBed();

        var result = await new UnityMonoAnalyzer().AnalyzeAsync(
            bed.Context(),
            TestBed.UnityMonoDetection());

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.EngineUnsupported, result.Error!.Code);
        Assert.Contains("Assembly-CSharp.dll", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_MissingGameDirectory_ReturnsArgumentInvalid()
    {
        using var bed = new TestBed();
        var context = GameContext.Create(Path.Combine(bed.Root, "does-not-exist"), bed.Workspace, "fake-game");

        var result = await new UnityMonoAnalyzer().AnalyzeAsync(context, TestBed.UnityMonoDetection());

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.ArgumentInvalid, result.Error!.Code);
    }

    [Fact]
    public async Task Analyze_WrongEngineFamily_ReturnsEngineUnsupported()
    {
        using var bed = new TestBed();
        bed.InstallTestAssemblyAsGame();
        var detection = TestBed.UnityMonoDetection();
        detection.Family = "renpy";

        var result = await new UnityMonoAnalyzer().AnalyzeAsync(bed.Context(), detection);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.EngineUnsupported, result.Error!.Code);
    }

    [Fact]
    public async Task Analyze_BadAssembly_ReturnsArgumentInvalid()
    {
        using var bed = new TestBed();
        bed.WriteBadAssembly();

        var result = await new UnityMonoAnalyzer().AnalyzeAsync(
            bed.Context(),
            TestBed.UnityMonoDetection());

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.ArgumentInvalid, result.Error!.Code);
        Assert.Contains("无法读取", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_EmptyAssemblyFile_ReturnsArgumentInvalid()
    {
        using var bed = new TestBed();
        bed.WriteEmptyAssembly();

        var result = await new UnityMonoAnalyzer().AnalyzeAsync(
            bed.Context(),
            TestBed.UnityMonoDetection());

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.ArgumentInvalid, result.Error!.Code);
    }

    [Fact]
    public async Task Analyze_FakeGame_FindsAllFourEntryPoints_WithS9Evidence()
    {
        using var bed = new TestBed();
        var assembly = bed.InstallTestAssemblyAsGame();
        var before = TestBed.Sha256(assembly);

        var result = await new UnityMonoAnalyzer().AnalyzeAsync(
            bed.Context(),
            TestBed.UnityMonoDetection());

        Assert.True(result.Ok, result.Error?.ToString());
        var report = result.Value!;
        Assert.Equal(bed.GameDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), report.GamePath);
        Assert.NotNull(report.Detection);
        Assert.True(report.Candidates.Count >= 4, $"candidates={report.Candidates.Count}");

        var sources = report.Candidates.Select(c => c.Source).ToList();
        Assert.Contains(sources, s => s.Contains("FakeGame.UIText::AnimateText", StringComparison.Ordinal));
        Assert.Contains(sources, s => s.Contains("FakeGame.UIText::DOText", StringComparison.Ordinal));
        Assert.Contains(sources, s => s.Contains("FakeGame.UIText::Choice", StringComparison.Ordinal));
        Assert.Contains(sources, s => s.Contains("FakeGame.UIText::HandleMessageText", StringComparison.Ordinal));

        var animate = report.Candidates.Single(c => c.Source.Contains("::AnimateText", StringComparison.Ordinal));
        Assert.Equal(TextCandidateKind.Dialogue, animate.Kind);
        Assert.True(animate.Score >= 70, $"AnimateText score={animate.Score}");
        Assert.Contains(animate.Evidence, line => line.Contains("S9+20", StringComparison.Ordinal));
        Assert.Contains(animate.Evidence, line => line.Contains("DOText", StringComparison.Ordinal));
        Assert.Contains(animate.Evidence, line => line.Contains("签名：", StringComparison.Ordinal));
        Assert.Contains(animate.Evidence, line => line.Contains("System.String text", StringComparison.Ordinal));

        var choice = report.Candidates.Single(c => c.Source.Contains("::Choice", StringComparison.Ordinal));
        Assert.Equal(TextCandidateKind.Choice, choice.Kind);

        Assert.NotEmpty(report.Coverage.Covered);
        Assert.NotEmpty(report.Coverage.NotCovered);
        Assert.NotEmpty(report.Coverage.ManualTodo);
        Assert.NotEmpty(report.Risks);
        Assert.NotEmpty(report.Unverified);
        Assert.Contains(report.Coverage.Covered, line => line.Contains("S9", StringComparison.Ordinal));
        Assert.Equal(before, TestBed.Sha256(assembly));
    }

    [Fact]
    public async Task Analyze_CoverageManualTodo_IsDerivedFromThisGame_NotAnotherTitle()
    {
        // 回归：0.2.48 之前 coverage/manual 里写死了别款游戏的入口名与实测秒数
        // （DOText→HandleMessageText→AnimateText、“67.7/416.2 秒”），
        // 用户给新游戏装补丁时会误以为报告在说另一款游戏。
        using var bed = new TestBed();
        bed.InstallTestAssemblyAsGame();

        var result = await new UnityMonoAnalyzer().AnalyzeAsync(
            bed.Context(),
            TestBed.UnityMonoDetection());

        Assert.True(result.Ok, result.Error?.ToString());
        var manual = result.Value!.Coverage.ManualTodo;
        Assert.NotEmpty(manual);

        var entryLine = Assert.Single(manual, line => line.Contains("真的被调用", StringComparison.Ordinal));
        Assert.Contains("AnimateText", entryLine, StringComparison.Ordinal);

        foreach (var line in manual)
        {
            Assert.DoesNotContain("67.7", line, StringComparison.Ordinal);
            Assert.DoesNotContain("416.2", line, StringComparison.Ordinal);
            Assert.DoesNotContain("DOText→HandleMessageText→AnimateText", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Analyze_IsIdempotent_SameIdsSourcesAndScores()
    {
        using var bed = new TestBed();
        bed.InstallTestAssemblyAsGame();
        var analyzer = new UnityMonoAnalyzer();

        var first = await analyzer.AnalyzeAsync(bed.Context(), TestBed.UnityMonoDetection());
        var second = await analyzer.AnalyzeAsync(bed.Context(), TestBed.UnityMonoDetection());

        Assert.True(first.Ok, first.Error?.ToString());
        Assert.True(second.Ok, second.Error?.ToString());
        var a = first.Value!.Candidates.Select(c => (c.Id, c.Source, c.Score)).ToArray();
        var b = second.Value!.Candidates.Select(c => (c.Id, c.Source, c.Score)).ToArray();
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task Analyze_ProfileLanguage_IsReflectedInCandidateEvidence()
    {
        using var bed = new TestBed();
        bed.InstallTestAssemblyAsGame();
        using var document = JsonDocument.Parse("""{"language":"ja"}""");
        var profile = new ProfileDocument
        {
            Options = new Dictionary<string, JsonElement> { ["language"] = document.RootElement.Clone() },
        };

        var result = await new UnityMonoAnalyzer().AnalyzeAsync(
            bed.Context(profile: profile),
            TestBed.UnityMonoDetection());

        Assert.True(result.Ok, result.Error?.ToString());
        var animate = result.Value!.Candidates.Single(c => c.Source.Contains("::AnimateText", StringComparison.Ordinal));
        Assert.Contains(animate.Evidence, line => line.Contains("language=ja", StringComparison.Ordinal));
    }
}
