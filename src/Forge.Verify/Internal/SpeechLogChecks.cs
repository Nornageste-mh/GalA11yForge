using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify.Internal;

/// <summary>
/// 安装后的“到底有没有发声”检查：读运行日志里补丁写下的朗读行，只做统计。
/// 找不到日志时不是失败，而是写入未验证项——日志缺失不等于没出声。
/// </summary>
internal static class SpeechLogChecks
{
    public static async Task RunAsync(
        VerificationRun run,
        PreparedContext context,
        VerifyOptions options,
        CancellationToken cancellationToken)
    {
        var explicitPath = !string.IsNullOrWhiteSpace(options.SpeechLogPath);
        var logPath = ResolveLogPath(context, options);

        if (logPath is null)
        {
            if (explicitPath)
            {
                run.Fail(
                    "s-01",
                    "语音日志存在",
                    VerifyPriority.P1,
                    $"指定的语音日志不存在：{options.SpeechLogPath}",
                    null,
                    new Dictionary<string, double> { ["speechLogMissing"] = 1 });
            }
            else
            {
                run.Unverified(
                    "未找到运行日志（BepInEx/LogOutput.log 或 a11y_speech.log）：补丁是否真的发声未验证；"
                    + "启动游戏并推进几句对白后再运行 verify，或显式传 --speech-log <日志路径>。");
            }

            return;
        }

        string relative;
        try
        {
            relative = Path.GetRelativePath(context.GameDirectory, logPath);
        }
        catch (ArgumentException)
        {
            relative = logPath;
        }

        SpeechLogSummary summary;
        try
        {
            summary = await SpeechLogInspector
                .InspectAsync(logPath, options.SpeechLogMaxBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            run.Fail(
                "s-01",
                "语音日志可读",
                VerifyPriority.P1,
                $"读取语音日志失败：{ex.Message}",
                new[] { relative });
            return;
        }

        run.Metric("speechLogBytes", summary.FileSize);
        run.Metric("speechLines", summary.SpeechLines);
        run.Metric("speechValidLines", summary.ValidLines);
        run.Metric("speechUniqueLines", summary.UniqueLines);
        run.Metric("speechPlaceholderLines", summary.PlaceholderLines);
    run.Metric("speechEchoLines", summary.EchoLines);
        run.Metric("speechErrorLines", summary.ErrorLines);
        run.Metric("speechRenpyLines", summary.RenpyLines);
        run.Metric("speechHookHitLines", summary.HookHitLines);
        run.Metric("speechWatchLines", summary.WatchLines);

        run.Pass(
            "s-01",
            "语音日志可读",
            VerifyPriority.P2,
            $"{relative}（{summary.FileSize:N0} B，{summary.ScannedLines} 行，{(summary.Truncated ? "只读了尾部" : "完整读取")}）",
            new[] { relative });

        if (summary.ValidLines == 0)
        {
            // “日志里没读到台词”不等于“补丁坏了”：可能只是停在标题画面。
            // 只写未验证项，不新增失败/跳过检查，避免把正常安装判成异常。
            run.Unverified(
                summary.SpeechLines == 0
                    ? $"日志 {relative} 里没有找到朗读行：可能还没进到对白，或补丁没接上；请推进几句后再运行 verify。"
                    : $"日志 {relative} 里有 {summary.SpeechLines} 行朗读输出，但有效文本 0 行（占位/空白 {summary.PlaceholderLines} 行）：可能还停在标题画面。");
            return;
        }

        run.Pass(
            "s-02",
            "日志里有有效朗读输出",
            VerifyPriority.P1,
            $"有效朗读 {summary.ValidLines} 行，去重后 {summary.UniqueLines} 行，占位噪声 {summary.PlaceholderLines} 行。",
            new[] { relative });

        // 0.2.59：区分“钩子命中正文”和“全靠兜底轮询”。全体兜底时只写未验证，绝不判通过——
        // 兜底只能证明“读到了某个文本组件”，不能证明“读到的是剧情正文”。
        if (summary.HookHitLines > 0 || summary.WatchLines > 0)
        {
            if (summary.HookHitLines == 0)
            {
                run.Skip(
                    "s-04",
                    "朗读来自正文钩子",
                    VerifyPriority.P2,
                    $"有效朗读 {summary.ValidLines} 行，但钩子命中 0 行、兜底轮询读到 {summary.WatchLines} 行：可能全部来自兜底轮询。",
                    new[] { relative });
                run.Unverified(
                    "朗读通道：全部来自兜底轮询（钩子 0 命中）——可能只读到 UI/菜单文字，剧情正文未验证。");
            }
            else
            {
                run.Pass(
                    "s-04",
                    "朗读来自正文钩子",
                    VerifyPriority.P2,
                    $"钩子命中 {summary.HookHitLines} 行，兜底轮询读到 {summary.WatchLines} 行。",
                    new[] { relative });
            }
        }

        if (summary.ErrorLines > 0)
        {
            run.Fail(
                "s-05",
                "日志里没有错误行",
                VerifyPriority.P1,
                $"日志里有 {summary.ErrorLines} 条错误行（[A11y错误] 或 Exception）：补丁可能部分失败，朗读不一定可靠。",
                new[] { relative });
        }
        else
        {
            run.Pass(
                "s-05",
                "日志里没有错误行",
                VerifyPriority.P1,
                "没有发现 [A11y错误] 或 Exception 行。",
                new[] { relative });
        }

        if (summary.ValidLines < 2)
        {
            run.Skip(
                "s-03",
                "朗读文本没有明显重复",
                VerifyPriority.P2,
                "有效朗读行只有 1 行，样本不足，无法判断是否重复朗读。",
                new[] { relative });
            return;
        }

        if (summary.ValidLines >= 4 && summary.UniqueLines * 2 <= summary.ValidLines)
        {
            run.Fail(
                "s-03",
                "朗读文本没有明显重复",
                VerifyPriority.P2,
                $"有效朗读 {summary.ValidLines} 行，去重后只有 {summary.UniqueLines} 行（同一句最多重复 {summary.MaxRepeat} 次）：可能重复朗读同一句。",
                new[] { relative });
            return;
        }

        run.Pass(
            "s-03",
            "朗读文本没有明显重复",
            VerifyPriority.P2,
            $"有效朗读 {summary.ValidLines} 行，去重后 {summary.UniqueLines} 行（同一句最多重复 {summary.MaxRepeat} 次）。",
            new[] { relative });
    }

    private static string? ResolveLogPath(PreparedContext context, VerifyOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.SpeechLogPath))
        {
            if (Path.IsPathRooted(options.SpeechLogPath))
            {
                var rooted = Path.GetFullPath(options.SpeechLogPath);
                return File.Exists(rooted) ? rooted : null;
            }

            // 0.2.82：相对路径先按当前工作目录找（命令行里 `--speech-log 文件名` 的常见用法），
            // 找不到再退回按游戏目录解析（旧行为），两者都不存在才判“日志不存在”。
            var fromWorkingDirectory = Path.GetFullPath(options.SpeechLogPath);
            if (File.Exists(fromWorkingDirectory))
            {
                return fromWorkingDirectory;
            }

            var fromGameDirectory = Path.GetFullPath(Path.Combine(context.GameDirectory, options.SpeechLogPath));
            return File.Exists(fromGameDirectory) ? fromGameDirectory : null;
        }

        var candidates = new[]
        {
            context.LogPath,
            Path.Combine(context.GameDirectory, "BepInEx", "LogOutput.log"),
            Path.Combine(context.GameDirectory, "a11y_speech.log"),
            Path.Combine(context.GameDirectory, "game", "a11y_speech.log"),
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
