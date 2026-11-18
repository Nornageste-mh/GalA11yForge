namespace Forge.E2E;

/// <summary>串联流水线前置步骤；每一步都明确区分“未接线”与“失败”。</summary>
internal static class Pipeline
{
    public static CommandResult Detect(E2ESandbox sandbox, string game)
    {
        var result = ForgeCli.Run("detect", game, sandbox.Workspace, sandbox.OutRoot);
        E2EAssert.Succeeded(result, "detect（前置步骤）", hint: $"sandbox：{sandbox.Root}");
        return result;
    }

    public static CommandResult Analyze(E2ESandbox sandbox, string game)
    {
        var result = ForgeCli.Run("analyze", game, sandbox.Workspace, sandbox.OutRoot);
        E2EAssert.Succeeded(result, "analyze（前置步骤）", allowPartial: true, hint: $"sandbox：{sandbox.Root}");
        return result;
    }

    public static CommandResult Plan(E2ESandbox sandbox, string game)
    {
        var result = ForgeCli.Run("plan", game, sandbox.Workspace, sandbox.OutRoot);
        E2EAssert.Succeeded(result, "plan（前置步骤）", allowPartial: true, hint: $"sandbox：{sandbox.Root}");
        return result;
    }

    public static CommandResult Generate(E2ESandbox sandbox, string game)
    {
        var result = ForgeCli.Run("generate", game, sandbox.Workspace, sandbox.OutRoot);
        E2EAssert.Succeeded(result, "generate（前置步骤）", allowPartial: true, hint: $"sandbox：{sandbox.Root}");
        return result;
    }

    public static CommandResult Build(E2ESandbox sandbox, string game, string hint)
    {
        var result = ForgeCli.Run("build", game, sandbox.Workspace, sandbox.OutRoot);
        E2EAssert.Succeeded(result, "build（install 前置步骤）", allowPartial: false, hint: hint);
        return result;
    }
}
