using System.Text.Json;

namespace GalA11yForge.Gui.Core;

/// <summary>
/// 「这个引擎没有内置适配器」这条失败路径的判定与解释。
///
/// CLI 在已知引擎但没有适配器时（例如 kirikiriz / gamecreator），analyze 会返回
/// <c>E_NOT_IMPLEMENTED</c>，建议里提示改用 <c>analyze --advanced</c>。GUI 的一键
/// 不应该就此停住：应自动换成高级分析并导出报告，再把原因讲清楚。
/// 这里只做纯判定与文案，不碰 UI。
/// </summary>
public static class AdapterlessFlow
{
    public const string NotImplementedCode = "E_NOT_IMPLEMENTED";

    /// <summary>判断某次 analyze 失败是不是“没有内置适配器”。</summary>
    public static bool IsAdapterlessAnalyzeFailure(ForgeEnvelope? envelope)
    {
        if (envelope is null)
        {
            return false;
        }

        if (!string.Equals(envelope.Code, NotImplementedCode, StringComparison.Ordinal))
        {
            return false;
        }

        var text = (envelope.Message ?? string.Empty) + " " + (envelope.Suggestion ?? string.Empty);
        return text.Contains("适配器尚未接线", StringComparison.Ordinal)
               || text.Contains("analyze --advanced", StringComparison.Ordinal);
    }

    /// <summary>从失败信封里尽量取出引擎族名，取不到返回 null。</summary>
    public static string? TryGetEngineFamily(ForgeEnvelope? envelope)
    {
        if (envelope is null || !envelope.HasData || envelope.Data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return envelope.Data.TryGetProperty("engine", out var engine) && engine.ValueKind == JsonValueKind.String
            ? engine.GetString()
            : null;
    }

    /// <summary>给读屏用户的一句话解释（失败路径转成“接下来能做什么”）。</summary>
    public static string Explain(string? engineFamily = null)
    {
        var engine = string.IsNullOrWhiteSpace(engineFamily) ? "这个引擎" : engineFamily;
        return $"「{engine}」没有内置适配器，不能自动生成可安装补丁；已改用 Ghidra 高级分析并导出报告。"
               + "自动安装仍需人工分析，报告与人工待办在结果区。";
    }
}
