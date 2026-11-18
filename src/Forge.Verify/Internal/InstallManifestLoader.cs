using System.Text;
using System.Text.Json;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Serialization;

namespace GalA11yForge.Verify.Internal;

internal sealed class ManifestLoadResult
{
    public DeployManifest? Manifest { get; init; }

    public string? Path { get; init; }

    public string? Error { get; init; }
}

/// <summary>按工作区约定查找并只读加载 install-manifest。</summary>
internal static class InstallManifestLoader
{
    public static async Task<ManifestLoadResult> LoadAsync(
        DeployManifest? inlineManifest,
        string? explicitPath,
        string workspaceRoot,
        string slug,
        CancellationToken cancellationToken)
    {
        if (inlineManifest is not null)
        {
            return new ManifestLoadResult { Manifest = inlineManifest, Path = explicitPath };
        }

        string? path;
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            path = explicitPath;
            if (!File.Exists(path))
            {
                return new ManifestLoadResult
                {
                    Path = path,
                    Error = $"显式指定的安装账本不存在：{path}",
                };
            }
        }
        else
        {
            var candidates = DefaultCandidates(workspaceRoot, slug);
            path = candidates.FirstOrDefault(File.Exists);
            if (path is null)
            {
                return new ManifestLoadResult
                {
                    Error = $"未找到安装账本。已查找：{string.Join("、", candidates)}",
                };
            }
        }

        try
        {
            string json;
            await using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            var manifest = ForgeJson.Deserialize<DeployManifest>(json);
            if (manifest is null)
            {
                return new ManifestLoadResult
                {
                    Path = path,
                    Error = $"安装账本内容为空：{path}",
                };
            }

            NormalizeBackupPaths(manifest);
            return new ManifestLoadResult { Manifest = manifest, Path = path };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            return new ManifestLoadResult
            {
                Path = path,
                Error = $"安装账本 JSON 解析失败：{ex.Message}",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new ManifestLoadResult
            {
                Path = path,
                Error = $"读取安装账本失败：{ex.Message}",
            };
        }
    }

    /// <summary>
    /// install-manifest.json 里的 backupPath 相对 backupRoot 记录（docs/09 账本格式）；
    /// deploy.json 则记录绝对路径。这里统一解析成绝对路径，交给 k-01..k-03 做越界校验。
    /// </summary>
    private static void NormalizeBackupPaths(DeployManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.BackupRoot))
        {
            return;
        }

        string backupRoot;
        try
        {
            backupRoot = Path.GetFullPath(manifest.BackupRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        foreach (var file in manifest.Files ?? new List<DeployFileEntry>())
        {
            if (file is null
                || string.IsNullOrWhiteSpace(file.BackupPath)
                || Path.IsPathRooted(file.BackupPath))
            {
                continue;
            }

            try
            {
                file.BackupPath = Path.GetFullPath(Path.Combine(
                    backupRoot,
                    file.BackupPath.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // 保留原值，让 BackupChecks 报“backupPath 不是绝对路径”，而不是静默吞掉。
            }
        }
    }

    public static IReadOnlyList<string> DefaultCandidates(string workspaceRoot, string slug)
        => new[]
        {
            Path.Combine(workspaceRoot, "games", slug, "05-deploy", "deploy.json"),
            Path.Combine(workspaceRoot, "games", slug, "05-deploy", "install-manifest.json"),
            Path.Combine(workspaceRoot, "games", slug, "06-verify", "install-manifest.json"),
        };
}
