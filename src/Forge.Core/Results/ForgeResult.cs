namespace GalA11yForge.Core.Results;

/// <summary>无返回值的步骤结果。</summary>
public sealed class ForgeResult
{
    private ForgeResult()
    {
    }

    public bool Ok { get; private init; }

    public ForgeError? Error { get; private init; }

    public static ForgeResult Success() => new() { Ok = true };

    public static ForgeResult Failure(ForgeError error) => new() { Ok = false, Error = error };

    public static ForgeResult Failure(string code, string message, string? suggestion = null)
        => Failure(ForgeError.Create(code, message, suggestion));
}

/// <summary>所有流水线步骤的统一返回：要么 Ok + Value，要么 Fail + 稳定错误码和用户建议。</summary>
public sealed class ForgeResult<T>
{
    private ForgeResult()
    {
    }

    public bool Ok { get; private init; }

    public T? Value { get; private init; }

    public ForgeError? Error { get; private init; }

    public static ForgeResult<T> Success(T value) => new() { Ok = true, Value = value };

    public static ForgeResult<T> Failure(ForgeError error) => new() { Ok = false, Error = error };

    public static ForgeResult<T> Failure(string code, string message, string? suggestion = null)
        => Failure(ForgeError.Create(code, message, suggestion));
}
