using System.Security.Cryptography;
using System.Text;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Serialization;
using GalA11yForge.Verify.Abstractions;
using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify.Tests;

internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "gaforge-verify-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Sub(string relative)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }
}

internal static class Hashes
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string Sha256(string content)
        => Convert.ToHexString(SHA256.HashData(Utf8NoBom.GetBytes(content))).ToLowerInvariant();

    public static long Size(string content) => Utf8NoBom.GetByteCount(content);
}

internal sealed class VerifyFixture : IDisposable
{
    public const string PluginRelativePath = "BepInEx/plugins/A11yPlugin.dll";
    public const string WinHttpRelativePath = "winhttp.dll";
    public const string DoorstopRelativePath = "doorstop_config.ini";

    public const string PluginContent = "plugin-binary-v1";
    public const string WinHttpContent = "winhttp-loader";
    public const string DoorstopOriginal = "[General]\nenabled=false\n";
    public const string DoorstopPatched = "[General]\nenabled=true\n";
    public const string DefaultLogContent =
        "[Message:   BepInEx] BepInEx 5.4.23.5\n" +
        "[Info   :   BepInEx] Loading [A11yPlugin 0.1.0.0]\n" +
        "[Message:   BepInEx] Chainloader startup complete\n";

    public VerifyFixture(bool standardInstall = true)
    {
        Temp = new TempFolder();
        GameDir = Temp.Sub("game");
        Workspace = Temp.Sub("ws");
        Slug = "sample-game";
        BackupRoot = Path.Combine(Workspace, "backup", "dep-001");
        ManifestPath = Path.Combine(Workspace, "games", Slug, "05-deploy", "deploy.json");
        BaselinePath = Path.Combine(Workspace, "games", Slug, "05-deploy", "baseline.json");

        if (standardInstall)
        {
            CreateStandardInstall();
        }
    }

    public TempFolder Temp { get; }

    public string GameDir { get; }

    public string Workspace { get; }

    public string Slug { get; }

    public string BackupRoot { get; }

    public string ManifestPath { get; }

    public string BaselinePath { get; }

    public DeployManifest Manifest { get; private set; } = new();

    public GameBaseline Baseline { get; private set; } = new();

    public string LogPath => Path.Combine(GameDir, "BepInEx", "LogOutput.log");

