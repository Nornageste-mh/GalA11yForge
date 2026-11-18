namespace GalA11yForge.Analyzers.Ghidra.Tests;

/// <summary>构造一个「看起来像 Ghidra 安装目录」的假目录：support/analyzeHeadless.bat + Ghidra/application.properties。</summary>
internal static class FakeGhidra
{
    public static string CreateHome(string parentDirectory, string version = "12.1.4")
    {
        var home = Path.Combine(parentDirectory, $"ghidra_{version}_PUBLIC");
        Directory.CreateDirectory(Path.Combine(home, "support"));
        File.WriteAllText(
            Path.Combine(home, "support", "analyzeHeadless.bat"),
            "@echo off\r\nexit /b 0\r\n");

        var meta = Path.Combine(home, "Ghidra");
        Directory.CreateDirectory(meta);
        File.WriteAllText(
            Path.Combine(meta, "application.properties"),
            $"application.version={version}\napplication.java.min=21\n");

        return home;
    }
}
