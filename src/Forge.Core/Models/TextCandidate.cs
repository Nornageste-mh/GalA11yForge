namespace GalA11yForge.Core.Models;

/// <summary>
/// 文本候选类别；JSON 中写 dialogue / choice / menu / native_api / unknown。
/// native_api 表示"不是托管的 UI 文本方法，而是原生代码里的文本 API 调用点"（例如 Ghidra 找出的 TextOutW 包装函数）。
/// </summary>
public enum TextCandidateKind
{
    Unknown = 0,
    Dialogue = 1,
    Choice = 2,
    Menu = 3,
    NativeApi = 4,
}

/// <summary>
/// 文本钩子候选。Score 为 0..100（研究期打分规则：≥70 高、50–69 中、30–49 低）；
/// Id 供 PatchPoint.CandidateId 引用，建议格式 "tc-001"。
/// </summary>
public sealed class TextCandidate
{
    public string Id { get; set; } = string.Empty;

    public TextCandidateKind Kind { get; set; } = TextCandidateKind.Unknown;

    /// <summary>候选来源，例如 "assembly:Assembly-CSharp.dll!/DialogueManager::ShowText"。</summary>
    public string Source { get; set; } = string.Empty;

    public double Score { get; set; }

    public List<string> Evidence { get; set; } = new();
}
