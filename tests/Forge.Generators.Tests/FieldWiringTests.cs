using System.Text.Json;
using GalA11yForge.Core.Models;
using Xunit;

namespace GalA11yForge.Generators.Tests;

/// <summary>
/// v0.2.5 字段兜底朗读：补丁点没有字符串参数、但证据给出实例文本字段时，
/// 生成 Postfix(object __instance) 反射读字段，而不是只做调用计数。
/// 场景来源：GalMaster 的 DialogueHandle 把台词写在 TMP_Text 字段里。
/// </summary>
public sealed class FieldWiringTests : GeneratorTestBase
{
    private const string SignatureNoString =
        "台词：完整签名（Cecil）：[Assembly-CSharp.dll] System.Void GameNS.DialogueHandle::ShowDialogue(System.Int32 index, System.Boolean save)。" +
        " 类型文本字段：mainDialogueName/mainDiagueText。";

    [Fact]
    public async Task Field_hook_speaks_through_sayfields_when_no_string_parameter()
    {
        var plan = Plan(Hook("dialogue", "GameNS.DialogueHandle:ShowDialogue", description: SignatureNoString));

        var result = await CreateGenerator().GenerateAsync(Context(), plan, OutDir);
        Assert.True(result.Ok, result.Error?.Message);

        var hook = ReadText(Path.Combine(OutDir, "Hooks", "Hook_dialogue.cs"));
        Assert.Contains("private static void Postfix(object __instance)", hook);
        Assert.Contains("SpeechGate.SayFields(__instance, new[] { \"mainDialogueName\", \"mainDiagueText\" });", hook);
        Assert.DoesNotContain("只能做调用计数", hook);

        var gate = ReadText(Path.Combine(OutDir, "Platform", "SpeechGate.cs"));
        Assert.Contains("internal static void SayFields(", gate);
        Assert.Contains("internal static void NoteFields(", gate);
        Assert.Contains("GetField(", gate);
        // 字段是类型级声明：只朗读本次变化过的字段，避免把旧句/调试文本再念一遍。
        Assert.Contains("ConditionalWeakTable", gate);
        Assert.Contains("字段与上次相同", gate);
        // 真机 GalMaster 首句的文本在钩子返回后才赋值：读不到时登记一次延迟复查。
        Assert.Contains("ScheduleFieldRecheck", gate);
        Assert.Contains("延迟复查", gate);
        // 等待上限可配置（0=关闭补读），默认 4 秒，上限 15 秒。
        Assert.Contains("ConfigureFieldRecheck", gate);
        Assert.Contains("DefaultFieldRecheckMaxWaitSeconds", gate);
        // 真机 manosaba：SetTitleText 反复收到 "[8] Empty" 这类占位符，不该当台词念。
        Assert.Contains("PlaceholderTextRx", gate);
        Assert.Contains("占位文本", gate);
        // 平台纪律：逐作层只经 Repeat，不直接 Speech.Speak。
        Assert.DoesNotContain("Speech.Speak", gate);

        var plugin = ReadText(Path.Combine(OutDir, "Plugin.cs"));
        Assert.Contains("SpeechGate.Update();", plugin);
        Assert.Contains("字段补读最长等待秒", plugin);
        Assert.Contains("SpeechGate.ConfigureFieldRecheck(", plugin);
        // 0.2.51 文本兜底轮询：未知框架/没识别到正文入口时的最后一道保险。
        Assert.Contains("SpeechGate.ConfigureWatcher(Config);", plugin);
        Assert.Contains("SpeechGate.TickWatcher();", plugin);
        Assert.Contains("文本兜底轮询", gate);
        Assert.Contains("FindObjectsOfTypeAll", gate);
        Assert.Contains("WatchIgnoreExact", gate);
        Assert.Contains("WatchPlaceholderRx", gate);
        Assert.Contains("windowed", gate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[Speech:watch]", gate);

        var profile = JsonDocument.Parse(ReadText(Path.Combine(OutDir, "profile.json"))).RootElement;
        Assert.Equal("speaks", profile.GetProperty("hooks")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Second_field_hook_becomes_buffer_only()
    {
        var plan = Plan(
            Hook("dialogue1", "GameNS.DialogueHandle:ShowDialogue", description: SignatureNoString),
            Hook("dialogue2", "GameNS.OtherDialog:Show", description:
                "台词：完整签名（Cecil）：[Assembly-CSharp.dll] System.Void GameNS.OtherDialog::Show(System.Int32 n)。 类型文本字段：bodyText。"));

        var result = await CreateGenerator().GenerateAsync(Context(), plan, OutDir);
        Assert.True(result.Ok, result.Error?.Message);

        var first = ReadText(Path.Combine(OutDir, "Hooks", "Hook_dialogue1.cs"));
        var second = ReadText(Path.Combine(OutDir, "Hooks", "Hook_dialogue2.cs"));
        Assert.Contains("SpeechGate.SayFields(__instance", first);
        Assert.Contains("SpeechGate.NoteFields(__instance", second);

        var profile = JsonDocument.Parse(ReadText(Path.Combine(OutDir, "profile.json"))).RootElement;
        Assert.Equal("speaks", profile.GetProperty("hooks")[0].GetProperty("status").GetString());
        Assert.Equal("buffered", profile.GetProperty("hooks")[1].GetProperty("status").GetString());
    }

    [Fact]
    public async Task No_string_and_no_fields_still_skeleton()
    {
        var plan = Plan(Hook("empty", "GameNS.Menu:Refresh", description:
            "菜单：完整签名（Cecil）：[Assembly-CSharp.dll] System.Void GameNS.Menu::Refresh(System.Int32 index)。"));

        var result = await CreateGenerator().GenerateAsync(Context(), plan, OutDir);
        Assert.True(result.Ok, result.Error?.Message);

        var hook = ReadText(Path.Combine(OutDir, "Hooks", "Hook_empty.cs"));
        Assert.Contains("private static void Postfix()", hook);
        Assert.Contains("只能做调用计数", hook);

        var profile = JsonDocument.Parse(ReadText(Path.Combine(OutDir, "profile.json"))).RootElement;
        Assert.Equal("skeleton_unwired", profile.GetProperty("hooks")[0].GetProperty("status").GetString());
    }

    private const string SignatureUtageChain =
        "台词：完整签名（Cecil）：[Assembly-CSharp.dll] System.Void Utage.AdvMessageWindow::PageTextChange(System.Int32 pageNo)。" +
        " 文本属性链：Text.OriginalText。";

    [Fact]
    public async Task Property_chain_hook_speaks_by_reflection_chain()
    {
        var plan = Plan(Hook("utage", "Utage.AdvMessageWindow:PageTextChange", description: SignatureUtageChain));

        var result = await CreateGenerator().GenerateAsync(Context(), plan, OutDir);
        Assert.True(result.Ok, result.Error?.Message);

        var hook = ReadText(Path.Combine(OutDir, "Hooks", "Hook_utage.cs"));
        Assert.Contains("private static void Postfix(object __instance)", hook);
        Assert.Contains(
            "SpeechGate.SayPropertyChain(__instance, new[] { \"Text\", \"OriginalText\" });",
            hook);
        Assert.Contains("按实例属性链兜底朗读", hook);
        Assert.DoesNotContain("只能做调用计数", hook);

        var gate = ReadText(Path.Combine(OutDir, "Platform", "SpeechGate.cs"));
        Assert.Contains("internal static void SayPropertyChain(", gate);
        Assert.Contains("internal static void NotePropertyChain(", gate);
        Assert.Contains("CollectPropertyChain(", gate);
        Assert.Contains("[Speech:chain]", gate);

        var profile = JsonDocument.Parse(ReadText(Path.Combine(OutDir, "profile.json"))).RootElement;
        Assert.Equal("speaks", profile.GetProperty("hooks")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task String_parameter_hook_still_wins_primary_over_field_hook()
    {
        var plan = Plan(
            Hook("dialogue1", "GameNS.DialogueHandle:ShowDialogue", description: SignatureNoString),
            Hook("dialogue2", "GameNS.ADV.Dialogue:SetText", description:
                "台词。完整签名（Cecil）：[Assembly-CSharp.dll] System.Void GameNS.ADV.Dialogue::SetText(System.String text)。"));

        var result = await CreateGenerator().GenerateAsync(Context(), plan, OutDir);
        Assert.True(result.Ok, result.Error?.Message);

        var fieldHook = ReadText(Path.Combine(OutDir, "Hooks", "Hook_dialogue1.cs"));
        var stringHook = ReadText(Path.Combine(OutDir, "Hooks", "Hook_dialogue2.cs"));
        Assert.Contains("SpeechGate.NoteFields(__instance", fieldHook);
        Assert.Contains("SpeechGate.Say(text);", stringHook);

        var profile = JsonDocument.Parse(ReadText(Path.Combine(OutDir, "profile.json"))).RootElement;
        Assert.Equal("buffered", profile.GetProperty("hooks")[0].GetProperty("status").GetString());
        Assert.Equal("speaks", profile.GetProperty("hooks")[1].GetProperty("status").GetString());
    }
}
