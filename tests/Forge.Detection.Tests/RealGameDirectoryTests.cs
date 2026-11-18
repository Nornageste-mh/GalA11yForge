using System.Security;

namespace GalA11yForge.Detection.Tests;

/// <summary>
/// 真实游戏目录的只读用例：目录不存在（或无权读取）时跳过而不是失败。
/// 这些目录属于本机 Steam 安装，测试只读取，不写入、不启动任何可执行文件。
/// </summary>
public class RealGameDirectoryTests
{
    private const string BuildDirectory = @"D:\Steam\steamapps\common\Build";
    private const string DdlcDirectory = @"D:\Steam\steamapps\common\Doki Doki Literature Club";
    private const string SenrenBankaDirectory = @"D:\Steam\steamapps\common\SenrenBanka";

    private static readonly EngineDetector Detector = new();

    [SkippableFact]
    public void Build_FiveNightsAtTheMansion_IsUnityMono()
    {
        Skip.IfNot(Directory.Exists(BuildDirectory), "本机不存在五天一夜目录，跳过真实目录用例。");

        var result = DetectOrSkip(BuildDirectory);

        Assert.Equal("unity_mono", result.Family);
        Assert.Equal(0.95, result.Confidence, precision: 6);
        Assert.Equal("6000.0.49f1", result.Primary.Version);
        Assert.Equal(64, result.Primary.Bitness);
        Assert.NotEmpty(result.Primary.Evidence);

        // 主结论必须来自 R01（Unity Mono 布局）；R26（BepInEx）只在游戏已装 BepInEx 时命中，
        // 这里不能把它当固定前提——游戏目录可能随时被用户/回归测试恢复成干净状态。
        var primary = result.Candidates.First(candidate => !candidate.IsSuppressed);
        Assert.Equal("R01", primary.RuleId);
    }

    [SkippableFact]
    public void Ddlc_IsRenpy_WithPythonVersion()
    {
        Skip.IfNot(Directory.Exists(DdlcDirectory), "本机不存在 DDLC 目录，跳过真实目录用例。");

        var result = DetectOrSkip(DdlcDirectory);

        Assert.Equal("renpy", result.Family);
        Assert.Equal(0.95, result.Confidence, precision: 6);
        Assert.Equal("6.99.12.4.2187", result.Primary.Version);
        Assert.Equal(32, result.Primary.Bitness);
        Assert.NotEmpty(result.Primary.Evidence);
    }

    [SkippableFact]
    public void SenrenBanka_IsKirikiriZ_WithKirikiri2Suppressed()
    {
        Skip.IfNot(Directory.Exists(SenrenBankaDirectory), "本机不存在 SenrenBanka 目录，跳过真实目录用例。");

        var result = DetectOrSkip(SenrenBankaDirectory);

        Assert.Equal("kirikiri_z", result.Family);
        Assert.Equal(0.90, result.Confidence, precision: 6);
        Assert.Equal(32, result.Primary.Bitness);
        // KKZ 的 .sig 指纹确认存在；版本没有静态来源，必须如实写 null 而不是编造。
        Assert.Null(result.Primary.Version);
        Assert.Contains(result.Primary.Evidence, line => line.Contains("unverified:version", StringComparison.Ordinal));

        var kirikiri2 = Assert.Single(result.Candidates.Where(candidate => candidate.Engine == "kirikiri_2"));
        Assert.True(kirikiri2.IsSuppressed);
        Assert.Empty(result.Primary.Evidence.Where(line => line.Length == 0));
    }

    [SkippableFact]
    public void SenrenBanka_Detection_DoesNotModifyGameFiles()
    {
        Skip.IfNot(Directory.Exists(SenrenBankaDirectory), "本机不存在 SenrenBanka 目录，跳过只读快照用例。");

        var before = Snapshot(SenrenBankaDirectory);
        _ = DetectOrSkip(SenrenBankaDirectory);
        var after = Snapshot(SenrenBankaDirectory);

        Assert.NotEmpty(after);
        Assert.Equal(before, after);
    }

    private static DetectionResult DetectOrSkip(string directory)
    {
        try
        {
            return Detector.Detect(directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            Skip.If(true, $"真实目录访问失败（{ex.GetType().Name}）：{ex.Message}");
            throw;
        }
    }

    /// <summary>目录快照：相对路径 + 长度 + 最后写入时间；跳过 reparse point，避免 junction 环。</summary>
    private static List<string> Snapshot(string root)
    {
        var entries = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    pending.Push(directory);
                }

                foreach (var file in Directory.EnumerateFiles(current))
                {
                    var info = new FileInfo(file);
                    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    entries.Add($"{relative}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                entries.Add($"skip|{current}");
            }
        }

        entries.Sort(StringComparer.Ordinal);
        return entries;
    }
}
