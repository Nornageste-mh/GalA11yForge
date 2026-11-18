namespace GalA11yForge.Core.Results;

/// <summary>
/// 稳定错误码。适配器作者只应依赖这些字符串，不要依赖异常文本。
/// 与 CLI 退出码的映射见 docs/06-接口契约-v1.md。
/// </summary>
public static class ForgeErrorCodes
{
    public const string ArgumentInvalid = "E_ARG_INVALID";
    public const string PathUnsafe = "E_PATH_UNSAFE";
    public const string GameWriteDenied = "E_GAME_WRITE_DENIED";
    public const string DependencyMissing = "E_DEPENDENCY_MISSING";
    public const string EngineUnsupported = "E_ENGINE_UNSUPPORTED";
    public const string NotImplemented = "E_NOT_IMPLEMENTED";
    public const string Partial = "E_PARTIAL";
    public const string Io = "E_IO";
    public const string AdapterNotFound = "E_ADAPTER_NOT_FOUND";
    public const string AdapterInvalid = "E_ADAPTER_INVALID";
    public const string ProfileInvalid = "E_PROFILE_INVALID";
    public const string ArtifactMissing = "E_ARTIFACT_MISSING";
    public const string ArtifactInvalid = "E_ARTIFACT_INVALID";
    public const string Internal = "E_INTERNAL";
}
