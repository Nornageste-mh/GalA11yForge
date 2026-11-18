using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Adapters.Il2Cpp.Internal;

/// <summary>
/// IL2CPP 检测：复用 <c>Forge.Detection</c> 的指纹库，再要求目录里确实有 <c>GameAssembly.dll</c>。
///
/// <para>
/// 只有 Unity Mono 的目录会返回 <c>E_ENGINE_UNSUPPORTED</c> 并指向 <c>unity_mono</c> 适配器——
/// 两条路线的补丁运行时完全不同（BepInEx 5 + Mono.Cecil vs BepInEx 6 + Il2CppInterop），
/// 不能互相顶替。
/// </para>
/// </summary>
internal sealed class Il2CppDetector
{
    private readonly GalA11yForge.Detection.EngineDetector _fingerprints;

    public Il2CppDetector(GalA11yForge.Detection.EngineDetector? fingerprints = null)
    {
        _fingerprints = fingerprints ?? new GalA11yForge.Detection.EngineDetector();
    }

    public Task<ForgeResult<EngineDetection>> DetectAsync(GameContext context, CancellationToken cancellationToken)
    {
        if (context is null)
        {
            return Task.FromResult(ForgeResult<EngineDetection>.Failure(
                ForgeErrorCodes.ArgumentInvalid, "上下文为空，无法执行 IL2CPP 检测。"));
        }

        if (string.IsNullOrWhiteSpace(context.GameDirectory))
        {
            return Task.FromResult(ForgeResult<EngineDetection>.Failure(
                ForgeErrorCodes.ArgumentInvalid, "游戏目录为空，无法执行 IL2CPP 检测。", "请用 --game-dir 指向游戏根目录。"));
        }

        if (!Directory.Exists(context.GameDirectory))
        {
            return Task.FromResult(ForgeResult<EngineDetection>.Failure(
                ForgeErrorCodes.ArgumentInvalid, "游戏目录不存在：" + context.GameDirectory,
                "检查路径是否拼错，或换一台机器时盘符是否变化。"));
        }

        var detection = _fingerprints.Detect(context.GameDirectory, cancellationToken);
        var primary = detection.Primary;
        var gameAssembly = Path.Combine(context.GameDirectory, "GameAssembly.dll");
        var hasGameAssembly = File.Exists(gameAssembly);

        if (primary is null)
        {
            return Task.FromResult(ForgeResult<EngineDetection>.Failure(
                ForgeErrorCodes.EngineUnsupported,
                "没能识别出游戏引擎：" + context.GameDirectory,
                "确认目录指向游戏根目录（含 GameAssembly.dll / UnityPlayer.dll / 游戏名_Data）。"));
        }

        var family = primary.Family ?? string.Empty;

        if (!family.Equals("unity_il2cpp", StringComparison.OrdinalIgnoreCase))
        {
            if (!hasGameAssembly)
            {
                var hint = family.Equals("unity_mono", StringComparison.OrdinalIgnoreCase)
                    ? "这是 Unity Mono（有 Managed 程序集、没有 GameAssembly.dll），请改用 unity_mono 适配器。"
                    : "检测到的引擎是 " + (family.Length == 0 ? "unknown" : family) + "，不是 Unity IL2CPP。";
                return Task.FromResult(ForgeResult<EngineDetection>.Failure(
                    ForgeErrorCodes.EngineUnsupported, hint + " 目录：" + context.GameDirectory,
                    "先用 detect 子命令确认引擎族，再指定对应适配器。"));
            }

            // 指纹库给了别的族，但 GameAssembly.dll 在：以文件事实为准，降低置信度并留证据。
            return Task.FromResult(ForgeResult<EngineDetection>.Success(new EngineDetection
            {
                SchemaVersion = 1,
                Family = "unity_il2cpp",
                Version = primary.Version,
                Bitness = primary.Bitness == 0 ? 64 : primary.Bitness,
                Confidence = Math.Round(Math.Min(0.70, Math.Max(0.40, primary.Confidence - 0.20)), 3),
                Evidence = Append(primary.Evidence,
                    "指纹库给出的引擎族为 " + (family.Length == 0 ? "unknown" : family) + "，但根目录存在 GameAssembly.dll，按 IL2CPP 处理。",
                    "已确认：GameAssembly.dll（" + new FileInfo(gameAssembly).Length.ToString("N0") + " 字节）。"),
            }));
        }

        if (!hasGameAssembly)
        {
            return Task.FromResult(ForgeResult<EngineDetection>.Failure(
                ForgeErrorCodes.EngineUnsupported,
                "识别为 Unity IL2CPP，但根目录没有 GameAssembly.dll：" + context.GameDirectory,
                "可能被压缩包/安装器分卷拆走，或目录选到了子目录。"));
        }

        var metadata = Il2CppLayoutProbe.Probe(context.GameDirectory, Il2CppOptions.From(context).AsDictionary());
        return Task.FromResult(ForgeResult<EngineDetection>.Success(new EngineDetection
        {
            SchemaVersion = 1,
            Family = "unity_il2cpp",
            Version = primary.Version,
            Bitness = primary.Bitness == 0 ? 64 : primary.Bitness,
            Confidence = Math.Max(0.85, primary.Confidence),
            Evidence = Append(primary.Evidence, metadata.Describe()),
        }));
    }

    private static List<string> Append(IEnumerable<string>? existing, params string[] extra)
    {
        var list = existing is null ? new List<string>() : new List<string>(existing);
        foreach (var item in extra)
        {
            if (!string.IsNullOrWhiteSpace(item) && !list.Contains(item))
            {
                list.Add(item);
            }
        }

        return list;
    }
}
