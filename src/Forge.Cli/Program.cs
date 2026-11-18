using System.Text;
using GalA11yForge.Cli;

UseUtf8WhenRedirected();

return await CliApplication.RunAsync(args, Console.Out, Console.Error);

// GUI / 其它程序会把我们的 stdout、stderr 当 UTF-8 解码（例如 Forge.Gui 的 ForgeProcessRunner）。
// 子进程完全没有控制台时，.NET 的控制台流可能按系统 OEM 代码页输出，中文就会变成乱码；
// 只要输出/错误被重定向，就显式声明 UTF-8。失败也不影响功能，只是保持默认。
static void UseUtf8WhenRedirected()
{
    try
    {
        if (Console.IsOutputRedirected || Console.IsErrorRedirected)
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
    }
    catch (IOException)
    {
    }
    catch (PlatformNotSupportedException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
}
