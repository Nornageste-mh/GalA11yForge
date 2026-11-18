using Xunit.Sdk;

namespace Forge.E2E;

public sealed class DetectTests
{
    [E2EFact]
    public void Detect_FakeUnityMonoGame_ReportsUnityMono()
    {
        var sandbox = E2ESandbox.Create("detect-unity-mono");
        var result = ForgeCli.Run("detect", E2ESandbox.FixtureRoot, sandbox.Workspace, sandbox.OutRoot);
        E2EAssert.Succeeded(result, "detect（假 Unity Mono 游戏）", hint: $"sandbox：{sandbox.Root}");

        var view = DetectView.Resolve(result, new[] { sandbox.Workspace, sandbox.OutRoot });
        Assert.True(
            string.Equals(view.Family, "unity_mono", StringComparison.OrdinalIgnoreCase),
            $"A2：假 Unity Mono 游戏应识别为 unity_mono，实际为 {view.Family}（结果来自 {view.Source}）。sandbox：{sandbox.Root}");
        Assert.True(
            view.Confidence > 0,
            $"A2：detect 成功但 confidence={view.Confidence}，要求 >0。结果来自 {view.Source}。sandbox：{sandbox.Root}");
        Assert.True(
            view.EvidenceCount > 0,
            $"A2：detect 成功但 evidence/signals 为空，要求 evidence 非空。结果来自 {view.Source}。sandbox：{sandbox.Root}");
    }

    [E2EFact]
    public void Detect_EmptyDirectory_ReportsUnknown()
    {
        var sandbox = E2ESandbox.Create("detect-empty");
        var empty = sandbox.EmptyGameDirectory();
        var result = ForgeCli.Run("detect", empty, sandbox.Workspace, sandbox.OutRoot);

        if (result.ExitCode == 4)
        {
            throw new XunitException(
                "A2：空目录 detect 返回 E_ENGINE_UNSUPPORTED（退出码 4），但契约要求“未知目录”返回成功且 "
                + "family=unknown、confidence=0；E_ENGINE_UNSUPPORTED 用于已识别但暂不支持的引擎。"
                + $"{Environment.NewLine}{result.Describe()}");
        }

        E2EAssert.Succeeded(result, "detect（空目录）", hint: $"sandbox：{sandbox.Root}");

        var view = DetectView.Resolve(result, new[] { sandbox.Workspace, sandbox.OutRoot });
        Assert.True(
            string.Equals(view.Family, "unknown", StringComparison.OrdinalIgnoreCase),
            $"A2：空目录应返回 family=unknown，实际为 {view.Family}（结果来自 {view.Source}）。sandbox：{sandbox.Root}");
        Assert.True(
            Math.Abs(view.Confidence) <= 1e-6,
            $"A2：空目录 confidence 应为 0，实际为 {view.Confidence}（结果来自 {view.Source}）。sandbox：{sandbox.Root}");
    }
}
