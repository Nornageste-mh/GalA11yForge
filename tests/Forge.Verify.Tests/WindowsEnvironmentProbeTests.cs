using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify.Tests;

public sealed class WindowsEnvironmentProbeTests
{
    [Fact]
    public async Task Probe_returns_four_backend_facts_without_throwing()
    {
        using var fixture = new VerifyFixture();

        var snapshot = await new WindowsEnvironmentProbe().ProbeAsync(fixture.GameDir);

        Assert.Equal(4, snapshot.Backends.Count);
        Assert.Contains(snapshot.Backends, backend => backend.Id == "tolk");
        Assert.Contains(snapshot.Backends, backend => backend.Id == "nvda");
        Assert.Contains(snapshot.Backends, backend => backend.Id == "zdsr");
        Assert.Contains(snapshot.Backends, backend => backend.Id == "sapi");
        Assert.All(snapshot.Backends, backend => Assert.False(string.IsNullOrWhiteSpace(backend.Fact)));
        Assert.InRange(snapshot.AvailableCount, 0, 4);
        Assert.Equal(4d, snapshot.Metrics["voiceBackends"]);
        Assert.Contains(snapshot.Notes, note => note.Contains("不", StringComparison.Ordinal));
    }
}
