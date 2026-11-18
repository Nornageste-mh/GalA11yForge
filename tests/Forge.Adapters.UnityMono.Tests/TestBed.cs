using System.Security.Cryptography;
using GalA11yForge.Core.Abstractions;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Adapters.UnityMono.Tests;

/// <summary>每个测试一份隔离的临时游戏目录 + 工作区，结束后清理。</summary>
internal sealed class TestBed : IDisposable
{
    public TestBed()
    {
        Root = Path.Combine(Path.GetTempPath(), "galai11y-w2h-" + Guid.NewGuid().ToString("N"));
        Workspace = Path.Combine(Root, "workspace");
        GameDirectory = Path.Combine(Root, "game");
        DataDirectory = Path.Combine(GameDirectory, "FakeGame_Data");
        ManagedDirectory = Path.Combine(DataDirectory, "Managed");
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(GameDirectory);
    }

    public string Root { get; }

    public string Workspace { get; }

    public string GameDirectory { get; }

    public string DataDirectory { get; }

    public string ManagedDirectory { get; }

    /// <summary>把测试程序集复制成假 Assembly-CSharp.dll；其中的 FakeGame.UIText 提供 4 个入口。</summary>
    public string InstallTestAssemblyAsGame()
    {
        Directory.CreateDirectory(ManagedDirectory);
        var destination = Path.Combine(ManagedDirectory, "Assembly-CSharp.dll");
        File.Copy(typeof(TestBed).Assembly.Location, destination, overwrite: true);
        return destination;
    }

    public void WriteBadAssembly()
    {
        Directory.CreateDirectory(ManagedDirectory);
        File.WriteAllBytes(Path.Combine(ManagedDirectory, "Assembly-CSharp.dll"), new byte[] { 0x4d, 0x5a, 0x00, 0x01, 0x02, 0x03 });
    }

    public void WriteEmptyAssembly()
    {
        Directory.CreateDirectory(ManagedDirectory);
        File.WriteAllBytes(Path.Combine(ManagedDirectory, "Assembly-CSharp.dll"), Array.Empty<byte>());
    }

    public GameContext Context(string slug = "fake-game", ProfileDocument? profile = null)
    {
        var context = GameContext.Create(GameDirectory, Workspace, slug);
        context.Profile = profile;
        return context;
    }

    public static EngineDetection UnityMonoDetection() => new()
    {
        Family = UnityMonoAdapter.EngineId,
        Version = "6000.0.49f1",
        Bitness = 64,
        Confidence = 1.0,
        Evidence = new List<string> { "tests: injected detection" },
    };

    public static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响断言。
        }
    }
}

/// <summary>记录调用参数的假生成器，用于验证桥接层的委托与路径校验。</summary>
internal sealed class RecordingGenerator : ICodeGenerator
{
    public int Calls { get; private set; }

    public string? LastOutput { get; private set; }

    public GameContext? LastContext { get; private set; }

    public PatchPlan? LastPlan { get; private set; }

    public Task<ForgeResult<GenerationResult>> GenerateAsync(
        GameContext context,
        PatchPlan plan,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        LastContext = context;
        LastPlan = plan;
        LastOutput = outputDirectory;
        return Task.FromResult(ForgeResult<GenerationResult>.Success(new GenerationResult
        {
            ProjectDirectory = outputDirectory,
            Artifacts = new List<GeneratedArtifact>(),
            Metadata = new Dictionary<string, string> { ["fake"] = "true" },
        }));
    }
}

internal sealed class ThrowingGenerator : ICodeGenerator
{
    public Task<ForgeResult<GenerationResult>> GenerateAsync(
        GameContext context,
        PatchPlan plan,
        string outputDirectory,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("boom from fake generator");
}

/// <summary>真实游戏路径；不存在时测试应 Skip（xunit 2.x 由 Attribute 设置 Skip）。</summary>
internal static class RealGamePaths
{
    public const string GameDirectory = @"D:\Steam\steamapps\common\Build";

    public static string AssemblyPath =>
        Path.Combine(GameDirectory, "5omeday_Data", "Managed", "Assembly-CSharp.dll");

    public static bool Exists => Directory.Exists(GameDirectory) && File.Exists(AssemblyPath);
}

internal sealed class RealGameFactAttribute : FactAttribute
{
    public RealGameFactAttribute()
    {
        if (!RealGamePaths.Exists)
        {
            Skip = "未实测：真实游戏不存在（" + RealGamePaths.GameDirectory + "）";
        }
    }
}
