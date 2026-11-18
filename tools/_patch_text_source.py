import pathlib

# PatchPlanTextInspector: add HasTextSource (string param OR instance text fields).
p = pathlib.Path(r'D:\DSHWorkSpace\src\Forge.Core\Models\PatchPlanTextInspector.cs')
s = p.read_text(encoding='utf-8')
orig = s
add = '''
    /// <summary>
    /// 计划的补丁点是否有可朗读文本来源：方法带 System.String 参数，或描述里带“类型文本字段：…”。
    /// 后者是 0.2.5 的实例字段兜底（无字符串参数时反射读实例字段），比只看参数更贴近“能不能读”。
    /// </summary>
    public static bool HasTextSource(PatchPlan? plan)
    {
        if (plan is null || plan.PatchPoints.Count == 0)
        {
            return false;
        }

        foreach (var point in plan.PatchPoints)
        {
            if (ContainsStringParameter(point.Description) || ContainsStringParameter(point.Target))
            {
                return true;
            }

            if (ContainsTextFieldMarker(point.Description))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsTextFieldMarker(string? text)
    {
        return !string.IsNullOrEmpty(text)
            && text.Contains(TextFieldMarker, StringComparison.Ordinal);
    }
'''
anchor = '    public static bool ContainsStringParameter(string? text)'
assert anchor in s
s = s.replace(anchor, add + '\n' + anchor, 1)

marker_decl = '    private const string TextFieldMarker = "类型文本字段：";'
# insert marker const after class opening brace
import re
m = re.search(r'(public static class PatchPlanTextInspector\r?\n\{\r?\n)', s)
if not m:
    raise SystemExit('class open not found')
s = s[:m.end()] + marker_decl + '\n' + s[m.end():]
p.write_text(s, encoding='utf-8')
print('PatchPlanTextInspector patched:', len(orig), '->', len(s))

# PipelineCommands: use HasTextSource in warning.
p = pathlib.Path(r'D:\DSHWorkSpace\src\Forge.Cli\PipelineCommands.cs')
s = p.read_text(encoding='utf-8')
old = '''        if (PatchPlanTextInspector.HasStringParameter(plan))
        {
            return;
        }

        await stderr.WriteLineAsync("[警告] 本次计划的补丁点都不含字符串参数：生成物只能记录调用次数，不会朗读。请按报告的人工待办人工指定文本来源，或换一款文本写法可静态识别的游戏再试。").ConfigureAwait(false);'''
new = '''        if (PatchPlanTextInspector.HasTextSource(plan))
        {
            return;
        }

        await stderr.WriteLineAsync("[警告] 本次计划的补丁点既没有字符串参数，也没有可读的实例文本字段：生成物只能记录调用次数，不会朗读。请按报告的人工待办人工指定文本来源，或换一款文本写法可静态识别的游戏再试。").ConfigureAwait(false);'''
assert old in s
s = s.replace(old, new, 1)
p.write_text(s, encoding='utf-8')
print('PipelineCommands warning updated')

# tests: add cases for HasTextSource / field marker.
p = pathlib.Path(r'D:\DSHWorkSpace\tests\Forge.Core.Tests\PatchPlanTextInspectorTests.cs')
s = p.read_text(encoding='utf-8')
orig = s
anchor = '\n}'
addition = '''
    [Fact]
    public void HasTextSource_FieldFallbackDescription_ReturnsTrue()
    {
        var plan = BuildPlan(description: "完整签名（Cecil）：[Assembly-CSharp.dll] System.Void Game.Dialogue::Show(System.Int32 id)。 类型文本字段：mainDialogueText/_text。");

        Assert.False(PatchPlanTextInspector.HasStringParameter(plan));
        Assert.True(PatchPlanTextInspector.HasTextSource(plan));
    }

    [Fact]
    public void HasTextSource_NeitherStringNorFields_ReturnsFalse()
    {
        var plan = BuildPlan(description: "完整签名（Cecil）：[Assembly-CSharp.dll] System.Void Game.Dialogue::PushButton(System.Boolean isSpace)。");

        Assert.False(PatchPlanTextInspector.HasTextSource(plan));
    }
'''
idx = s.rfind(anchor)
assert idx > 0
s = s[:idx] + addition + s[idx:]
p.write_text(s, encoding='utf-8')
print('tests patched')
