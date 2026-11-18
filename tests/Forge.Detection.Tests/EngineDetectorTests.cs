using System.Text;

namespace GalA11yForge.Detection.Tests;

public class EngineDetectorTests
{
    private static readonly EngineDetector Detector = new();

    [Fact]
    public void UnityMono_Detected_WithVersionBitnessAndDualRules()
    {
        using var game = GameFixtures.UnityMono();

        var result = Detector.Detect(game.Path);

        Assert.Equal("unity_mono", result.Family);
        Assert.True(result.IsKnown);
        Assert.False(result.IsSuspected);
        Assert.InRange(result.Confidence, 0.9, 1.0);
        Assert.Equal(0.95, result.Confidence, precision: 6);
        Assert.Equal("2022.3.43f1", result.Primary.Version);
        Assert.Equal(64, result.Primary.Bitness);
        Assert.Equal("高", result.Verdict);
        Assert.NotEmpty(result.Primary.Evidence);
        Assert.Contains(result.Primary.Evidence, line => line.StartsWith("file:", StringComparison.Ordinal));
        Assert.Contains(result.Primary.Evidence, line => line.Contains("@48", StringComparison.Ordinal));
        Assert.Contains(result.Candidates, candidate => candidate.RuleId == "R26" && candidate.Score == 70);
        Assert.All(result.Candidates, candidate => Assert.NotEmpty(candidate.Evidence));
    }

    [Fact]
    public void UnityIl2cpp_Detected_WithoutVersionOrBitnessSource()
    {
        using var game = GameFixtures.UnityIl2Cpp();

        var result = Detector.Detect(game.Path);

        Assert.Equal("unity_il2cpp", result.Family);
        Assert.InRange(result.Confidence, 0.9, 1.0);
        Assert.Null(result.Primary.Version);
        Assert.Equal(32, result.Primary.Bitness);
        Assert.Contains(result.Primary.Evidence, line => line.Contains("unverified:version", StringComparison.Ordinal));
        Assert.Contains(result.Primary.Evidence, line => line.Contains("measured:bitness 32", StringComparison.Ordinal));
    }

    [Fact]
    public void Renpy_Detected_WithTupleAndVcVersion()
    {
        using var game = GameFixtures.Renpy();

        var result = Detector.Detect(game.Path);

        Assert.Equal("renpy", result.Family);
        Assert.Equal(0.95, result.Confidence, precision: 6);
        Assert.Equal("7.4.11.0.2266", result.Primary.Version);
        Assert.Equal(32, result.Primary.Bitness);
        Assert.Contains(result.Primary.Evidence, line => line.Contains("measured:count *.rpa = 1", StringComparison.Ordinal));
        Assert.Contains(result.Primary.Evidence, line => line.Contains("version_tuple", StringComparison.Ordinal));
    }

    [Fact]
    public void KirikiriZ_BeatsKirikiri2_ByDisambiguation()
    {
        using var game = GameFixtures.KirikiriZ();

        var result = Detector.Detect(game.Path);

        Assert.Equal("kirikiri_z", result.Family);
        Assert.Equal(0.90, result.Confidence, precision: 6);

        var kirikiri2 = Assert.Single(result.Candidates.Where(candidate => candidate.Engine == "kirikiri_2"));
        Assert.True(kirikiri2.IsSuppressed);
        Assert.Equal(80, kirikiri2.Score);
        // R05 靠 exe 里的 UTF-16LE "TVP(KIRIKIRI)" 命中：必须两种编码都搜。
        Assert.Contains(kirikiri2.Evidence, line => line.Contains("utf16le", StringComparison.Ordinal));
        Assert.Contains(result.Primary.Evidence, line => line.StartsWith("suppressed:kirikiri_2", StringComparison.Ordinal));
        Assert.Null(result.Primary.Version);
        Assert.Contains(result.Primary.Evidence, line => line.Contains("unverified:version", StringComparison.Ordinal));
        Assert.Equal(32, result.Primary.Bitness);
    }

    [Fact]
    public void GameCreator_BeatsElectron_ByDisambiguation()
    {
        using var game = GameFixtures.GameCreatorElectron();

        var result = Detector.Detect(game.Path);

        Assert.Equal("gamecreator", result.Family);
        Assert.Equal(0.90, result.Confidence, precision: 6);

        var electron = Assert.Single(result.Candidates.Where(candidate => candidate.Engine == "electron_generic"));
        Assert.True(electron.IsSuppressed);
        Assert.Contains(result.Primary.Evidence, line => line.StartsWith("suppressed:electron_generic", StringComparison.Ordinal));
        Assert.Contains(result.Primary.Evidence, line => line.Contains("measured:text index.html", StringComparison.Ordinal));
    }

    [Fact]
    public void MkxmRgss_Detected_ByMagic()
    {
        using var game = GameFixtures.MkxpRgss();

        var result = Detector.Detect(game.Path);

        Assert.Equal("rm_xp_vx_ace", result.Family);
        Assert.Equal(0.90, result.Confidence, precision: 6);
        Assert.Contains(result.Primary.Evidence, line => line.Contains("measured:magic Game.rgssad", StringComparison.Ordinal));
    }

