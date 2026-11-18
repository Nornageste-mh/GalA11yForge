using System.Windows.Forms.Automation;

namespace GalA11yForge.Gui;

/// <summary>
/// 读屏播报通道。用 UIA 通知（RaiseAutomationNotification）把「执行完成 / 失败原因」推给 NVDA 等读屏，
/// 不抢焦点、不弹窗。没有读屏或系统太旧时通知会静默失败，这里全部吞掉异常：
/// 播报是增强，永远不能让它把主流程带崩。
/// </summary>
public sealed class Announcer
{
    private readonly Control _anchor;

    public Announcer(Control anchor)
    {
        _anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
    }

    /// <summary>已尝试播报的次数（测试/诊断用）。</summary>
    public int Attempts { get; private set; }

    /// <summary>播报一段短文本。建议 &lt; 200 字，只放结论，细节让用户去结果区读。</summary>
    public void Announce(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Attempts++;
        try
        {
            _anchor.AccessibilityObject?.RaiseAutomationNotification(
                AutomationNotificationKind.ActionCompleted,
                AutomationNotificationProcessing.MostRecent,
                text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 没有 UIA 客户端、系统不支持通知、窗口句柄已销毁 —— 都不影响功能。
        }
    }
}
