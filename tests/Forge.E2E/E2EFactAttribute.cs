using Xunit;

namespace Forge.E2E;

/// <summary>
/// E2E 专用 Fact：默认跳过，只有显式设置 FORGE_E2E=1 时才真正执行。
///
/// 为什么不用运行时 SkipException：xunit 2.5.3 里在测试体内抛 SkipException 会被判为“失败”，
/// 不会计入 skipped。FactAttribute.Skip 在 2.5.3 是 virtual，因此这里在发现阶段直接返回跳过原因，
/// 得到真正的 skipped 状态，且不引入额外依赖。
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class E2EFactAttribute : FactAttribute
{
    public const string SwitchVariable = "FORGE_E2E";

    private static readonly string? SkipReason = ResolveSkipReason();

    public override string? Skip
    {
        get => SkipReason ?? base.Skip;
        set => base.Skip = value;
    }

    private static string? ResolveSkipReason()
    {
        var value = Environment.GetEnvironmentVariable(SwitchVariable);
        if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"E2E 集成测试默认跳过：设置环境变量 {SwitchVariable}=1 后重跑。"
             + "首次运行前先执行 tests/fixtures/make-fixture.ps1 生成假游戏夹具。"
             + $"当前 {SwitchVariable}={value ?? "(未设置)"}。";
    }
}
