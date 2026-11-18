using GalA11yForge.Core.Models;

namespace GalA11yForge.Detection;

/// <summary>
/// 0.2.71：占位/空文件同样能命中 R01 指纹（0 字节 UnityPlayer.dll + Assembly-CSharp.dll 也能得 0.95）。
/// 这里按文件大小做一次降级校验，把置信度压到 0.6 并注明原因，避免“看着识别成功、其实没法分析”。
/// 幂等：重复调用不会重复加证据。
/// </summary>
public static class PlaceholderBinaryCheck
{
    internal const string EvidenceMarker = "PE 大小校验降级";

    private const long MinUnityPlayerBytes = 64 * 1024;

    private const long MinAssemblySharpBytes = 4 * 1024;

    public static void Apply(string gameDirectory, EngineDetection detection)
    {
        if (detection is null || string.IsNullOrWhiteSpace(gameDirectory))
        {
            return;
        }

        if (!string.Equals(detection.Family, "unity_mono", StringComparison.Ordinal))
        {
            return;
        }

        if (detection.Evidence.Any(line => line.Contains(EvidenceMarker, StringComparison.Ordinal)))
        {
            return;
        }

        long? unityPlayerBytes = null;
        var unityPlayer = Path.Combine(gameDirectory, "UnityPlayer.dll");
        if (File.Exists(unityPlayer))
        {
            unityPlayerBytes = new FileInfo(unityPlayer).Length;
        }

        long? assemblyBytes = null;
        try
        {
            foreach (var dataDirectory in Directory.EnumerateDirectories(gameDirectory, "*_Data"))
            {
                var assembly = Path.Combine(dataDirectory, "Managed", "Assembly-CSharp.dll");
                if (File.Exists(assembly))
                {
                    assemblyBytes = new FileInfo(assembly).Length;
                    break;
                }
            }
        }
        catch (IOException)
        {
            // 目录读不了就按未知处理，不影响识别结果。
        }
        catch (UnauthorizedAccessException)
        {
        }

        var placeholder =
            (unityPlayerBytes is { } player && player < MinUnityPlayerBytes)
            || (assemblyBytes is { } assembly2 && assembly2 < MinAssemblySharpBytes);
        if (!placeholder)
        {
            return;
        }

        detection.Confidence = Math.Min(detection.Confidence, 0.6);
        detection.Evidence.Add(
            $"measured:{EvidenceMarker}（UnityPlayer.dll={(unityPlayerBytes?.ToString() ?? "缺失")} 字节，"
            + $"Assembly-CSharp.dll={(assemblyBytes?.ToString() ?? "缺失")} 字节）：可能是占位/空文件，识别结果不可信。");
    }
}
