using GalA11yForge.Core.Results;

namespace GalA11yForge.Generators.Internal;

/// <summary>错误对象的集中构造，避免各处的错误码/建议措辞漂移。</summary>
internal static class GeneratorErrors
{
    public static ForgeError Argument(string message, string? suggestion = null)
        => ForgeError.Create(ForgeErrorCodes.ArgumentInvalid, message, suggestion);

    public static ForgeError PathUnsafe(string message, string? suggestion = null)
        => ForgeError.Create(ForgeErrorCodes.PathUnsafe, message, suggestion);

    public static ForgeError GameWriteDenied(string message, string? suggestion = null)
        => ForgeError.Create(ForgeErrorCodes.GameWriteDenied, message, suggestion);

    public static ForgeError EngineUnsupported(string message, string? suggestion = null)
        => ForgeError.Create(ForgeErrorCodes.EngineUnsupported, message, suggestion);

    public static ForgeError DependencyMissing(string message, string? suggestion = null)
        => ForgeError.Create(ForgeErrorCodes.DependencyMissing, message, suggestion);

    public static ForgeError ArtifactInvalid(string message, string? suggestion = null)
        => ForgeError.Create(ForgeErrorCodes.ArtifactInvalid, message, suggestion);

    public static ForgeError Io(string message, string? suggestion = null)
        => ForgeError.Create(ForgeErrorCodes.Io, message, suggestion);

    public static ForgeError Internal(string message, string? suggestion = null)
        => ForgeError.Create(ForgeErrorCodes.Internal, message, suggestion);
}
