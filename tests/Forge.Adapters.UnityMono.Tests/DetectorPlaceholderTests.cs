using GalA11yForge.Detection;
using GalA11yForge.Core.Models;
using Xunit;

namespace Forge.Adapters.UnityMono.Tests;

/// <summary>0.2.71：占位/空文件命中 R01 时的降级校验。</summary>
public sealed class DetectorPlaceholderTests
{
    [Fact]
    public void PlaceholderBinaries_DowngradeConfidence_AndExplain()
    {
        var dir = CreateGame(2, 2);
        try
        {
            var detection = new EngineDetection { Family = "unity_mono", Confidence = 0.95 };
            PlaceholderBinaryCheck.Apply(dir, detection);

            Assert.Equal(0.6, detection.Confidence, 3);
            Assert.Contains(detection.Evidence, line => line.Contains("PE 大小校验降级", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RealisticBinaries_KeepConfidence()
    {
        var dir = CreateGame(1_000_000, 200_000);
        try
        {
            var detection = new EngineDetection { Family = "unity_mono", Confidence = 0.95 };
            PlaceholderBinaryCheck.Apply(dir, detection);

            Assert.Equal(0.95, detection.Confidence, 3);
            Assert.DoesNotContain(detection.Evidence, line => line.Contains("PE 大小校验降级", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string CreateGame(long unityPlayerBytes, long assemblyBytes)
    {
        var dir = Path.Combine(Path.GetTempPath(), "forge-detector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "UnityPlayer.dll"), new byte[unityPlayerBytes]);
        var managed = Path.Combine(dir, "FakeGame_Data", "Managed");
        Directory.CreateDirectory(managed);
        File.WriteAllBytes(Path.Combine(managed, "Assembly-CSharp.dll"), new byte[assemblyBytes]);
        return dir;
    }
}