    public string PathOfGame(string relativePath)
        => Path.Combine(GameDir, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public void CreateStandardInstall()
    {
        WriteGame("game.exe", "fake-game-exe-v1");
        WriteGame("data/scenario.bin", "original-scenario-data");
        WriteGame("BepInEx/core/BepInEx.dll", "bepinex-core");
        WriteGame(DoorstopRelativePath, DoorstopPatched);
        WriteGame(WinHttpRelativePath, WinHttpContent);
        WriteGame(PluginRelativePath, PluginContent);
        WriteGame("BepInEx/LogOutput.log", DefaultLogContent);

        Directory.CreateDirectory(BackupRoot);
        System.IO.File.WriteAllText(
            Path.Combine(BackupRoot, DoorstopRelativePath),
            DoorstopOriginal,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Manifest = new DeployManifest
        {
            SchemaVersion = 1,
            DeploymentId = "dep-001",
            GamePath = GameDir,
            BackupRoot = BackupRoot,
            CreatedAt = "2026-11-14T00:00:00+08:00",
            Files = new List<DeployFileEntry>
            {
                new()
                {
                    RelativePath = PluginRelativePath,
                    Action = DeployFileAction.Add,
                    Sha256After = Hashes.Sha256(PluginContent),
                },
                new()
                {
                    RelativePath = WinHttpRelativePath,
                    Action = DeployFileAction.Add,
                    Sha256After = Hashes.Sha256(WinHttpContent),
                },
                new()
                {
                    RelativePath = DoorstopRelativePath,
                    Action = DeployFileAction.Replace,
                    ExistedBefore = true,
                    BackupPath = Path.Combine(BackupRoot, DoorstopRelativePath),
                    Sha256Before = Hashes.Sha256(DoorstopOriginal),
                    Sha256After = Hashes.Sha256(DoorstopPatched),
                },
            },
        };
        SaveManifest();

        Baseline = new GameBaseline
        {
            SchemaVersion = 1,
            GamePath = GameDir,
            CreatedAt = "2026-11-14T00:00:00+08:00",
            Files = new List<BaselineFileEntry>
            {
                Entry("game.exe", "fake-game-exe-v1"),
                Entry("data/scenario.bin", "original-scenario-data"),
                Entry(DoorstopRelativePath, DoorstopOriginal),
            },
        };
        SaveBaseline();
    }

    public void ApplyRollbackState()
    {
        System.IO.File.Delete(PathOfGame(PluginRelativePath));
        System.IO.File.Delete(PathOfGame(WinHttpRelativePath));
        WriteGame(DoorstopRelativePath, DoorstopOriginal);
    }

    public void WriteGame(string relativePath, string content)
    {
        var path = PathOfGame(relativePath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        System.IO.File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public void SetManifest(DeployManifest manifest)
    {
        Manifest = manifest;
    }

    public void SetBaseline(GameBaseline baseline)
    {
        Baseline = baseline;
    }

    public void SaveManifest()
    {
        var directory = Path.GetDirectoryName(ManifestPath);        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        System.IO.File.WriteAllText(
            ManifestPath,
            ForgeJson.Serialize(Manifest),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public void SaveBaseline()
    {
        var directory = Path.GetDirectoryName(BaselinePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        System.IO.File.WriteAllText(
            BaselinePath,
            ForgeJson.Serialize(Baseline),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public VerifyOptions Options(
        VerifyMode mode = VerifyMode.Install,
        bool probe = false,
        IEnvironmentProbe? environmentProbe = null)
        => new()
        {
            GameDirectory = GameDir,
            WorkspaceRoot = Workspace,
            Slug = Slug,
            Mode = mode,
            ProbeEnvironment = probe,
            EnvironmentProbe = environmentProbe,
        };

    public Task<ForgeResult<VerifyReport>> RunAsync(VerifyOptions? options = null)
        => new VerificationService().VerifyAsync(options ?? Options());

    public void Dispose() => Temp.Dispose();

    private static BaselineFileEntry Entry(string relativePath, string content)
        => new()
        {
            RelativePath = relativePath,
            Size = Hashes.Size(content),
            Sha256 = Hashes.Sha256(content),
        };
}

internal sealed class FakeEnvironmentProbe : IEnvironmentProbe
{
    private readonly EnvironmentSnapshot _snapshot;

    public FakeEnvironmentProbe(EnvironmentSnapshot snapshot) => _snapshot = snapshot;

    public Task<EnvironmentSnapshot> ProbeAsync(string gameDirectory, CancellationToken cancellationToken = default)
        => Task.FromResult(_snapshot);

    public static EnvironmentSnapshot AllAvailable() => new()
    {
        CapturedAt = "2026-11-14T00:00:00+08:00",
        Backends = new List<VoiceBackendFact>
        {
            Backend("tolk", "Tolk", true, "Tolk.dll 位于测试目录"),
            Backend("nvda", "NVDA", true, "NVDA 进程运行中，控制器 DLL 就位"),
            Backend("zdsr", "争渡", true, "争渡进程运行中"),
            Backend("sapi", "SAPI5", true, "注册表中有 2 个语音 token"),
        },
        Notes = new List<string> { "测试用环境快照，不代表真实出声。" },
    };

    public static EnvironmentSnapshot None() => new()
    {
        CapturedAt = "2026-11-14T00:00:00+08:00",
        Backends = new List<VoiceBackendFact>
        {
            Backend("tolk", "Tolk", false, "未找到 Tolk.dll"),
            Backend("nvda", "NVDA", false, "未安装/未运行"),
            Backend("zdsr", "争渡", false, "未安装/未运行"),
            Backend("sapi", "SAPI5", false, "注册表无语音 token"),
        },
        Notes = new List<string> { "测试用空环境快照。" },
    };

    private static VoiceBackendFact Backend(string id, string name, bool available, string fact)
        => new()
        {
            Id = id,
            DisplayName = name,
            Available = available,
            Fact = fact,
            Evidence = new List<string>(),
        };
}

internal sealed class ThrowingEnvironmentProbe : IEnvironmentProbe
{
    public Task<EnvironmentSnapshot> ProbeAsync(string gameDirectory, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("测试用探测失败");
}

internal static class ReportAssert
{
    public static VerifyCheck Check(VerifyReport report, string id)
    {
        var matches = report.Checks.Where(check => check.Id == id).ToList();
        Assert.Single(matches);
        return matches[0];
    }

    public static void HasStatus(VerifyReport report, string id, VerifyCheckStatus status)
        => Assert.Equal(status, Check(report, id).Status);
}
