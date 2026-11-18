using System.Security.Cryptography;

namespace GalA11yForge.Verify.Internal;

/// <summary>一个文件的只读事实。Sha256 为空且 Error 非空时表示存在但读不了。</summary>
internal readonly record struct FileProbe(bool Exists, long Size, string? Sha256, string? Error);

internal static class FileFacts
{
    public const string EmptyFileSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public static bool IsValidSha256(string? value)
        => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    public static string? NormalizeSha256(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// 只读探测：存在性、大小、SHA-256。共享读写与删除，避免锁住正在写的日志。
    /// </summary>
    public static async Task<FileProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new FileProbe(false, 0, null, null);
            }

            var info = new FileInfo(path);
            var size = info.Length;

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return new FileProbe(true, size, Convert.ToHexString(hash).ToLowerInvariant(), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var size = 0L;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception)
            {
                // 保留 0；错误信息已经足够定位问题。
            }

            return new FileProbe(true, size, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 同步只读探测，供同步部署流程（安装前基线）使用；语义与 <see cref="ProbeAsync"/> 一致。
    /// </summary>
    public static FileProbe Probe(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new FileProbe(false, 0, null, null);
            }

            var info = new FileInfo(path);
            var size = info.Length;

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                FileOptions.SequentialScan);

            var hash = SHA256.HashData(stream);
            return new FileProbe(true, size, Convert.ToHexString(hash).ToLowerInvariant(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var size = 0L;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception)
            {
                // 保留 0；错误信息已经足够定位问题。
            }

            return new FileProbe(true, size, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static string? TryGetDirectory(string path)
    {
        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
