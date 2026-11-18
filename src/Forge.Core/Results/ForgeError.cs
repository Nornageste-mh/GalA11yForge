namespace GalA11yForge.Core.Results;

/// <summary>面向用户的错误对象。错误码稳定，Message 可直接展示，Suggestion 给出下一步。</summary>
public sealed class ForgeError
{
    public string Code { get; init; } = ForgeErrorCodes.Internal;

    public string Message { get; init; } = string.Empty;

    /// <summary>用户可读的下一步建议；没有建议时为 null。</summary>
    public string? Suggestion { get; init; }

    /// <summary>可选的机器可读上下文（命令、路径、阶段等）。不要放游戏台词或用户隐私内容。</summary>
    public IReadOnlyDictionary<string, string>? Details { get; init; }

    public static ForgeError Create(
        string code,
        string message,
        string? suggestion = null,
        IReadOnlyDictionary<string, string>? details = null)
        => new()
        {
            Code = code,
            Message = message,
            Suggestion = suggestion,
            Details = details,
        };

    public override string ToString()
        => string.IsNullOrWhiteSpace(Suggestion) ? $"{Code}: {Message}" : $"{Code}: {Message}（建议：{Suggestion}）";
}
