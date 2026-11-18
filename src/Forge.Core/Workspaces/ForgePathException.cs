using GalA11yForge.Core.Results;

namespace GalA11yForge.Core.Workspaces;

/// <summary>路径安全校验失败。Exception 只用于编程错误边界；CLI 应捕获并转成 ForgeResult。</summary>
public sealed class ForgePathException : Exception
{
    public ForgePathException(ForgeError error)
        : base(error.Message)
    {
        Error = error;
    }

    public ForgeError Error { get; }

    public static ForgePathException Escape(string path, string root)
        => new(ForgeError.Create(
            ForgeErrorCodes.PathUnsafe,
            $"路径越界：{path} 不在允许的根目录 {root} 之内。",
            "检查参数里的相对路径，不要使用 ..、绝对路径或盘符。"));

    public static ForgePathException GameDirectory(string path, string gameDirectory)
        => new(ForgeError.Create(
            ForgeErrorCodes.GameWriteDenied,
            $"拒绝写入游戏目录：{path}（游戏目录 {gameDirectory} 只读）。",
            "补丁产物先写到工作区；部署阶段由 IDeployer 统一备份后再写游戏目录。"));

    public static ForgePathException InvalidRelative(string relative, string reason)
        => new(ForgeError.Create(
            ForgeErrorCodes.PathUnsafe,
            $"非法相对路径 “{relative}”：{reason}。",
            "使用形如 game.exe 或 patch/a11y.dll 的相对路径，不要包含 .. 或盘符。"));

    public static ForgePathException InvalidSlug(string slug)
        => new(ForgeError.Create(
            ForgeErrorCodes.ArgumentInvalid,
            $"非法游戏标识 “{slug}”。",
            "slug 只能包含小写字母、数字、点、下划线和连字符，长度 1–64。"));
}
