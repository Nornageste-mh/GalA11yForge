using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Build.Tests;

/// <summary>测试只用临时目录：绝不触碰真实游戏目录。</summary>
internal sealed class TempArea : IDisposable
{
    public TempArea()
    {
        Root = Path.Combine(Path.GetTempPath(), "gaforge-tests", "build-" + Guid.NewGuid().ToString("N"));
        WorkspaceRoot = Path.Combine(Root, "workspace");
        GameDirectory = Path.Combine(Root, "game");
        Directory.CreateDirectory(WorkspaceRoot);
        Directory.CreateDirectory(GameDirectory);
    }

    public string Root { get; }

    public string WorkspaceRoot { get; }

    public string GameDirectory { get; }

    public ForgeWorkspace CreateWorkspace() => new(WorkspaceRoot, GameDirectory);

    public GameContext CreateContext(bool dryRun = false)
    {
        var context = GameContext.Create(GameDirectory, WorkspaceRoot, "sample-game");
        context.DryRun = dryRun;
        return context;
    }

    public string GameFile(string relativePath, string content)
    {
        var path = Path.Combine(GameDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public string WorkspaceFile(string relativePath, string content)
    {
        var path = Path.Combine(WorkspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public string GameText(string relativePath)
        => File.ReadAllText(Path.Combine(GameDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public bool GameExists(string relativePath)
        => File.Exists(Path.Combine(GameDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class TestArtifacts
{
    public static string DotnetPath
    {
        get
        {
            var local = @"D:\dotnet\dotnet.exe";
            return File.Exists(local) ? local : "dotnet";
        }
    }

    public static string CreateBepInExPackage(string directory, bool includeRequiredFiles = true)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "BepInEx_win_x64_5.4.23.5.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        if (includeRequiredFiles)
        {
            AddEntry(archive, "winhttp.dll", "fake-winhttp");
            AddEntry(archive, "doorstop_config.ini", "fake-doorstop");
            AddEntry(archive, "BepInEx/core/BepInEx.dll", "fake-bepinex-core");
            AddEntry(archive, "BepInEx/core/0Harmony.dll", "fake-harmony");
        }

        return path;
    }

    public static string CreateBepInExPackageWithMissingRequiredFile(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "BepInEx_win_x64_5.4.23.5.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddEntry(archive, "winhttp.dll", "fake-winhttp");
        AddEntry(archive, "BepInEx/core/BepInEx.dll", "fake-bepinex-core");
        return path;
    }

    public static string CreatePluginArtifact(string workspaceRoot, string bytes = "fake-plugin")
    {
        var directory = Path.Combine(workspaceRoot, "work", "build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "A11yPlugin.dll");
        File.Copy(typeof(ForgeWorkspace).Assembly.Location, path, overwrite: true);
        return path;
    }

    public static BuildResult CreateBuildResult(string pluginPath, params string[] extraArtifacts)
    {
        var artifacts = new List<string> { pluginPath };
        artifacts.AddRange(extraArtifacts);
        return new BuildResult
        {
            OutputDirectory = Path.GetDirectoryName(pluginPath)!,
            Artifacts = artifacts,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["version"] = "0.1.0.0",
            },
        };
    }

    public static string CreateProject(string workspaceRoot, bool invalidCode = false)
    {
        var projectDirectory = Path.Combine(workspaceRoot, "work", "generated");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, "SamplePlugin.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <AssemblyName>SamplePlugin</AssemblyName>
                <RootNamespace>SamplePlugin</RootNamespace>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(projectDirectory, "Plugin.cs"), invalidCode
            ? "public sealed class Plugin { this is not C# }"
            : "namespace SamplePlugin; public sealed class Plugin { public string Name => \"sample\"; }",
            new UTF8Encoding(false));
        return projectDirectory;
    }

    public static BuildOptions Options(TempArea area, string? dotnetPath = null)
        => new()
        {
            DotnetPath = dotnetPath ?? DotnetPath,
            Configuration = "Release",
            OutputDirectory = Path.Combine(area.WorkspaceRoot, "work", "build-output"),
            LogDirectory = Path.Combine(area.WorkspaceRoot, "logs"),
            Timeout = TimeSpan.FromMinutes(2),
        };

    public static InstallManifest Manifest(
        TempArea area,
        string backupDirectory,
        params InstallFileEntry[] entries)
    {
        var manifest = new InstallManifest
        {
            SchemaVersion = 1,
            DeploymentId = "dep-test-123",
            GameKey = "sample-game",
            GamePath = area.GameDirectory,
            WorkspacePath = area.WorkspaceRoot,
            BackupRoot = backupDirectory,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            Files = entries.ToList(),
        };
        return manifest;
    }

    public static InstallFileEntry AddEntry(string relativePath, string? sha256 = null)
        => new()
        {
            RelativePath = relativePath,
            Action = DeployFileAction.Add,
            ExistedBefore = false,
            Sha256After = sha256 ?? new string('a', 64),
        };

    public static InstallFileEntry ReplaceEntry(string relativePath, string? backupPath = null, string? shaBefore = null, string? shaAfter = null)
        => new()
        {
            RelativePath = relativePath,
            Action = DeployFileAction.Replace,
            ExistedBefore = true,
            BackupPath = backupPath ?? relativePath,
            Sha256Before = shaBefore ?? new string('b', 64),
            Sha256After = shaAfter ?? new string('c', 64),
        };

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}

internal sealed class FakeProcessRunner : IProcessRunner
{
    public ProcessResult Result { get; set; } = new() { ExitCode = 0 };

    public Exception? Throw { get; set; }

    public string? LastFileName { get; private set; }

    public IReadOnlyList<string>? LastArguments { get; private set; }

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        LastFileName = fileName;
        LastArguments = arguments;
        if (Throw is not null)
        {
            throw Throw;
        }

        return Task.FromResult(Result);
    }
}
