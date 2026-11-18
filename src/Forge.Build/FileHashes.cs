using System.Security.Cryptography;

namespace GalA11yForge.Build;

/// <summary>SHA-256 小工具。账本中的哈希统一写成大写十六进制，比较时忽略大小写。</summary>
public static class FileHashes
{
    public static string Sha256File(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Sha256Stream(stream);
    }

    public static string Sha256Bytes(byte[] bytes) => Sha256Bytes(bytes.AsSpan());

    public static string Sha256Bytes(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static string Sha256Stream(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));

    public static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    public static bool EqualsHash(string? left, string? right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    public static bool MatchesFile(string path, string? expected)
        => File.Exists(path) && EqualsHash(Sha256File(path), expected);
}
