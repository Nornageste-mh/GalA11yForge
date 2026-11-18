namespace GalA11yForge.Detection.Internal;

/// <summary>
/// 只读读取器：绝不修改文件、不占用写锁。
/// 读不到（权限/占用/不支持）返回 false，由调用方按「未命中 + unverified」处理，不抛异常。
/// </summary>
internal static class SafeFileReader
{
    public static bool TryReadPrefix(string path, int maxBytes, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (maxBytes <= 0 || string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 8192,
                FileOptions.SequentialScan);

            var length = (int)Math.Min(stream.Length, maxBytes);
            if (length <= 0)
            {
                return true;
            }

            var buffer = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = stream.Read(buffer, offset, length - offset);
                if (read <= 0)
                {
                    break;
                }

                offset += read;
            }

            if (offset != length)
            {
                Array.Resize(ref buffer, offset);
            }

            bytes = buffer;
            return true;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or NotSupportedException
                                       or ArgumentException
                                       or System.Security.SecurityException)
        {
            return false;
        }
    }
}
