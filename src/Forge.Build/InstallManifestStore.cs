using System.Text;
using System.Text.Json;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Serialization;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Build;

/// <summary>
/// install-manifest.json 的读写。写盘用同目录临时文件 + 原子替换，UTF-8 无 BOM；
/// 读取后立即做结构校验，避免拿半截或越界账本回滚。
/// </summary>
public static class InstallManifestStore
{
    public const string FileName = "install-manifest.json";

    public static string DefaultPath(ForgeWorkspace workspace, string slug)
        => Path.Combine(workspace.StageDirectory(slug, PipelineStage.Deploy), FileName);

    public static ForgeResult<string> Save(ForgeWorkspace workspace, InstallManifest manifest, string? path = null)
    {
        if (workspace is null)
        {
            return ForgeResult<string>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid, "缺少工作区。", "检查调用参数。"));
        }

        var validation = InstallManifestValidation.Validate(workspace, manifest, requireBackupFiles: true);
        if (!validation.Ok)
        {
            return ForgeResult<string>.Failure(validation.Error!);
        }

        string fullPath;
        try
        {
            fullPath = ResolvePath(workspace, path, manifest.GameKey);
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<string>.Failure(ex.Error);
        }

        var json = ForgeJson.Serialize(manifest);
        var directory = Path.GetDirectoryName(fullPath)!;
        var temporary = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, fullPath, overwrite: true);
            return ForgeResult<string>.Success(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            return ForgeResult<string>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"写入部署账本失败：{ex.Message}",
                "检查工作区磁盘空间与目录权限。",
                new Dictionary<string, string> { ["path"] = fullPath }));
        }
    }

    public static ForgeResult<InstallManifest> Load(ForgeWorkspace workspace, string path)
    {
        if (workspace is null)
        {
            return ForgeResult<InstallManifest>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid, "缺少工作区。", "检查调用参数。"));
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
            if (!PathSafety.IsUnder(workspace.RootPath, fullPath) || PathGuard.SamePath(fullPath, workspace.RootPath))
            {
                return ForgeResult<InstallManifest>.Failure(ForgeError.Create(
                    ForgeErrorCodes.PathUnsafe,
                    $"账本不在工作区内：{fullPath}",
                    $"把 install-manifest.json 放在工作区 {workspace.RootPath} 内，或使用 --manifest 指定正确路径。"));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ForgeResult<InstallManifest>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid, $"账本路径非法：{ex.Message}", "检查 --manifest 参数。"));
        }

        if (!File.Exists(fullPath))
        {
            return ForgeResult<InstallManifest>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArtifactMissing,
                $"部署账本不存在：{fullPath}",
                "先完成一次部署，或用 --manifest 指向正确的 install-manifest.json。"));
        }

        InstallManifest? manifest;
        try
        {
            var json = File.ReadAllText(fullPath, Encoding.UTF8);
            manifest = ForgeJson.Deserialize<InstallManifest>(json);
        }
        catch (JsonException ex)
        {
            return ForgeResult<InstallManifest>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArtifactInvalid,
                $"部署账本不是有效 JSON：{ex.Message}",
                "账本可能损坏；不要手工拼接，重新部署或从备份恢复。",
                new Dictionary<string, string> { ["path"] = fullPath }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ForgeResult<InstallManifest>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"读取部署账本失败：{ex.Message}",
                "检查文件权限。",
                new Dictionary<string, string> { ["path"] = fullPath }));
        }

        if (manifest is null)
        {
            return ForgeResult<InstallManifest>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArtifactInvalid, "部署账本内容为空。", "重新部署生成账本。"));
        }

        var validation = InstallManifestValidation.Validate(workspace, manifest, requireBackupFiles: true);
        return validation.Ok
            ? ForgeResult<InstallManifest>.Success(manifest)
            : ForgeResult<InstallManifest>.Failure(validation.Error!);
    }

    public static string ResolvePath(ForgeWorkspace workspace, string? path, string slug)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return DefaultPath(workspace, slug);
        }

        return Path.IsPathFullyQualified(path)
            ? workspace.EnsureWriteAllowed(Path.GetFullPath(path))
            : workspace.ResolveWritePath(path);
    }

    public static bool HasBom(string path)
    {
        using var stream = File.OpenRead(path);
        return stream.ReadByte() == 0xEF
               && stream.ReadByte() == 0xBB
               && stream.ReadByte() == 0xBF;
    }

    /// <summary>
    /// 回滚成功后把账本标记为“已回滚”。回滚会消耗备份文件，不能走 Save 的完整性校验，
    /// 所以这里按同一份 JSON 约定直接重写账本；写失败只返回错误，不改变回滚结果。
    /// </summary>
    public static ForgeResult<string> MarkRolledBack(ForgeWorkspace workspace, InstallManifest manifest, string path)
    {
        if (workspace is null || manifest is null)
        {
            return ForgeResult<string>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid, "缺少工作区或账本。", "检查调用参数。"));
        }

        manifest.RolledBack = true;
        manifest.RolledBackAt = DateTimeOffset.Now.ToString("O");

        string fullPath;
        try
        {
            fullPath = ResolvePath(workspace, path, manifest.GameKey);
        }
        catch (ForgePathException ex)
        {
            return ForgeResult<string>.Failure(ex.Error);
        }

        try
        {
            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporary, ForgeJson.Serialize(manifest), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, fullPath, overwrite: true);
            return ForgeResult<string>.Success(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ForgeResult<string>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"回滚成功，但标记账本“已回滚”失败：{ex.Message}",
                "账本仍是旧状态，下次安装可能仍要求 --force；可直接删除账本文件后重试。",
                new Dictionary<string, string> { ["path"] = fullPath }));
        }
    }

    /// <summary>账本存在且已被标记为“已回滚”（即补丁已卸载、可以重新安装而不必 --force）。</summary>
    public static bool IsRolledBack(ForgeWorkspace workspace, string path)
    {
        if (workspace is null || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        var loaded = Load(workspace, path);
        return loaded.Ok && loaded.Value is { RolledBack: true };
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时文件清理失败不改变主错误。
        }
    }
}
