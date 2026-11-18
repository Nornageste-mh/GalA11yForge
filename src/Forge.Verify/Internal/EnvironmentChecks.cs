using GalA11yForge.Verify.Abstractions;
using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify.Internal;

/// <summary>语音后端可用性检查（e-01）。只报事实，不承诺出声。</summary>
internal static class EnvironmentChecks
{
    public static async Task RunAsync(
        VerificationRun run,
        PreparedContext context,
        VerifyOptions options,
        IEnvironmentProbe defaultProbe,
        CancellationToken cancellationToken)
    {
        if (!options.ProbeEnvironment)
        {
            run.Unverified("未探测语音后端（ProbeEnvironment=false），Tolk/NVDA/争渡/SAPI 的可用性未验证。");
            return;
        }

        var probe = options.EnvironmentProbe ?? defaultProbe;
        EnvironmentSnapshot snapshot;
        try
        {
            snapshot = await probe.ProbeAsync(context.GameDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            run.Skip(
                "e-01",
                "语音后端可用性",
                VerifyPriority.P1,
                $"语音后端探测失败：{ex.Message}");
            run.Unverified("语音后端探测失败，Tolk/NVDA/争渡/SAPI 的可用性未验证。");
            return;
        }

        run.Report.Environment = snapshot;

        if (snapshot.Backends.Count == 0)
        {
            run.Skip(
                "e-01",
                "语音后端可用性",
                VerifyPriority.P1,
                "环境探测没有返回任何语音后端事实。");
            run.Unverified("未获得任何语音后端事实，Tolk/NVDA/争渡/SAPI 的可用性未验证。");
            return;
        }

        var available = snapshot.Backends.Where(backend => backend.Available == true).ToList();
        var detail = string.Join(
            "；",
            snapshot.Backends.Select(backend =>
                $"{backend.DisplayName}：{(backend.Available switch
                {
                    true => "可用",
                    false => "不可用",
                    null => "未确认",
                })}（{backend.Fact}）"));

        if (available.Count == 0)
        {
            run.Skip(
                "e-01",
                "语音后端可用性",
                VerifyPriority.P1,
                $"未发现可用语音后端。{detail}",
                metrics: new Dictionary<string, double>
                {
                    ["voiceBackends"] = snapshot.Backends.Count,
                    ["availableVoiceBackends"] = 0,
                });
            run.Unverified("未发现可用语音后端（Tolk/NVDA/争渡/SAPI），游戏内朗读能力未验证；请安装/启动至少一个后端后重跑。");
            return;
        }

        run.Pass(
            "e-01",
            "语音后端可用性",
            VerifyPriority.P1,
            $"发现 {available.Count} 个可用语音后端。{detail}",
            metrics: new Dictionary<string, double>
            {
                ["voiceBackends"] = snapshot.Backends.Count,
                ["availableVoiceBackends"] = available.Count,
            });
        run.Unverified("环境探测只确认语音后端组件存在，不验证游戏内是否真的出声；朗读效果需要人工实测。");
    }
}
