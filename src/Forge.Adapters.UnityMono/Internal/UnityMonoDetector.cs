using GalA11yForge.Core.Abstractions;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Detection;

namespace GalA11yForge.Adapters.UnityMono.Internal;

/// <summary>复用 Forge.Detection 的指纹引擎，把同步 Detect 包成 IEngineDetector。</summary>
internal sealed class UnityMonoDetector : IEngineDetector
{
    private readonly EngineDetector _detector;

    public UnityMonoDetector(EngineDetector detector)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
    }

    public Task<ForgeResult<EngineDetection>> DetectAsync(
        GameContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (context is null)
            {
                return Task.FromResult(ForgeResult<EngineDetection>.Failure(
                    ForgeErrorCodes.ArgumentInvalid,
                    "GameContext 不能为空。"));
            }

            if (string.IsNullOrWhiteSpace(context.GameDirectory))
            {
                return Task.FromResult(ForgeResult<EngineDetection>.Failure(
                    ForgeErrorCodes.ArgumentInvalid,
                    "游戏目录不能为空。"));
            }

            if (!Directory.Exists(context.GameDirectory))
            {
                return Task.FromResult(ForgeResult<EngineDetection>.Failure(
                    ForgeErrorCodes.ArgumentInvalid,
                    $"游戏目录不存在：{context.GameDirectory}",
                    "确认 --game 指向安装目录。"));
            }

            var result = _detector.Detect(context.GameDirectory, cancellationToken);
            if (result.Primary is null)
            {
                return Task.FromResult(ForgeResult<EngineDetection>.Failure(
                    ForgeErrorCodes.AdapterInvalid,
                    "检测器没有返回 Primary 结果。"));
            }

            GalA11yForge.Detection.PlaceholderBinaryCheck.Apply(context.GameDirectory, result.Primary);

            return Task.FromResult(ForgeResult<EngineDetection>.Success(result.Primary));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(ForgeResult<EngineDetection>.Failure(
                ForgeErrorCodes.Internal,
                "检测已取消。"));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(ForgeResult<EngineDetection>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                $"检测参数不合法：{ex.Message}",
                "检查游戏目录。"));
        }
        catch (DirectoryNotFoundException ex)
        {
            return Task.FromResult(ForgeResult<EngineDetection>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                $"检测时找不到目录：{ex.Message}",
                "确认游戏目录仍然存在。"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ForgeResult<EngineDetection>.Failure(
                ForgeErrorCodes.Internal,
                $"Unity Mono 检测失败：{ex.Message}",
                "保留游戏目录不变，把该消息交给开发者。"));
        }
    }

}
