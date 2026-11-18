using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Adapters.Renpy.Tests;

public sealed class DetectionTests
{
    private readonly RenpyAdapter _adapter = new();

    [Fact]
    public async Task Detect_Renpy699_ParsesVersionTupleAndPython2Bitness()
    {
        using var fixture = new RenpyFixture("6.99");

        var result = await _adapter.DetectAsync(fixture.Context());

        Assert.True(result.Ok, result.Error?.Message);
        var detection = result.Value!;
        Assert.Equal("renpy", detection.Family);
        Assert.Equal("6.99.12.4", detection.Version);
        Assert.Equal(32, detection.Bitness);
        Assert.InRange(detection.Confidence, 0.85, 1.0);
        Assert.Contains(detection.Evidence, e => e.Contains("windows-i686", StringComparison.Ordinal));
        Assert.Contains(detection.Evidence, e => e.Contains("renpy/__init__.py", StringComparison.Ordinal));
        Assert.Contains(detection.Evidence, e => e.Contains("RPA-3.0", StringComparison.Ordinal));
        Assert.Contains(detection.Evidence, e => e.Contains("unverified:未启动游戏", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Detect_Renpy85_ParsesVersionFromVcVersionAnd64Bit()
    {
        using var fixture = new RenpyFixture("8.5");

        var result = await _adapter.DetectAsync(fixture.Context());

        Assert.True(result.Ok, result.Error?.Message);
        var detection = result.Value!;
        Assert.Equal("8.5.2.26010301", detection.Version);
        Assert.Equal(64, detection.Bitness);
        Assert.Contains(detection.Evidence, e => e.Contains("renpy/vc_version.py", StringComparison.Ordinal));
        Assert.Contains(detection.Evidence, e => e.Contains("py3-windows-x86_64", StringComparison.Ordinal));
        Assert.Contains(detection.Evidence, e => e.Contains("branch=fix", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Detect_ExistingA11yOverlay_IsReportedInEvidence()
    {
        using var fixture = new RenpyFixture("8.5").WithExistingOverlay();

        var result = await _adapter.DetectAsync(fixture.Context());

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Contains(result.Value!.Evidence, e => e.Contains("疑似已装 a11y", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Detect_NonRenpyDirectory_ReturnsEngineUnsupported()
    {
        using var fixture = new RenpyFixture("6.99");
        var plain = Path.Combine(fixture.Root, "plain-game");
        Directory.CreateDirectory(Path.Combine(plain, "game"));

        var result = await _adapter.DetectAsync(GameContext.Create(plain, fixture.Workspace));

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.EngineUnsupported, result.Error!.Code);
    }

    [Fact]
    public async Task Detect_MissingDirectory_ReturnsArgumentInvalid()
    {
        var missing = Path.Combine(Path.GetTempPath(), "gaforge-renpy-tests", Guid.NewGuid().ToString("N"), "nope");

        var result = await _adapter.DetectAsync(GameContext.Create(missing, Path.GetDirectoryName(missing)!));

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.ArgumentInvalid, result.Error!.Code);
    }

    [Fact]
    public async Task Detect_DoesNotWriteGameDirectory()
    {
        using var fixture = new RenpyFixture("6.99");
        var before = Snapshot(fixture.GameDirectory);

        var result = await _adapter.DetectAsync(fixture.Context());

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(before, Snapshot(fixture.GameDirectory));
    }

    private static string[] Snapshot(string root) => Directory
        .EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => $"{Path.GetRelativePath(root, path)}|{new FileInfo(path).Length}")
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();
}
