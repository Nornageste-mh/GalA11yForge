using System.Text;
using GalA11yForge.Build;

namespace GalA11yForge.Build.Tests;

public sealed class LoaderCompatProbeTests
{
    private static void WriteManagedFile(TempArea area, string fileName, string content)
    {
        var path = Path.Combine(area.GameDirectory, "game_Data", "Managed", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    [Fact]
    public void Probe_MscorlibWithoutGetPeKind_IsUnsupportedWithManualTodo()
    {
        using var area = new TempArea();
        WriteManagedFile(area, "mscorlib.dll", "this-runtime-has-no-pekind-member");
        WriteManagedFile(area, "UnityEngine.InputLegacyModule.dll", "anyKeyDown");

        var report = LoaderCompatProbe.Probe(area.GameDirectory);

        Assert.Equal(LoaderCompatProbe.VerdictUnsupported, report.Verdict);
        Assert.True(report.IsUnsupported);
        Assert.False(report.MscorlibHasGetPEKind);
        Assert.Contains(report.ManualTodos, todo => todo.Contains("GetPEKind", StringComparison.Ordinal));
    }

    [Fact]
    public void Probe_TrimmedInputModule_IsDegraded()
    {
        using var area = new TempArea();
        WriteManagedFile(area, "mscorlib.dll", "filled-with System.Reflection.Module.GetPEKind symbol");
        WriteManagedFile(area, "UnityEngine.InputLegacyModule.dll", "only anyKey remains");

        var report = LoaderCompatProbe.Probe(area.GameDirectory);

        Assert.Equal(LoaderCompatProbe.VerdictDegraded, report.Verdict);
        Assert.True(report.IsDegraded);
        Assert.True(report.MscorlibHasGetPEKind);
        Assert.False(report.InputHasAnyKeyDown);
        Assert.Contains(report.ManualTodos, todo => todo.Contains("anyKeyDown", StringComparison.Ordinal));
    }

    [Fact]
    public void Probe_HealthyMonoGame_IsSupported()
    {
        using var area = new TempArea();
        WriteManagedFile(area, "mscorlib.dll", "System.Reflection.Module.GetPEKind");
        WriteManagedFile(area, "UnityEngine.InputLegacyModule.dll", "get_anyKeyDown anyKey");

        var report = LoaderCompatProbe.Probe(area.GameDirectory);

        Assert.Equal(LoaderCompatProbe.VerdictSupported, report.Verdict);
        Assert.True(report.IsSupported);
        Assert.True(report.MscorlibHasGetPEKind);
        Assert.True(report.InputHasAnyKeyDown);
        Assert.NotNull(report.DataDirectory);
    }

    [Fact]
    public void Probe_MissingUnityLayout_IsUnknown()
    {
        using var area = new TempArea();

        var report = LoaderCompatProbe.Probe(area.GameDirectory);

        Assert.Equal(LoaderCompatProbe.VerdictUnknown, report.Verdict);
        Assert.NotEmpty(report.Evidence);
    }

    [Fact]
    public void Probe_NonExistentDirectory_IsUnknownNotThrow()
    {
        var report = LoaderCompatProbe.Probe(Path.Combine(Path.GetTempPath(), "gaforge-missing-" + Guid.NewGuid().ToString("N")));

        Assert.Equal(LoaderCompatProbe.VerdictUnknown, report.Verdict);
    }
}
