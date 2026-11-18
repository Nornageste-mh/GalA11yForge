using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Core.Abstractions;

/// <summary>报告写出器。JSON 与 Markdown 必须来自同一份 ReportDocument，且都含覆盖声明/未验证项。</summary>
public interface IReportWriter
{
    Task<ForgeResult<ReportOutput>> WriteAsync(
        ReportDocument report,
        string outputDirectory,
        CancellationToken cancellationToken = default);
}

public enum FlowLogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>流程日志（reports/flow.jsonl，一行一个 JSON 对象）。日志不得包含游戏台词原文。</summary>
public interface IFlowLogger
{
    void Log(FlowLogLevel level, string stage, string message, IReadOnlyDictionary<string, string>? data = null);
}
