using Xunit.Sdk;

namespace Forge.E2E;

/// <summary>E2E 的公共断言：把“未接线”和“真失败”区分开，绝不把 E_NOT_IMPLEMENTED 当通过。</summary>
internal static class E2EAssert
{
    public static void Succeeded(CommandResult result, string step, bool allowPartial = false, string? hint = null)
    {
        var hintText = string.IsNullOrWhiteSpace(hint)
            ? string.Empty
            : $"{Environment.NewLine}提示：{hint}";

        if (result.TimedOut)
        {
            throw new XunitException(
                $"{step} 超时（>{result.Elapsed.TotalSeconds:F0}s），已终止进程。{hintText}{Environment.NewLine}{result.Describe()}");
        }

        if (result.ExitCode == 0)
        {
            return;
        }

        if (allowPartial && result.ExitCode == 5)
        {
            // 部分成功：产物仍要由调用方断言；这里只放行退出码。
            return;
        }

        if (string.Equals(result.Code, ForgeCli.NotImplementedCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new XunitException(
                $"接线未完成：{step} 返回 E_NOT_IMPLEMENTED（退出码 {result.ExitCode}）。"
                + "CLI 的七步引擎尚未接入，这不是夹具或测试代码的问题；请 Lead 完成接线后重跑。"
                + $"{hintText}{Environment.NewLine}{result.Describe()}");
        }

        throw new XunitException(
            $"{step} 失败（退出码 {result.ExitCode}"
            + (string.IsNullOrWhiteSpace(result.Code) ? string.Empty : $"，code={result.Code}")
            + $"）。{hintText}{Environment.NewLine}{result.Describe()}");
    }

    public static void HasValue(System.Text.Json.JsonElement root, string property, string context)
    {
        if (!JsonHelpers.HasValue(root, property))
        {
            throw new XunitException($"{context} 缺少字段 {property}：{JsonHelpers.Format(root)}");
        }
    }
}
