using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Core.Abstractions;

/// <summary>
/// 高级分析后端（Ghidra、IDA 导出等）。后端不在主流程里，只有 --advanced 或人工触发时调用；
/// 后端返回的结果必须带 Coverage 与 Unverified，报告侧原样展示。
/// </summary>
public interface IAnalyzerBackend
{
    /// <summary>稳定标识，例如 ghidra、rizin。</summary>
    string BackendId { get; }

    Task<ForgeResult<BackendAnalysisResult>> AnalyzeAsync(
        AnalyzerBackendRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 万能层（通用运行时文本捕获）占位。v1 只做实验性实现：
/// 它不是“已支持引擎”，任何结果都必须在报告中标注 Experimental 与未验证项。
/// </summary>
public interface IUniversalCapture
{
    Task<ForgeResult<UniversalCaptureResult>> CaptureAsync(
        UniversalCaptureRequest request,
        CancellationToken cancellationToken = default);
}
