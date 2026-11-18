using GalA11yForge.Core.Results;

namespace GalA11yForge.Cli;

/// <summary>CLI 退出码。0 成功、2 参数错误、3 依赖/能力缺失、4 引擎不支持、5 部分成功；1 用于未预期内部错误。</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int InternalError = 1;
    public const int ArgumentError = 2;
    public const int DependencyMissing = 3;
    public const int EngineUnsupported = 4;
    public const int PartialSuccess = 5;

    public static int FromError(ForgeError? error)
    {
        if (error is null)
        {
            return Success;
        }

        return error.Code switch
        {
            ForgeErrorCodes.ArgumentInvalid or ForgeErrorCodes.PathUnsafe or ForgeErrorCodes.ProfileInvalid
                => ArgumentError,
            ForgeErrorCodes.DependencyMissing or ForgeErrorCodes.NotImplemented or ForgeErrorCodes.AdapterNotFound
                or ForgeErrorCodes.AdapterInvalid or ForgeErrorCodes.Io
                => DependencyMissing,
            ForgeErrorCodes.EngineUnsupported => EngineUnsupported,
            ForgeErrorCodes.Partial or ForgeErrorCodes.ArtifactMissing or ForgeErrorCodes.ArtifactInvalid
                => PartialSuccess,
            ForgeErrorCodes.GameWriteDenied => ArgumentError,
            _ => InternalError,
        };
    }
}

public enum CliCommand
{
    None = 0,
    Detect,
    Analyze,
    Plan,
    Generate,
    Build,
    Install,
    Verify,
    Report,
    Doctor,
    Tools,
    Help,
    Version,
}

/// <summary>解析后的命令行。路径保留用户原样，由 CliApplication 做存在性与安全校验。</summary>
public sealed class CliOptions
{
    public CliCommand Command { get; init; }

    public string? GamePath { get; init; }

    public string? OutputPath { get; init; }

    public string? WorkspacePath { get; init; }

    public bool Json { get; init; }

    public bool DryRun { get; init; }

    public bool Verbose { get; init; }

    public bool Advanced { get; init; }

    /// <summary>install/build 使用的 BepInEx 离线包路径；为空时在工作区缓存里找。</summary>
    public string? BepInExPath { get; init; }

    /// <summary>
    /// 语音运行期 DLL 目录（Tolk.dll、nvdaControllerClient64.dll 等，用户自备、不随产品分发）。
    /// 为空时 IL2CPP 安装仍会继续，但会警告“很可能无法朗读”。
    /// </summary>
    public string? SpeechRuntimePath { get; init; }

    /// <summary>
    /// verify 用的语音日志路径：给“安装后到底有没有发声”检查用。
    /// 为空时自动找 BepInEx/LogOutput.log 或 Ren'Py 的 a11y_speech.log。
    /// </summary>
    public string? SpeechLogPath { get; init; }

    /// <summary>verify --rollback：先按安装清单回滚，再跑回滚后检查。</summary>
    public bool Rollback { get; init; }

    /// <summary>install --force：允许覆盖已存在的部署（默认拒绝二次部署）。</summary>
    public bool Force { get; init; }

    /// <summary>--option key=value：覆盖 profile.options 的运行时选项（可重复）。</summary>
    public Dictionary<string, string> OptionOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CliParseResult
{
    public CliOptions? Options { get; init; }

    public ForgeError? Error { get; init; }

    public bool HelpRequested { get; init; }

    public bool VersionRequested { get; init; }
}
