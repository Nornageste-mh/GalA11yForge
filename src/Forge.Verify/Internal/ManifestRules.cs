using GalA11yForge.Core.Models;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Verify.Internal;

internal sealed class ManifestValidation
{
    public List<string> Issues { get; } = new();

    public bool IsValid => Issues.Count == 0;

    public string Detail => Issues.Count == 0 ? "账本结构完整。" : string.Join("；", Issues);
}

internal static class ManifestRules
{
    public const int SupportedSchemaVersion = 1;

    /// <summary>
    /// 校验账本结构。路径安全性由 m-03 单独负责；这里只判断字段是否存在、取值是否自洽。
    /// </summary>
    public static ManifestValidation Validate(DeployManifest manifest, string gameDirectory)
    {
        var validation = new ManifestValidation();

        if (manifest.SchemaVersion != SupportedSchemaVersion)
        {
            validation.Issues.Add($"schemaVersion={manifest.SchemaVersion}，当前只支持 {SupportedSchemaVersion}");
        }

        if (string.IsNullOrWhiteSpace(manifest.DeploymentId))
        {
            validation.Issues.Add("deploymentId 为空");
        }

        if (string.IsNullOrWhiteSpace(manifest.GamePath))
        {
            validation.Issues.Add("gamePath 为空");
        }
        else
        {
            try
            {
                var manifestGame = PathSafety.NormalizeRoot(manifest.GamePath);
                var actualGame = PathSafety.NormalizeRoot(gameDirectory);
                if (!string.Equals(manifestGame, actualGame, StringComparison.OrdinalIgnoreCase))
                {
                    validation.Issues.Add($"gamePath 与当前游戏目录不一致（账本：{manifestGame}）");
                }
            }
            catch (ForgePathException ex)
            {
                validation.Issues.Add($"gamePath 非法：{ex.Error.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(manifest.BackupRoot))
        {
            validation.Issues.Add("backupRoot 为空");
        }

        if (string.IsNullOrWhiteSpace(manifest.CreatedAt))
        {
            validation.Issues.Add("createdAt 为空");
        }

        if (manifest.Files is null || manifest.Files.Count == 0)
        {
            validation.Issues.Add("files 为空，无法核对任何部署内容");
        }
        else
        {
            for (var index = 0; index < manifest.Files.Count; index++)
            {
                var entry = manifest.Files[index];
                if (entry is null)
                {
                    validation.Issues.Add($"files[{index}] 为空条目");
                    continue;
                }

                if (!Enum.IsDefined(entry.Action))
                {
                    validation.Issues.Add($"files[{index}].action={entry.Action} 非法");
                }
            }
        }

        return validation;
    }

    public static string? NormalizeRelative(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        try
        {
            return PathSafety.ValidateRelative(relativePath);
        }
        catch (ForgePathException)
        {
            return null;
        }
    }

    public static bool TryResolveGamePath(
        string gameDirectory,
        string relativePath,
        out string fullPath,
        out string? error)
    {
        try
        {
            fullPath = PathSafety.ResolveUnder(gameDirectory, relativePath);
            error = null;
            return true;
        }
        catch (ForgePathException ex)
        {
            fullPath = string.Empty;
            error = ex.Error.Message;
            return false;
        }
    }

    public static string Summarize(IReadOnlyList<string> items, int limit = 8)
    {
        if (items.Count == 0)
        {
            return string.Empty;
        }

        if (items.Count <= limit)
        {
            return string.Join("；", items);
        }

        return string.Join("；", items.Take(limit)) + $"；等共 {items.Count} 项";
    }
}
