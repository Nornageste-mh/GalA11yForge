using GalA11yForge.Core.Models;

namespace GalA11yForge.Core.Tests;

/// <summary>
/// 选点诚实闸门的共用判断（task-37 P2 回归）：
/// Mono 的签名在 Description，IL2CPP 的签名在 Target，两处都必须被识别，
/// 否则 IL2CPP 的正常方案会被误报“没有字符串参数”。
/// </summary>
public sealed class PatchPlanTextInspectorTests
{
    [Fact]
    public void Null_plan_has_no_string_parameter()
    {
        Assert.False(PatchPlanTextInspector.HasStringParameter(null));
    }

    [Fact]
    public void Plan_without_points_has_no_string_parameter()
    {
        Assert.False(PatchPlanTextInspector.HasStringParameter(new PatchPlan()));
    }

    [Fact]
    public void Mono_signature_in_description_is_detected()
    {
        var plan = new PatchPlan
        {
            PatchPoints =
            [
                new PatchPoint
                {
                    Id = "hook-001",
                    Kind = PatchPointKind.Hook,
                    Target = "Assembly-CSharp.dll!/UIText:AnimateText",
                    Description = "完整签名（Cecil）：[Assembly-CSharp.dll] System.Void UIText::AnimateText(TMPro.TextMeshProUGUI targetText, System.String text, System.Single duration)。",
                },
            ],
        };

        Assert.True(PatchPlanTextInspector.HasStringParameter(plan));
    }

    [Fact]
    public void Il2Cpp_signature_in_target_is_detected()
    {
        var plan = new PatchPlan
        {
            PatchPoints =
            [
                new PatchPoint
                {
                    Id = "hook-001",
                    Kind = PatchPointKind.Hook,
                    Target = "Elringus.Naninovel.Runtime.dll!Naninovel.UITextPrinter::set_Text(System.String|value)",
                    Description = "Naninovel.UITextPrinter::set_Text（台词，分 100）",
                },
            ],
        };

        Assert.True(PatchPlanTextInspector.HasStringParameter(plan));
    }

    [Fact]
    public void Non_text_hooks_are_not_counted()
    {
        var plan = new PatchPlan
        {
            PatchPoints =
            [
                new PatchPoint
                {
                    Id = "hook-001",
                    Kind = PatchPointKind.Hook,
                    Target = "Assembly-CSharp.dll!/UIText:PushButton",
                    Description = "完整签名（Cecil）：[Assembly-CSharp.dll] System.Void UIText::PushButton(System.Boolean isSpace)。",
                },
            ],
        };

        Assert.False(PatchPlanTextInspector.HasStringParameter(plan));
    }
    [Fact]
    public void Field_fallback_description_counts_as_text_source()
    {
        var plan = new PatchPlan
        {
            PatchPoints =
            [
                new PatchPoint
                {
                    Id = "hook-001",
                    Kind = PatchPointKind.Hook,
                    Target = "Assembly-CSharp.dll!/Game.Dialogue::Show",
                    Description = "完整签名（Cecil）：[Assembly-CSharp.dll] System.Void Game.Dialogue::Show(System.Int32 id)。 类型文本字段：mainDialogueText/_text。",
                },
            ],
        };

        Assert.False(PatchPlanTextInspector.HasStringParameter(plan));
        Assert.True(PatchPlanTextInspector.HasTextSource(plan));
    }

    [Fact]
    public void Neither_string_nor_fields_is_not_text_source()
    {
        var plan = new PatchPlan
        {
            PatchPoints =
            [
                new PatchPoint
                {
                    Id = "hook-001",
                    Kind = PatchPointKind.Hook,
                    Target = "Assembly-CSharp.dll!/UIText:PushButton",
                    Description = "完整签名（Cecil）：[Assembly-CSharp.dll] System.Void UIText::PushButton(System.Boolean isSpace)。",
                },
            ],
        };

        Assert.False(PatchPlanTextInspector.HasTextSource(plan));
        Assert.True(PatchPlanTextInspector.WarnsNoTextSource(plan));
    }

    [Fact]
    public void Renpy_plan_without_static_strings_does_not_warn_because_text_is_runtime()
    {
        // task-53 真机：Ren'Py 覆盖层从运行时 say screen/focus_list 取文本，
        // 补丁点没有静态字符串参数是正常形态，旧警告是误报。
        var plan = new PatchPlan
        {
            Target = new PlanTarget { Engine = "renpy", EngineVersion = "6.99.12.4.2187" },
            PatchPoints =
            [
                new PatchPoint
                {
                    Id = "pp-001",
                    Kind = PatchPointKind.Script,
                    Target = "game/a11y_platform/00_contract.rpy",
                    Description = "公共契约层：运行时从 say screen 取文本。",
                },
            ],
        };

        Assert.False(PatchPlanTextInspector.HasTextSource(plan));
        Assert.False(PatchPlanTextInspector.WarnsNoTextSource(plan));
    }

    [Fact]
    public void Plan_with_text_source_never_warns()
    {
        var plan = new PatchPlan
        {
            Target = new PlanTarget { Engine = "unity_mono" },
            PatchPoints =
            [
                new PatchPoint
                {
                    Id = "hook-001",
                    Kind = PatchPointKind.Hook,
                    Target = "Assembly-CSharp.dll!/UIText:AnimateText",
                    Description = "完整签名（Cecil）：[Assembly-CSharp.dll] System.Void UIText::AnimateText(System.String text)。",
                },
            ],
        };

        Assert.False(PatchPlanTextInspector.WarnsNoTextSource(plan));
    }

}
