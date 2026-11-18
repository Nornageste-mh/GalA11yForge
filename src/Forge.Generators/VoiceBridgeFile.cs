namespace GalA11yForge.Generators;

/// <summary>
/// 一份语音桥源文件：平台层（L1）或契约层（L2）的原文，等待生成器替换命名空间。
///
/// <para>
/// 为什么不直接让生成器读 Framework 目录：生成器要能在没有仓库的机器上跑，
/// 也允许测试注入最小样本。文件内容必须保持「除命名空间声明外逐字节一致」，
/// 所以这里保留原文、BOM 状态与来源哈希，替换由生成器统一做。
/// </para>
/// </summary>
public sealed class VoiceBridgeFile
{
    public VoiceBridgeFile(
        string relativePath,
        string text,
        bool utf8Bom,
        string sourceId,
        string sourceSha256)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("语音桥文件缺少相对路径。", nameof(relativePath));
        }

        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        RelativePath = relativePath;
        Text = text;
        Utf8Bom = utf8Bom;
        SourceId = string.IsNullOrWhiteSpace(sourceId) ? relativePath : sourceId;
        SourceSha256 = sourceSha256 ?? string.Empty;
    }

    /// <summary>生成物中的相对路径，使用 '/' 分隔，例如 <c>Platform/Speech.cs</c>。</summary>
    public string RelativePath { get; }

    /// <summary>源文件原文（UTF-8 解码后，尚未替换命名空间）。</summary>
    public string Text { get; }

    /// <summary>源文件是否带 UTF-8 BOM；生成时原样保留。</summary>
    public bool Utf8Bom { get; }

    /// <summary>来源标识，例如 <c>Framework/platform/Speech.cs</c>。用于生成记录，不写绝对路径。</summary>
    public string SourceId { get; }

    /// <summary>源文件原始字节的 SHA-256（小写十六进制）。</summary>
    public string SourceSha256 { get; }
}
