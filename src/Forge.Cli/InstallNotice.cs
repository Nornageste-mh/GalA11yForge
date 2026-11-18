using System.Collections.Generic;

namespace GalA11yForge.Cli;

/// <summary>
/// 0.2.62：安装结果里的提醒。IL2CPP 默认不带 Tolk 语音运行时，插件会降级成只写日志；
/// 用户看到“安装完成”却听不到声音时，至少要能在结果里找到原因。
/// </summary>
public static class InstallNotice
{
    public const string SpeechRuntimeMissingText =
        " 注意：没有指定 --speech-runtime，IL2CPP 插件默认只写日志、不朗读；需要朗读时加 --speech-runtime <含 Tolk.dll 的目录>。";

    /// <summary>0.2.68：把「计划里有多少钩子 / 有没有兜底轮询」也写进安装结果，避免用户以为装完就会全自动读剧情。</summary>
    /// <summary>0.2.75：再补一句“装好后去哪看日志、怎么自动检查有没有念出来”。</summary>
    public static string Compose(
        bool isIl2Cpp,
        string? speechRuntimePath,
        IDictionary<string, object?> data,
        int hookCount = 0,
        bool hasWatcherFallback = false,
        string? gameDirectory = null,
        string? engineFamily = null)
    {
        var message = "安装完成。";
        if (isIl2Cpp && string.IsNullOrWhiteSpace(speechRuntimePath))
        {
            message += SpeechRuntimeMissingText;
            data["speechRuntimeMissing"] = true;
        }

        message += hookCount > 0
            ? $" 本补丁有 {hookCount} 个钩子"
            : " 本补丁没有自动钩子";
        message += hasWatcherFallback
            ? "；另外「文本兜底轮询」也开着：文本变化也会念，可在 BepInEx 配置里关掉（朗读 → 文本兜底轮询）。"
            : "。";

        data["hookCount"] = hookCount;
        data["watcherFallback"] = hasWatcherFallback;

        if (!string.IsNullOrWhiteSpace(gameDirectory))
        {
            var logPath = string.Equals(engineFamily, "renpy", System.StringComparison.OrdinalIgnoreCase)
                ? System.IO.Path.Combine(gameDirectory, "a11y_speech.log")
                : System.IO.Path.Combine(gameDirectory, "BepInEx", "LogOutput.log");
            data["speechLogHint"] = logPath;
            message += $" 装好后启动游戏、推进几句，日志看 {logPath}；" +
                       $"也可以用 forge verify --game \"{gameDirectory}\" --speech-log \"{logPath}\" --json 自动检查有没有念出来。";
        }

        return message;
    }
}
