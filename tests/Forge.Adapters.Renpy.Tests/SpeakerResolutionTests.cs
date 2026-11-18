using System.Diagnostics;
using System.Text;
using GalA11yForge.Core.Models;

namespace GalA11yForge.Adapters.Renpy.Tests;

/// <summary>
/// 说话人解析回归：DDLC 用 DynamicCharacter("s_name")，.name 只是表达式，
/// 必须走 unicode(character)/str(character) 才拿得到 Sayori。
/// 结构断言始终执行；本机有 Python 时再把生成模板里的两个函数抽出来真跑一遍。
/// </summary>
public sealed class SpeakerResolutionTests
{
    private readonly RenpyAdapter _adapter = new();

    [Fact]
    public async Task GeneratedDialogue_ResolvesDynamicCharacterDisplayName()
    {
        using var fixture = new RenpyFixture("6.99");
        var context = fixture.Context();
        var generated = await GenerateAsync(context, fixture);
        var dialoguePath = Path.Combine(generated, "game", "a11y_platform", "02_dialogue.rpy");
        var text = File.ReadAllText(dialoguePath);

        Assert.Contains("def _a11y_character_display_name(obj):", text, StringComparison.Ordinal);
        Assert.Contains("unicode(obj)", text, StringComparison.Ordinal);
        Assert.Contains("str(obj)", text, StringComparison.Ordinal);

        var speaker = ExtractFunction(text, "_a11y_speaker_name");
        var evaluate = speaker.IndexOf("_a11y_character_display_name(obj)", StringComparison.Ordinal);
        var readName = speaker.IndexOf("getattr(obj, \"name\"", StringComparison.Ordinal);
        Assert.True(evaluate >= 0, "说话人解析没有调用引擎求值入口");
        Assert.True(readName < 0 || evaluate < readName, "必须先 unicode()/str() 求值，再退回 .name");

        var python = FindPython();
        if (python is null)
        {
            // 与 RealGameTests 一致：本机没有 Python 时不做假断言，结构断言已经守住回归。
            return;
        }

        var result = RunPython(python, dialoguePath, Script);
        Assert.True(
            result.ExitCode == 0,
            $"Python 回归失败（{result.ExitCode}）：{result.StdOut}{result.StdErr}");
        Assert.Contains("PASS", result.StdOut, StringComparison.Ordinal);
    }

    private async Task<string> GenerateAsync(GameContext context, RenpyFixture fixture)
    {
        var detection = await _adapter.DetectAsync(context);
        Assert.True(detection.Ok, detection.Error?.Message);
        var analysis = await _adapter.AnalyzeAsync(context, detection.Value!);
        Assert.True(analysis.Ok, analysis.Error?.Message);
        var plan = await _adapter.PlanAsync(context, analysis.Value!);
        Assert.True(plan.Ok, plan.Error?.Message);
        var generated = await _adapter.GenerateAsync(context, plan.Value!, fixture.GeneratedDirectory, CancellationToken.None);
        Assert.True(generated.Ok, generated.Error?.Message);
        return generated.Value!.ProjectDirectory;
    }

    private static string ExtractFunction(string text, string name)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = -1;
        var indent = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart(' ');
            if (!trimmed.StartsWith("def " + name + "(", StringComparison.Ordinal))
            {
                continue;
            }

