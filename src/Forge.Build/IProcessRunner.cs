namespace GalA11yForge.Build;

/// <summary>可替换的外部进程执行器，测试里用假实现验证超时/失败分支。</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
