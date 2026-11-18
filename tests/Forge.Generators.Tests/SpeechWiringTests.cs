using System.Text.Json;
using GalA11yForge.Core.Models;
using Xunit;

namespace GalA11yForge.Generators.Tests;

/// <summary>
/// v0.2 朗读接线：生成器从补丁点证据的完整 Cecil 签名里取第一个字符串参数，
/// 主钩子走 SpeechGate.Say（立即朗读），其余带字符串参数的钩子只走 Note（进缓冲）。
/// 没有字符串参数的钩子仍然只记命中。
/// </summary>
public sealed class SpeechWiringTests : GeneratorTestBase
{
    private const string DialogueSignature =
        "台词 setter。完整签名（Cecil）：[Assembly-CSharp.dll] System.Void GameNS.ADV.Dialogue::SetText(System.String text)。";

    [Fact]
    public async Task Primary_hook_with_string_signature_speaks_through_Repeat()
    {
        var plan = Plan(
            Hook("dialogue", "GameNS.ADV.Dialogue:SetText", description: DialogueSignature),
            Hook("advance", "GameNS.ADV.Advance:OnClick(string)"));

        var result = await CreateGenerator().GenerateAsync(Context(), plan, OutDir);
        Assert.True(result.Ok, result.Error?.Message);

        var hook = ReadText(Path.Combine(OutDir, "Hooks", "Hook_dialogue.cs"));
        Assert.Contains("private static void Postfix(string text)", hook);
        Assert.Contains("SpeechGate.Say(text);", hook);
        Assert.DoesNotContain("只能做调用计数", hook);

        // 无签名的钩子仍只记命中。
        var advance = ReadText(Path.Combine(OutDir, "Hooks", "Hook_advance.cs"));
        Assert.Contains("只能做调用计数", advance);
        Assert.DoesNotContain("SpeechGate", advance);

        // 朗读闸门只经 Repeat 出声，不直接碰 Speech.Speak。
        var gate = ReadText(Path.Combine(OutDir, "Platform", "SpeechGate.cs"));
        Assert.Contains("internal static void Say", gate);
        Assert.Contains("Repeat.Say(clean, true);", gate);
        Assert.Contains("Repeat.Note(clean);", gate);
        Assert.DoesNotContain("Speech.Speak", gate);

        var profile = JsonDocument.Parse(ReadText(Path.Combine(OutDir, "profile.json"))).RootElement;
        Assert.Equal("speaks", profile.GetProperty("hooks")[0].GetProperty("status").GetString());
        Assert.Equal("skeleton_unwired", profile.GetProperty("hooks")[1].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Second_string_hook_becomes_buffer_only()
    {
        var plan = Plan(
            Hook("dialogue-a", "GameNS.ADV.Dialogue:SetText", description: DialogueSignature),
            Hook(
                "dialogue-b",
                "GameNS.ADV.Dialogue2:SetText",
                description: "完整签名（Cecil）：[Assembly-CSharp.dll] System.Void GameNS.ADV.Dialogue2::SetText(System.String message)。"));

        var result = await CreateGenerator().GenerateAsync(Context(), plan, OutDir);
        Assert.True(result.Ok, result.Error?.Message);

        var primary = ReadText(Path.Combine(OutDir, "Hooks", "Hook_dialogue_a.cs"));
        Assert.Contains("SpeechGate.Say(text);", primary);

        var secondary = ReadText(Path.Combine(OutDir, "Hooks", "Hook_dialogue_b.cs"));
        Assert.Contains("private static void Postfix(string message)", secondary);
        Assert.Contains("SpeechGate.Note(message);", secondary);
        Assert.DoesNotContain("SpeechGate.Say", secondary);

        var profile = JsonDocument.Parse(ReadText(Path.Combine(OutDir, "profile.json"))).RootElement;
        Assert.Equal("speaks", profile.GetProperty("hooks")[0].GetProperty("status").GetString());
        Assert.Equal("buffered", profile.GetProperty("hooks")[1].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Parameter_names_are_taken_from_evidence_not_guessed()
    {
        // 同一个方法：证据给出参数名 message，生成物就应该用 message；不能硬编码 text。
        var plan = Plan(
            Hook(
                "talk",
                "GameNS.ADV.Talk:Show(string)",
                description: "完整签名（Cecil）：[Assembly-CSharp.dll] System.Void GameNS.ADV.Talk::Show(System.String message)。"));

        var result = await CreateGenerator().GenerateAsync(Context(), plan, OutDir);
        Assert.True(result.Ok, result.Error?.Message);

        var hook = ReadText(Path.Combine(OutDir, "Hooks", "Hook_talk.cs"));
        Assert.Contains("private static void Postfix(string message)", hook);
        Assert.Contains("SpeechGate.Say(message);", hook);
    }
}
