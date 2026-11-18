using System.Text;
using GalA11yForge.ExternalReader;
using GalA11yForge.ExternalReader.Demo;
using GalA11yForge.ExternalReader.Native;
using GalA11yForge.ExternalReader.Runtime;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        var o = Options.Parse(args);
        if (o.Mode == "help" || args.Length == 0)
        {
            Console.WriteLine(Options.Usage);
            return 0;
        }

        // 必须在任何窗口/UIA 调用之前：否则 150% 缩放下截图区域与 UIA 坐标会对不上
        Win32.EnableDpiAwareness();

        using var log = Log.Create(o.LogPath, o.Mode);
        log.Evt("start", null,
            ("mode", o.Mode), ("args", string.Join(' ', args)), ("version", "0.1.0.0"),
            ("os", Environment.OSVersion.VersionString), ("x64", Environment.Is64BitProcess),
            ("cwd", Environment.CurrentDirectory), ("runId", log.RunId), ("log", log.TextPath));

        var code = 0;
        try
        {
            code = o.Mode switch
            {
                "probe" => ProbeModes.Probe(o, log),
                "dump" => ProbeModes.Dump(o, log),
                "ocr-shot" => ProbeModes.OcrShot(o, log),
                "demo" => DemoWindow.Run(o, log),
                "events" => EventMode.Run(o, log),
                "run" => RunReader(o, log),
                _ => Unknown(o, log),
            };
        }
        catch (Exception ex)
        {
            log.Evt("fatal", ex.GetType().Name + ": " + ex.Message, ("stack", ex.StackTrace));
            Console.Error.WriteLine("fatal: " + ex);
            code = 1;
        }
        finally
        {
            log.Evt("exit", null, ("code", code), ("elapsedMs", log.ElapsedMs));
        }
        return code;
    }

    private static int RunReader(Options o, Log log)
    {
        using var engine = new ReaderEngine(o, log);
        return engine.Run();
    }

    private static int Unknown(Options o, Log log)
    {
        log.Evt("mode.unknown", o.Mode);
        Console.Error.WriteLine("未知模式: " + o.Mode);
        Console.WriteLine(Options.Usage);
        return 64;
    }
}
