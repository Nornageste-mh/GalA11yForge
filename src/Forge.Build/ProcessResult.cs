namespace GalA11yForge.Build;

/// <summary>外部进程的完整结果。TimedOut / Canceled 与普通非零退出码分开，便于给出不同错误码。</summary>
public sealed class ProcessResult
{
    public int ExitCode { get; init; } = -1;

    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;

    public bool TimedOut { get; init; }

    public bool Canceled { get; init; }

    public TimeSpan Duration { get; init; }

    public bool Succeeded => !TimedOut && !Canceled && ExitCode == 0;
}
