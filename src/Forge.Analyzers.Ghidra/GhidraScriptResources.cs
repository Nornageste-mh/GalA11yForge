using System.Security.Cryptography;
using System.Text;

namespace GalA11yForge.Analyzers.Ghidra;

/// <summary>
/// 嵌入脚本的释放与完整性校验。脚本原样来自 research/10-ghidra-re/evidence/scripts/，
/// 释放时按 SHA-256 比对：内容相同就复用，不同才覆盖（避免用旧脚本跑出不一致的产物）。
/// </summary>
public static class GhidraScriptResources
{
    public const string CandidateHookDumpFileName = "CandidateHookDump.java";

    internal const string CandidateHookDumpResourceName =
        "GalA11yForge.Analyzers.Ghidra.Assets.CandidateHookDump.java";

    private static readonly Lazy<string> EmbeddedSha256Lazy = new(ComputeEmbeddedSha256);

    /// <summary>嵌入脚本的 SHA-256（小写十六进制），可写进报告作为「用的是哪份脚本」的证据。</summary>
    public static string EmbeddedSha256 => EmbeddedSha256Lazy.Value;

    /// <summary>把 CandidateHookDump.java 释放到 directory，返回脚本绝对路径。</summary>
    public static string ExtractCandidateHookDump(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, CandidateHookDumpFileName);
        var expected = EmbeddedSha256;

        if (File.Exists(target) && ComputeFileSha256(target) == expected)
        {
            return target;
        }

        using var stream = typeof(GhidraScriptResources).Assembly
            .GetManifestResourceStream(CandidateHookDumpResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"嵌入资源缺失：{CandidateHookDumpResourceName}。这是构建配置错误，不是用户环境问题。");
        }

        using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            stream.CopyTo(file);
        }

        var actual = ComputeFileSha256(target);
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"脚本释放后校验失败：期望 {expected}，实际 {actual}（{target}）。");
        }

        return target;
    }

    public static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ComputeEmbeddedSha256()
    {
        using var stream = typeof(GhidraScriptResources).Assembly
            .GetManifestResourceStream(CandidateHookDumpResourceName)
            ?? throw new InvalidOperationException($"嵌入资源缺失：{CandidateHookDumpResourceName}。");
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
