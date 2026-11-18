import pathlib

p = pathlib.Path(r'D:\DSHWorkSpace\src\Forge.Cli\PipelineCommands.cs')
s = p.read_text(encoding='utf-8')
old = '''        if (PatchPlanTextInspector.HasStringParameter(plan))
        {
            return;
        }

        await stderr.WriteLineAsync(
            "[警告] 本次计划的补丁点都不含字符串参数：生成物只能记录调用次数，不会朗读。"
            + " 请按报告的人工待办人工指定文本来源，或换一款文本写法可静态识别的游戏再试。").ConfigureAwait(false);'''
new = '''        if (PatchPlanTextInspector.HasTextSource(plan))
        {
            return;
        }

        await stderr.WriteLineAsync(
            "[警告] 本次计划的补丁点既没有字符串参数，也没有可读的实例文本字段：生成物只能记录调用次数，不会朗读。"
            + " 请按报告的人工待办人工指定文本来源，或换一款文本写法可静态识别的游戏再试。").ConfigureAwait(false);'''
if old in s:
    s = s.replace(old, new, 1)
    p.write_text(s, encoding='utf-8')
    print('warning updated')
else:
    print('warning block NOT found (maybe already updated)')

# tests: append two cases with explicit PatchPlan construction.
p = pathlib.Path(r'D:\DSHWorkSpace\tests\Forge.Core.Tests\PatchPlanTextInspectorTests.cs')
s = p.read_text(encoding='utf-8')
addition = '''
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
    }
'''
if 'Field_fallback_description_counts_as_text_source' not in s:
    idx = s.rfind('\n}')
    s = s[:idx] + addition + s[idx:]
    p.write_text(s, encoding='utf-8')
    print('tests appended')
else:
    print('tests already present')
