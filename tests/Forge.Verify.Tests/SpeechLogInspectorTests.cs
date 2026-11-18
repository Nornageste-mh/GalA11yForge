using System.Text;

namespace GalA11yForge.Verify.Tests;

public sealed class SpeechLogInspectorTests
{
    private static string WriteLog(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), "forge-speech-" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public async Task Counts_hook_mounted_and_resolve_error_lines()
    {
        var path = WriteLog(
            "[Hook] 已挂载 Hook_hook_001 → Utage.AdvMessageWindow::PageTextChange",
            "[Hook] 已挂载 Hook_hook_003 → Utage.SystemUiDialog2Button::Open",
            "[Hook] Hook_hook_002 解析目标时出错：Ambiguous match found.",
            "[Speech] 早春");

        try
        {
            var summary = await SpeechLogInspector.InspectAsync(path);

            Assert.Equal(2, summary.HookMountedLines);
            Assert.Equal(1, summary.HookResolveErrorLines);
            Assert.Equal(1, summary.ValidLines);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Counts_hook_hits_and_watch_fallback_lines()
    {
        var path = WriteLog(
            "[Speech] 早春",
            "[Hook:hook-001] 命中 DialogueSceneManager::UpdateDialogueInfo；按实例文本字段兜底朗读。",
            "[Speech:watch] 兜底读到：设置",
            "[Speech:watch] 兜底读到：关闭");

        try
        {
            var summary = await SpeechLogInspector.InspectAsync(path);

            Assert.Equal(1, summary.HookHitLines);
            Assert.Equal(2, summary.WatchLines);
            Assert.Equal(1, summary.ValidLines);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Counts_unity_and_il2cpp_speech_lines_and_filters_noise()
    {
        var path = WriteLog(
            "[Info  :BepInEx] BepInEx 5.4.23.5",
            "[Speech] 早上好",
            "[a11y-speech] 早上好",
            "[a11y-speech] 今天天气不错",
            "[朗读] 旧框架也支持",
            "[Speech] Empty",
            "[a11y-speech] [12] 未设置",
            "[Speech:fields] 这行没有冒号标记，不算朗读",
            "[Speech:note] 历史缓冲不算朗读",
            "[Speech:placeholder] 跳过占位文本");

        try
        {
            var summary = await SpeechLogInspector.InspectAsync(path);

            Assert.Equal(6, summary.SpeechLines);
            Assert.Equal(4, summary.ValidLines);
            Assert.Equal(3, summary.UniqueLines);
            Assert.Equal(2, summary.PlaceholderLines);
            Assert.Equal(2, summary.MaxRepeat);
            Assert.False(summary.Truncated);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Counts_renpy_backend_lines()
    {
        var path = WriteLog(
            "[A11y启动] +5.8s 补丁 0.2.0.1",
            "[A11y菜单] +6.6s backend=SAPI 选项 2 个：1 Yes.；2 No.",
            "[A11y屏幕] +7.1s backend=SAPI 输入框：Please enter your name:",
            "[A11y焦点] +307s backend=SAPI 按钮 Quit");

        try
        {
            var summary = await SpeechLogInspector.InspectAsync(path);
            Assert.Equal(3, summary.SpeechLines);
            Assert.Equal(3, summary.ValidLines);
            Assert.Equal(3, summary.UniqueLines);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Empty_file_returns_zeroes()
    {
        var path = Path.Combine(Path.GetTempPath(), "forge-speech-" + Guid.NewGuid().ToString("N") + ".log");
        await File.WriteAllTextAsync(path, string.Empty);

        try
        {
            var summary = await SpeechLogInspector.InspectAsync(path);
            Assert.Equal(0, summary.SpeechLines);
            Assert.Equal(0, summary.ValidLines);
            Assert.Equal(0, summary.UniqueLines);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Oversized_log_reads_tail_and_reports_truncated()
    {
        var filler = string.Join("\n", Enumerable.Repeat("[Info] 无关日志行", 4000));
        var path = WriteLog(filler, "[a11y-speech] 尾巴上的台词");

        try
        {
            var summary = await SpeechLogInspector.InspectAsync(path, maxBytes: 4096);
            Assert.True(summary.Truncated);
            Assert.True(summary.ValidLines >= 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Does_not_double_count_reader_echo_lines()
    {
        var log = Path.Combine(Path.GetTempPath(), "forge-speech-" + Guid.NewGuid().ToString("N") + ".log");
        await File.WriteAllTextAsync(
            log,
            "[Speech] 早春\n[朗读] 早春\n[Speech] 下雨了\n",
            new UTF8Encoding(false));

        var summary = await SpeechLogInspector.InspectAsync(log, 4 * 1024 * 1024);

        Assert.Equal(1, summary.EchoLines);
        Assert.Equal(2, summary.ValidLines);
        Assert.Equal(2, summary.UniqueLines);
    }

    [Fact]
    public async Task Counts_renpy_overlay_channel_lines()
    {
        var path = Path.Combine(Path.GetTempPath(), "forge-speech-" + Guid.NewGuid().ToString("N") + ".log");
        await File.WriteAllTextAsync(
            path,
            "[A11y对话] +3.577s 这是第一句测试对白。\n" +
            "[A11y屏幕] +9.554s 提示：问题是：你要去哪里？。选项 2 个：1 去公园；2 回家\n" +
            "[A11y屏幕] +9.555s backend=NVDA 提示：问题是：你要去哪里？。选项 2 个：1 去公园；2 回家\n",
            new UTF8Encoding(false));
        try
        {
            var summary = await SpeechLogInspector.InspectAsync(path);
            // RenpyLines 只统计“计入有效行”的通道行；带 backend= 的重复行算回声（0.2.73 口径）。
            Assert.Equal(2, summary.RenpyLines);
            Assert.Equal(2, summary.ValidLines);
            Assert.Equal(1, summary.EchoLines);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Counts_error_lines_for_s05()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forge-speech-err-" + Guid.NewGuid().ToString("N") + ".log");
        await System.IO.File.WriteAllTextAsync(path, "[A11y错误] sapi.speak: NameError\n[Speech] 早春\nSystem.Exception: boom\n", new UTF8Encoding(false));
        var summary = await SpeechLogInspector.InspectAsync(path);

        Assert.Equal(2, summary.ErrorLines);
        Assert.Equal(1, summary.ValidLines);
        System.IO.File.Delete(path);
    }
}