    [Fact]
    public void WebGal_Detected_ByTextAndSceneDirectory()
    {
        using var game = GameFixtures.WebGal();

        var result = Detector.Detect(game.Path);

        Assert.Equal("webgal", result.Family);
        Assert.Equal(0.85, result.Confidence, precision: 6);
        Assert.Contains(result.Primary.Evidence, line => line.Contains("measured:text game/config.txt", StringComparison.Ordinal));
        Assert.Contains(result.Primary.Evidence, line => line.StartsWith("dir:game/scene", StringComparison.Ordinal));
    }

    [Fact]
    public void ElectronGeneric_Detected_WithMediumConfidence()
    {
        using var game = GameFixtures.ElectronGeneric();

        var result = Detector.Detect(game.Path);

        Assert.Equal("electron_generic", result.Family);
        Assert.Equal(0.70, result.Confidence, precision: 6);
        Assert.Equal("中", result.Verdict);
        Assert.Equal(64, result.Primary.Bitness);
        Assert.Null(result.Primary.Version);
        Assert.Contains(result.Primary.Evidence, line => line.Contains("file:resources/app.asar", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyDirectory_ReturnsUnknown_WithScanEvidence()
    {
        using var game = new TempGameDirectory("empty");

        var result = Detector.Detect(game.Path);

        Assert.Equal("unknown", result.Family);
        Assert.Equal(0.0, result.Confidence);
        Assert.False(result.IsKnown);
        Assert.Equal("无", result.Verdict);
        Assert.Empty(result.Candidates);
        Assert.Null(result.BestCandidate);
        Assert.NotEmpty(result.Primary.Evidence);
        Assert.Contains(result.Primary.Evidence, line => line.StartsWith("scan:files=0", StringComparison.Ordinal));
        Assert.Contains(result.Primary.Evidence, line => line.Contains("engine:none", StringComparison.Ordinal));
        Assert.Equal(0, result.FilesScanned);
    }

    [Fact]
    public void UnknownProgram_ReturnsUnknown_AndListsScannedSignals()
    {
        using var game = new TempGameDirectory("unknown");
        game.WriteText("readme.txt", "not a galgame");
        game.WriteBytes("data.bin", new byte[] { 1, 2, 3 });

        var result = Detector.Detect(game.Path);

        Assert.Equal("unknown", result.Family);
        Assert.Equal(0.0, result.Confidence);
        Assert.Contains(result.Primary.Evidence, line => line.Contains("hits=0", StringComparison.Ordinal));
        Assert.Contains(result.Primary.Evidence, line => line.Contains("scan:top_level=", StringComparison.Ordinal));
        Assert.Contains(result.Primary.Evidence, line => line.Contains("readme.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Detection_IsCaseInsensitive()
    {
        using var game = new TempGameDirectory("case-insensitive");
        game.WriteBytes("UnityPlayer.DLL", new byte[200_000]);
        game.WriteBytes("GAME_DATA/Managed/ASSEMBLY-CSHARP.DLL", new byte[50_000]);

        var result = Detector.Detect(game.Path);

        Assert.Equal("unity_mono", result.Family);
        Assert.Contains(result.Primary.Evidence, line => line.Contains("unityplayer.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PeString_IsSkipped_WhenExecutableExceedsTwelveMegabytes()
    {
        using var game = new TempGameDirectory("oversized-exe");
        game.WriteText("default.ttf", "font");

        var executable = new byte[13 * 1024 * 1024];
        Encoding.ASCII.GetBytes("ONScripter").CopyTo(executable, 1024);
        game.WriteBytes("big.exe", executable);

        var result = Detector.Detect(game.Path);

        // R07 需要 pe_string 命中，但 >12MB 的 exe 按参考实现跳过 → 只能报 unknown。
        Assert.Equal("unknown", result.Family);
        Assert.Equal(0.0, result.Confidence);
    }

    [Fact]
    public void Renpy_WithoutVersionFiles_ReportsNullVersionAndUnverified()
    {
        using var game = new TempGameDirectory("renpy-no-version");
        game.CreateDirectory("renpy");
        game.WriteBytes("game/audio.rpa", new byte[8]);

        var result = Detector.Detect(game.Path);

        Assert.Equal("renpy", result.Family);
        Assert.Null(result.Primary.Version);
        Assert.Contains(result.Primary.Evidence, line => line.Contains("unverified:version", StringComparison.Ordinal));
        Assert.Equal(0, result.Primary.Bitness);
        Assert.Contains(result.Primary.Evidence, line => line.Contains("unverified:bitness", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingDirectory_ThrowsDirectoryNotFound()
    {
        var missing = Path.Combine(Path.GetTempPath(), "gala11y-detection-tests", "missing-" + Guid.NewGuid().ToString("N"));

        Assert.Throws<DirectoryNotFoundException>(() => Detector.Detect(missing));
        Assert.Throws<ArgumentException>(() => Detector.Detect("  "));
    }

    [Fact]
    public void ToJson_SerializesContractShape_AndToTuplesMatchesCandidates()
    {
        using var game = GameFixtures.UnityMono();
        var result = Detector.Detect(game.Path);

        var json = result.ToJson();
        Assert.Contains("\"family\": \"unity_mono\"", json);
        Assert.Contains("\"confidence\": 0.95", json);
        Assert.Contains("\"bitness\": 64", json);
        Assert.Contains("\"version\": \"2022.3.43f1\"", json);

        var tuples = result.ToTuples();
        Assert.Equal(result.Candidates.Count, tuples.Count);
        Assert.All(tuples, tuple => Assert.NotEmpty(tuple.Evidence));
    }
}
