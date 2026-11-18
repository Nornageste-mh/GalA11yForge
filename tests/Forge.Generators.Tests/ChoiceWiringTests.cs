using System.Text.Json;
using GalA11yForge.Core.Models;
using Xunit;

namespace GalA11yForge.Generators.Tests;

/// <summary>
/// v0.2.2 选项朗读：证据里带 ChoiceAsset、没有字符串参数的补丁点，生成器要接
/// 「选项列表朗读 + 数字键选择」，而不是只做调用计数——选项期间游戏自己禁用鼠标。
/// </summary>
public sealed class ChoiceWiringTests : GeneratorTestBase
{
    private const string ChoiceSignature =
        "选项：完整签名（Cecil）：[Assembly-CSharp.dll] System.Void UIText::Choice(ChoiceAsset cs, System.Int32 hideMask)。";

    [Fact]
    public async Task Choice_hook_gets_reader_and_digit_key_selection()
    {
        var plan = Plan(Hook("choice", "UIText:Choice", description: ChoiceSignature));

        var result = await CreateGenerator().GenerateAsync(Context(), plan, OutDir);
        Assert.True(result.Ok, result.Error?.Message);

        var hook = ReadText(Path.Combine(OutDir, "Hooks", "Hook_choice.cs"));
        Assert.Contains("private static void Postfix(object[] __args, object __instance)", hook);
        Assert.Contains("ChoiceReader.OnChoice(__args, __instance);", hook);
        Assert.DoesNotContain("只能做调用计数", hook);

        var reader = ReadText(Path.Combine(OutDir, "Platform", "ChoiceReader.cs"));
        Assert.Contains("internal static void OnChoice", reader);
        Assert.Contains("Repeat.Push(", reader);
        Assert.Contains("WasNumberPressed", reader);
        Assert.Contains("onClick", reader);
        // 平台纪律：逐作层只经 Repeat，不直接 Speech.Speak。
        Assert.DoesNotContain("Speech.Speak", reader);

        var plugin = ReadText(Path.Combine(OutDir, "Plugin.cs"));
        Assert.Contains("ChoiceReader.Update();", plugin);

        var profile = JsonDocument.Parse(ReadText(Path.Combine(OutDir, "profile.json"))).RootElement;
        Assert.Equal("choices", profile.GetProperty("hooks")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Choice_hook_does_not_steal_primary_speech_from_dialogue_hook()
    {
        var plan = Plan(
            Hook("dialogue", "GameNS.ADV.Dialogue:SetText",
                description: "台词。完整签名（Cecil）：[Assembly-CSharp.dll] System.Void GameNS.ADV.Dialogue::SetText(System.String text)。"),
            Hook("choice", "UIText:Choice", description: ChoiceSignature));

        var result = await CreateGenerator().GenerateAsync(Context(), plan, OutDir);
        Assert.True(result.Ok, result.Error?.Message);

        var dialogue = ReadText(Path.Combine(OutDir, "Hooks", "Hook_dialogue.cs"));
        Assert.Contains("SpeechGate.Say(text);", dialogue);

        var profile = JsonDocument.Parse(ReadText(Path.Combine(OutDir, "profile.json"))).RootElement;
        Assert.Equal("speaks", profile.GetProperty("hooks")[0].GetProperty("status").GetString());
        Assert.Equal("choices", profile.GetProperty("hooks")[1].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Plans_without_choice_hooks_do_not_generate_choice_reader()
    {
        var plan = Plan(
            Hook("dialogue", "GameNS.ADV.Dialogue:SetText",
                description: "台词。完整签名（Cecil）：[Assembly-CSharp.dll] System.Void GameNS.ADV.Dialogue::SetText(System.String text)。"));

        var result = await CreateGenerator().GenerateAsync(Context(), plan, OutDir);
        Assert.True(result.Ok, result.Error?.Message);

        Assert.False(File.Exists(Path.Combine(OutDir, "Platform", "ChoiceReader.cs")));
        var plugin = ReadText(Path.Combine(OutDir, "Plugin.cs"));
        Assert.DoesNotContain("ChoiceReader.Update()", plugin);
    }
}
