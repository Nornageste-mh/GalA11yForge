using GalA11yForge.Adapters.UnityMono.Internal;

namespace GalA11yForge.Adapters.UnityMono.Tests;

public sealed class DiscoveryTargetTests
{
    [Fact]
    public void ChooseDataDirectory_PrefersRealGameOverLauncher()
    {
        var root = Path.Combine(Path.GetTempPath(), "gala11y-f3-" + Guid.NewGuid().ToString("N"));
        try
        {
            var launcherManaged = Path.Combine(root, "SevenDaysLauncher_Data", "Managed");
            Directory.CreateDirectory(launcherManaged);
            File.WriteAllBytes(Path.Combine(launcherManaged, "Assembly-CSharp.dll"), new byte[128]);
            File.WriteAllBytes(Path.Combine(root, "SevenDaysLauncher.exe"), Array.Empty<byte>());

            var gameDir = Path.Combine(root, "game");
            var gameManaged = Path.Combine(gameDir, "SevenDays_Data", "Managed");
            Directory.CreateDirectory(gameManaged);
            File.WriteAllBytes(Path.Combine(gameManaged, "Assembly-CSharp.dll"), new byte[3 * 1024 * 1024]);
            File.WriteAllBytes(Path.Combine(gameDir, "SevenDays.exe"), Array.Empty<byte>());

            var chosen = UnityMonoDiscovery.ChooseDataDirectory(root, new List<string>
            {
                Path.Combine(root, "SevenDaysLauncher_Data"),
                Path.Combine(gameDir, "SevenDays_Data"),
            });

            Assert.EndsWith("SevenDays_Data", chosen, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ChooseDataDirectory_KeepsRootGameWhenNoLauncherPresent()
    {
        var root = Path.Combine(Path.GetTempPath(), "gala11y-f3b-" + Guid.NewGuid().ToString("N"));
        try
        {
            var managed = Path.Combine(root, "5omeday_Data", "Managed");
            Directory.CreateDirectory(managed);
            File.WriteAllBytes(Path.Combine(managed, "Assembly-CSharp.dll"), new byte[2 * 1024 * 1024]);
            File.WriteAllBytes(Path.Combine(root, "5omeday.exe"), Array.Empty<byte>());

            var chosen = UnityMonoDiscovery.ChooseDataDirectory(root, new List<string>
            {
                Path.Combine(root, "5omeday_Data"),
            });

            Assert.EndsWith("5omeday_Data", chosen, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
