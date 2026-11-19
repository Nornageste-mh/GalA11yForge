namespace GalA11yForge.Gui;

internal static class Program
{
    private static readonly string CrashLogPath = Path.Combine(
        Path.GetTempPath(),
        "GalA11yForge-gui-crash.log");

    [STAThread]
    private static void Main(string[] args)
    {
        // 高 DPI 必须在创建任何窗口之前设置；PerMonitorV2 让 150% 缩放下文字不糊。
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 老系统不支持 PerMonitorV2 时退回系统默认，不阻断启动。
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            Application.SetDefaultFont(new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 缺字体也不影响使用，退回系统默认字体。
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception);

        var options = GuiStartupOptions.Parse(args);
        Application.Run(new MainForm(options));
    }

    private static void ReportFatal(Exception? exception)
    {
        var text = exception?.ToString() ?? "未知错误（异常对象为空）。";
        try
        {
            File.AppendAllText(CrashLogPath, $"[{DateTimeOffset.Now:O}]{Environment.NewLine}{text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 日志写不下去也不能再抛，否则会盖住原始错误。
        }

        MessageBox.Show(
            $"界面遇到未处理的错误：{Environment.NewLine}{Environment.NewLine}{exception?.Message}{Environment.NewLine}{Environment.NewLine}详细信息已写入：{CrashLogPath}{Environment.NewLine}请把该文件交给开发者。",
            "Gal 无障碍辅助 Demo 快速生成工具发生错误",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
