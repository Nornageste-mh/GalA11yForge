using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Serialization;

namespace GalA11yForge.Core.Profiles;

/// <summary>profile.json / overrides.json 的读写。写入使用同目录临时文件再替换，避免半截 JSON。</summary>
public static class ProfileStore
{
    public static ForgeResult<ProfileDocument> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return ForgeResult<ProfileDocument>.Success(new ProfileDocument());
            }

            var json = File.ReadAllText(path);
            var document = ForgeJson.Deserialize<ProfileDocument>(json);
            if (document is null)
            {
                return ForgeResult<ProfileDocument>.Failure(ForgeError.Create(
                    ForgeErrorCodes.ProfileInvalid,
                    $"配置文件为空或不是 JSON 对象：{path}。",
                    "用 --workspace 检查目录，或删除该文件让工具重新生成。"));
            }

            return ProfileMerger.Validate(document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ForgeResult<ProfileDocument>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"读取配置失败：{path}：{ex.Message}",
                "检查文件权限后重试。"));
        }
        catch (System.Text.Json.JsonException ex)
        {
            return ForgeResult<ProfileDocument>.Failure(ForgeError.Create(
                ForgeErrorCodes.ProfileInvalid,
                $"配置文件不是合法 JSON：{path}：{ex.Message}",
                "修正 JSON 语法，或删除该文件使用默认配置。"));
        }
    }

    /// <summary>读取 profile.json 与 overrides.json 并合并；两者都不存在时返回默认文档。</summary>
    public static ForgeResult<ProfileDocument> LoadPair(string profilePath, string overridesPath)
    {
        var baseResult = Load(profilePath);
        if (!baseResult.Ok)
        {
            return baseResult;
        }

        var overrideResult = Load(overridesPath);
        if (!overrideResult.Ok)
        {
            return overrideResult;
        }

        return ForgeResult<ProfileDocument>.Success(ProfileMerger.Merge(baseResult.Value!, overrideResult.Value));
    }

    public static ForgeResult<bool> Save(string path, ProfileDocument document)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, ForgeJson.Serialize(document));
            File.Move(temp, path, overwrite: true);
            return ForgeResult<bool>.Success(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ForgeResult<bool>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"写入配置失败：{path}：{ex.Message}",
                "检查磁盘空间与文件权限后重试。"));
        }
    }
}
