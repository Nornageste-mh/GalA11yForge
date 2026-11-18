using GalA11yForge.Core;

namespace GalA11yForge.Generators.Internal;

/// <summary>
/// 生成的 <c>profile.json</c>：只记录 id / 类型 / 状态 / 计数，
/// **不复制** plan 里的 Description、ManualTodo 自由文本——那些字段可能含台词或剧透。
/// </summary>
internal sealed class GeneratedProfile
{
    public int SchemaVersion { get; set; } = 1;

    public string Generator { get; set; } = "unity_mono";

    public string GeneratorVersion { get; set; } = ForgeInfo.Version;

    public string GameSlug { get; set; } = string.Empty;

    public string GameName { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Engine { get; set; } = "unity_mono";

    public string Namespace { get; set; } = string.Empty;

    public string AssemblyName { get; set; } = string.Empty;

    public string PluginGuid { get; set; } = string.Empty;

    public string PluginVersion { get; set; } = string.Empty;

    public string FrameworkFingerprint { get; set; } = string.Empty;

    public List<ProfileHookEntry> Hooks { get; set; } = new();

    public List<ProfileSkippedEntry> SkippedPatchPoints { get; set; } = new();

    public ProfileTodoSummary ManualTodo { get; set; } = new();

    public ProfileRiskSummary Risks { get; set; } = new();

    public ProfileSettings Settings { get; set; } = new();

    public List<string> Verification { get; set; } = new();
}

internal sealed class ProfileHookEntry
{
    public string Id { get; set; } = string.Empty;

    public string Kind { get; set; } = "hook";

    public string Target { get; set; } = string.Empty;

    public string Status { get; set; } = "skeleton_unwired";

    public string File { get; set; } = string.Empty;

    public double Confidence { get; set; }
}

internal sealed class ProfileSkippedEntry
{
    public string Id { get; set; } = string.Empty;

    public string Kind { get; set; } = "other";

    public string Status { get; set; } = "not_implemented_by_generator";
}

internal sealed class ProfileTodoSummary
{
    public int Count { get; set; }

    public int Blocking { get; set; }

    public string Note { get; set; } = "生成器不复制人工待办的自由文本，详见 plan.json。";
}

internal sealed class ProfileRiskSummary
{
    public int Count { get; set; }

    public int Blocking { get; set; }

    public string Note { get; set; } = "生成器不复制风险描述的自由文本，详见 plan.json。";
}

internal sealed class ProfileSettings
{
    public string RepeatHotkey { get; set; } = "=";

    public string SpeechBackend { get; set; } = "auto";
}
