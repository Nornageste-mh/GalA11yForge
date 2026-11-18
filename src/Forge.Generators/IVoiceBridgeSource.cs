namespace GalA11yForge.Generators;

/// <summary>
/// 语音桥源文件提供者。默认实现从仓库的 <c>Framework/</c> 读取平台层与契约层；
/// 测试可以注入最小样本，避免测试依赖 Framework 的具体内容。
/// </summary>
public interface IVoiceBridgeSource
{
    /// <summary>来源标识（写进生成记录，不写绝对路径）。</summary>
    string Id { get; }

    /// <summary>
    /// 读取全部源文件。读取失败时抛出：
    /// <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>（IO），
    /// <see cref="InvalidDataException"/>（内容不合约定），
    /// <see cref="DirectoryNotFoundException"/> / <see cref="FileNotFoundException"/>（缺依赖）。
    /// </summary>
    IReadOnlyList<VoiceBridgeFile> ReadFiles();
}