            start = i;
            indent = lines[i].Length - trimmed.Length;
            break;
        }

        Assert.True(start >= 0, "生成模板里找不到函数 " + name);
        var builder = new StringBuilder();
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i];
            if (i > start)
            {
                if (line.Trim().Length == 0)
                {
                    builder.Append('\n');
                    continue;
                }

                var currentIndent = line.Length - line.TrimStart(' ').Length;
                if (currentIndent <= indent)
                {
                    break;
                }
            }

            builder.AppendLine(line.Length >= indent ? line[indent..] : line);
        }

        return builder.ToString();
    }

    private static string? FindPython()
    {
        var configured = Environment.GetEnvironmentVariable("GALA11Y_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        if (File.Exists(@"D:\python\python.exe"))
        {
            return @"D:\python\python.exe";
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), "python.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunPython(string python, string dialoguePath, string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-");
        startInfo.ArgumentList.Add(dialoguePath);

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Write(script);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            return (-1, stdout, stderr + "\nPython 超时（30s）");
        }

        return (process.ExitCode, stdout, stderr);
    }

    private const string Script =
        """
        import io
        import sys
        import types

        path = sys.argv[1]
        lines = io.open(path, encoding="utf-8-sig").read().splitlines()


        def extract(name):
            start = None
            indent = 0
            for i, line in enumerate(lines):
                stripped = line.lstrip(" ")
                if stripped.startswith("def " + name + "("):
                    start = i
                    indent = len(line) - len(stripped)
                    break
            if start is None:
                raise SystemExit("missing function: " + name)
            out = [lines[start][indent:]]
            for line in lines[start + 1:]:
                if not line.strip():
                    out.append("")
                    continue
                cur = len(line) - len(line.lstrip(" "))
                if cur <= indent:
                    break
                out.append(line[indent:])
            return "\n".join(out)


        helper_src = extract("_a11y_character_display_name")
        speaker_src = extract("_a11y_speaker_name")


        class FakeCharacter(object):
            def __init__(self, expr, value, dynamic=True):
                self.name = expr
                self.value = value
                self.dynamic = dynamic

            def __unicode__(self):
                return self.value

            def __str__(self):
                return self.value


        class BrokenCharacter(FakeCharacter):
            def __unicode__(self):
                raise TypeError("broken name")

            def __str__(self):
                raise TypeError("broken name")


        class CallableCharacter(object):
            dynamic = True

            def __init__(self, value):
                self.name = lambda: value

            def __unicode__(self):
                return self.name()

            def __str__(self):
                return self.name()


        def py2_unicode(obj):
            fn = getattr(obj, "__unicode__", None)
            if fn is not None:
                return fn()
            return str(obj)


        def make_namespace(py2):
            store = types.SimpleNamespace()
            renpy = types.SimpleNamespace(store=store)
            ns = {
                "_A11Y_PY2": py2,
                "_A11Y_TEXT": str,
                "_a11y_u": lambda value: value,
                "_a11y_clean": lambda value: value.strip(),
                "renpy": renpy,
            }
            if py2:
                ns["unicode"] = py2_unicode
            exec(helper_src, ns)
            exec(speaker_src, ns)
            return ns


        def check(py2):
            label = "py2" if py2 else "py3"
            ns = make_namespace(py2)
            store = ns["renpy"].store
            speak = ns["_a11y_speaker_name"]

            store.s = FakeCharacter("s_name", "Sayori")
            store._last_say_who = "s"
            got = speak()
            assert got == "Sayori", label + " dynamic: " + repr(got)

            store.y = FakeCharacter("Yuri", "Yuri", dynamic=False)
            store._last_say_who = "y"
            got = speak()
            assert got == "Yuri", label + " static: " + repr(got)

            store.p = "Monika"
            store._last_say_who = "p"
            got = speak()
            assert got == "Monika", label + " literal: " + repr(got)

            store._last_say_who = "'Natsuki'"
            got = speak()
            assert got == "Natsuki", label + " quoted: " + repr(got)

            store._last_say_who = "missing_name"
            got = speak()
            assert got == "", label + " missing: " + repr(got)

            store.m = CallableCharacter("Monika")
            store._last_say_who = "m"
            got = speak()
            assert got == "Monika", label + " callable: " + repr(got)

            store.b = BrokenCharacter("s_name", "Sayori")
            store._last_say_who = "b"
            got = speak()
            assert got == "", label + " broken: " + repr(got)

            store._last_say_who = None
            got = speak()
            assert got == "", label + " narration: " + repr(got)


        check(True)
        check(False)
        print("PASS dynamic-character speaker resolution")
        """;
}
