namespace Forge.E2E;

/// <summary>
/// 一次 E2E 测试的隔离环境：临时根目录 + 工作区 + 输出目录。
/// 默认不自动清理，失败时可以直接打开目录看现场。
/// </summary>
internal sealed class E2ESandbox
{
    private static readonly Lazy<string> RepoRootLazy = new(ResolveRepoRoot);
    private static readonly Lazy<string> FixtureRootLazy = new(ResolveFixtureRoot);

    private E2ESandbox(string root)
    {
        Root = root;
        Workspace = Path.Combine(root, "workspace");
        OutRoot = Path.Combine(root, "out");
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(OutRoot);
    }

    public static string RepoRoot => RepoRootLazy.Value;

    /// <summary>假 Unity Mono 游戏夹具目录（tests/fixtures/fake-game-unity-mono）。</summary>
    public static string FixtureRoot => FixtureRootLazy.Value;

    public static string DotnetPath { get; } = ResolveDotnet();

    public string Root { get; }

    /// <summary>传给 --workspace 的临时工作区。</summary>
    public string Workspace { get; }

    /// <summary>传给 --out 的临时输出根；一条流水线内所有命令共用一个，避免产物散落。</summary>
    public string OutRoot { get; }

    public static E2ESandbox Create(string name)
    {
        var safeName = new string(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());
        var root = Path.Combine(
            Path.GetTempPath(),
            "forge-e2e",
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new E2ESandbox(root);
    }

    /// <summary>把夹具游戏整体复制到 sandbox，install/rollback 只动这份副本。</summary>
    public string CopyFixtureGame(string name = "game")
    {
        var target = Path.Combine(Root, name);
        CopyDirectory(FixtureRoot, target);
        return target;
    }

    public string EmptyGameDirectory(string name = "empty-game")
    {
        var target = Path.Combine(Root, name);
        Directory.CreateDirectory(target);
        return target;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }

    private static string ResolveDotnet()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("FORGE_DOTNET");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        return File.Exists(@"D:\dotnet\dotnet.exe") ? @"D:\dotnet\dotnet.exe" : "dotnet";
    }

    private static string ResolveRepoRoot()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("FORGE_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            var full = Path.GetFullPath(fromEnvironment);
            if (File.Exists(Path.Combine(full, "src", "GalA11yForge.sln")))
            {
                return full;
            }

            throw new InvalidOperationException(
                $"FORGE_REPO_ROOT={full} 下找不到 src/GalA11yForge.sln，请指向 GalA11y Forge 仓库根目录。");
        }

        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "GalA11yForge.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException(
            $"找不到仓库根（自 {AppContext.BaseDirectory} 与 {Environment.CurrentDirectory} 向上都没有 src/GalA11yForge.sln）。"
            + "请设置 FORGE_REPO_ROOT 指向仓库根目录。");
    }

    private static string ResolveFixtureRoot()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("FORGE_FIXTURE_DIR");
        var root = !string.IsNullOrWhiteSpace(fromEnvironment)
            ? Path.GetFullPath(fromEnvironment)
            : Path.Combine(RepoRoot, "tests", "fixtures", "fake-game-unity-mono");

        var required = new[]
        {
            Path.Combine(root, "5omeday.exe"),
            Path.Combine(root, "5omeday_Data", "Managed", "Assembly-CSharp.dll"),
            Path.Combine(root, "5omeday_Data", "globalgamemanagers"),
            Path.Combine(root, "UnityPlayer.dll"),
            Path.Combine(root, "MonoBleedingEdge"),
        };

        var missing = required.Where(path => !File.Exists(path) && !Directory.Exists(path)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"假游戏夹具不完整：{root}\n缺少：{string.Join("、", missing)}\n"
                + "请先运行：pwsh -File tests/fixtures/make-fixture.ps1（需要 dotnet 8）。");
        }

        return root;
    }
}
