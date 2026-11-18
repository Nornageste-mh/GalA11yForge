using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Build;

/// <summary>
/// install-manifest 的结构与安全校验。回滚前必须先过这一关：
/// 路径不能逃逸游戏目录、备份必须落在工作区 backup/ 下、动作与哈希字段必须自洽。
/// </summary>
public static class InstallManifestValidation
{
    public const int SupportedSchemaVersion = 1;

    public static ForgeResult<bool> Validate(
        ForgeWorkspace workspace,
        InstallManifest? manifest,
        bool requireBackupFiles = true,
        bool requireHashes = true)
    {
        if (workspace is null)
        {
            return Fail(ForgeErrorCodes.ArgumentInvalid, "缺少工作区。", "检查调用参数。");
        }

        if (manifest is null)
        {
            return Fail(ForgeErrorCodes.ArtifactInvalid, "账本为空。", "重新部署或传入正确的 install-manifest.json。");
        }

        if (manifest.SchemaVersion != SupportedSchemaVersion)
        {
            return Fail(ForgeErrorCodes.ArtifactInvalid,
                $"账本 schemaVersion={manifest.SchemaVersion}，当前只支持 {SupportedSchemaVersion}。",
                "升级 GalA11y Forge，或使用对应版本的账本。");
        }

        if (!PathSafety.IsValidSlug(manifest.DeploymentId))
        {
            return Fail(ForgeErrorCodes.ArtifactInvalid,
                $"账本 deploymentId 非法：{manifest.DeploymentId}",
                "deploymentId 只能包含小写字母、数字、点、下划线和连字符。");
        }

        if (!PathSafety.IsValidSlug(manifest.GameKey))
        {
            return Fail(ForgeErrorCodes.ArtifactInvalid,
                $"账本 gameKey 非法：{manifest.GameKey}",
                "gameKey 应为游戏 slug。");
        }

        if (string.IsNullOrWhiteSpace(manifest.GamePath) || !Path.IsPathFullyQualified(manifest.GamePath))
        {
            return Fail(ForgeErrorCodes.ArtifactInvalid, "账本 gamePath 必须是绝对路径。", "重新部署生成账本。");
        }

        string gamePath;
        string backupRoot;
        try
        {
            gamePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(manifest.GamePath));
            if (string.IsNullOrWhiteSpace(manifest.BackupRoot) || !Path.IsPathFullyQualified(manifest.BackupRoot))
            {
                return Fail(ForgeErrorCodes.ArtifactInvalid, "账本 backupRoot 必须是绝对路径。", "重新部署生成账本。");
            }

            backupRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(manifest.BackupRoot));
            if (!PathSafety.IsUnder(workspace.BackupRoot, backupRoot) || PathGuard.SamePath(backupRoot, workspace.BackupRoot))
            {
                return Fail(ForgeErrorCodes.PathUnsafe,
                    $"账本 backupRoot 不在工作区 backup/ 的子目录内：{backupRoot}",
                    "账本可能被篡改；不要回滚来源不明的 manifest。");
            }

