namespace GalA11yForge.Analyzers.Ghidra.Tests;

/// <summary>每个测试独占的临时目录，测试结束递归删除（失败不抛）。</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "gala11y-ghidra-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Sub(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 有句柄（例如被杀进程未释放）时留给系统临时目录清理。
        }
    }
}