            workspace.EnsureBackupPath(backupRoot);
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<bool>.Failure(ex.Error);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Fail(ForgeErrorCodes.ArtifactInvalid, $"账本路径非法：{ex.Message}", "重新部署生成账本。");
        }

        if (!string.IsNullOrWhiteSpace(manifest.WorkspacePath)
            && !PathGuard.SamePath(manifest.WorkspacePath, workspace.RootPath))
        {
            return Fail(ForgeErrorCodes.PathUnsafe,
                $"账本属于其他工作区：{manifest.WorkspacePath}（当前 {workspace.RootPath}）",
                "用生成该账本时的工作区回滚，或恢复备份后重新部署。");
        }

        if (string.IsNullOrWhiteSpace(manifest.CreatedAt))
        {
            return Fail(ForgeErrorCodes.ArtifactInvalid, "账本缺少 createdAt。", "重新部署生成账本。");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files ?? new List<InstallFileEntry>())
        {
            if (file is null)
            {
                return Fail(ForgeErrorCodes.ArtifactInvalid, "账本 files 含 null 项。", "重新部署生成账本。");
            }

            string relative;
            try
            {
                relative = PathSafety.ValidateRelative(file.RelativePath);
                var target = PathSafety.ResolveUnder(gamePath, relative);
                PathGuard.EnsureNoReparsePoints(gamePath, target);
            }
            catch (ForgePathException ex)
            {
                return ForgeResult<bool>.Failure(ForgeError.Create(
                    ex.Error.Code,
                    $"账本文件路径校验失败：{ex.Error.Message}",
                    "账本可能被篡改；不要回滚来源不明的 manifest。"));
            }

            if (!seen.Add(relative))
            {
                return Fail(ForgeErrorCodes.ArtifactInvalid, $"账本文件路径重复：{relative}", "重新部署生成账本。");
            }

            if (!Enum.IsDefined(file.Action))
            {
                return Fail(ForgeErrorCodes.ArtifactInvalid, $"未知的部署动作：{file.Action}", "使用当前版本重新生成账本。");
            }

            if (file.SizeBefore < 0 || file.SizeAfter < 0)
            {
                return Fail(ForgeErrorCodes.ArtifactInvalid, $"文件大小不能为负：{relative}", "重新部署生成账本。");
            }

            if (file.Sha256Before is not null && !FileHashes.IsSha256(file.Sha256Before))
            {
                return Fail(ForgeErrorCodes.ArtifactInvalid, $"sha256Before 格式非法：{relative}", "重新部署生成账本。");
            }

            if (file.Sha256After is not null && !FileHashes.IsSha256(file.Sha256After))
            {
                return Fail(ForgeErrorCodes.ArtifactInvalid, $"sha256After 格式非法：{relative}", "重新部署生成账本。");
            }

            switch (file.Action)
            {
                case DeployFileAction.Add:
                    if (file.ExistedBefore)
                    {
                        return Fail(ForgeErrorCodes.ArtifactInvalid, $"Add 动作不应标记 existedBefore=true：{relative}", "重新部署生成账本。");
                    }

                    if (!string.IsNullOrWhiteSpace(file.BackupPath))
                    {
                        return Fail(ForgeErrorCodes.ArtifactInvalid, $"Add 动作不应有 backupPath：{relative}", "重新部署生成账本。");
                    }

                    if (requireHashes && string.IsNullOrWhiteSpace(file.Sha256After))
                    {
                        return Fail(ForgeErrorCodes.ArtifactInvalid, $"Add 动作缺少 sha256After：{relative}", "重新部署生成账本。");
                    }

                    break;

                case DeployFileAction.Replace:
                case DeployFileAction.Remove:
                    if (!file.ExistedBefore)
                    {
                        return Fail(ForgeErrorCodes.ArtifactInvalid,
                            $"{file.Action} 动作必须 existedBefore=true：{relative}",
                            "重新部署生成账本。");
                    }

                    if (string.IsNullOrWhiteSpace(file.BackupPath))
                    {
                        return Fail(ForgeErrorCodes.ArtifactMissing,
                            $"{file.Action} 动作缺少 backupPath：{relative}",
                            "没有备份就不能回滚；用备份重新部署。");
                    }

                    if (requireHashes && string.IsNullOrWhiteSpace(file.Sha256Before))
                    {
                        return Fail(ForgeErrorCodes.ArtifactInvalid, $"{file.Action} 动作缺少 sha256Before：{relative}", "重新部署生成账本。");
                    }

                    if (file.Action == DeployFileAction.Replace && requireHashes && string.IsNullOrWhiteSpace(file.Sha256After))
                    {
                        return Fail(ForgeErrorCodes.ArtifactInvalid, $"Replace 动作缺少 sha256After：{relative}", "重新部署生成账本。");
                    }

                    if (file.Action == DeployFileAction.Remove && !string.IsNullOrWhiteSpace(file.Sha256After))
                    {
                        return Fail(ForgeErrorCodes.ArtifactInvalid, $"Remove 动作不应有 sha256After：{relative}", "重新部署生成账本。");
                    }

                    break;

                default:
                    return Fail(ForgeErrorCodes.ArtifactInvalid, $"未知的部署动作：{file.Action}", "使用当前版本重新生成账本。");
            }

            if (!string.IsNullOrWhiteSpace(file.BackupPath))
            {
                string backupRelative;
                try
                {
                    backupRelative = PathSafety.ValidateRelative(file.BackupPath);
                }
                catch (ForgePathException ex)
                {
                    return Fail(ForgeErrorCodes.PathUnsafe,
                        $"账本 backupPath 非法：{file.BackupPath}（{ex.Error.Message}）",
                        "备份路径必须是 backupRoot 下的相对路径。");
                }

                string backupFull;
                try
                {
                    backupFull = workspace.EnsureBackupPath(
                        Path.Combine(backupRoot, backupRelative.Replace('/', Path.DirectorySeparatorChar)));
                }
                catch (ForgePathException ex)
                {
                    return ForgeResult<bool>.Failure(ex.Error);
                }

                if (requireBackupFiles && !File.Exists(backupFull))
                {
                    return Fail(ForgeErrorCodes.ArtifactMissing,
                        $"账本指向的备份文件不存在：{backupFull}",
                        "备份被移动或删除，无法安全回滚；请恢复备份目录后重试。");
                }
            }
        }

        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in manifest.CreatedDirectories ?? new List<string>())
        {
            try
            {
                var relative = PathSafety.ValidateRelative(directory);
                if (!directories.Add(relative))
                {
                    return Fail(ForgeErrorCodes.ArtifactInvalid, $"createdDirectories 重复：{relative}", "重新部署生成账本。");
                }
            }
            catch (ForgePathException ex)
            {
                return Fail(ForgeErrorCodes.PathUnsafe,
                    $"createdDirectories 含非法路径：{directory}（{ex.Error.Message}）",
                    "账本可能被篡改。");
            }
        }

        return ForgeResult<bool>.Success(true);
    }

    private static ForgeResult<bool> Fail(string code, string message, string? suggestion)
        => ForgeResult<bool>.Failure(ForgeError.Create(code, message, suggestion));
}
