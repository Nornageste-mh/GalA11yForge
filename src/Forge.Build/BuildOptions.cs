namespace GalA11yForge.Build;

/// <summary>构建选项。所有路径都会被校验：构建产物与日志只能落在工作区内。</summary>
public sealed class BuildOptions
{
    /// <summary>dotnet 可执行文件。为空时依次尝试 GALA11Y_DOTNET、DOTNET_HOST_PATH、D:\dotnet\dotnet.exe、PATH。</summary>
    public string? DotnetPath { get; set; }

    public string Configuration { get; set; } = "Release";

    /// <summary>编译输出目录；为空时在工作区 work/ 下新建。</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>日志目录；为空时使用工作区 logs/。</summary>
    public string? LogDirectory { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>传给 dotnet build 的 --no-restore。</summary>
    public bool NoRestore { get; set; }

    /// <summary>可选 -f 目标框架。</summary>
    public string? Framework { get; set; }

    /// <summary>可选 /p:Version=...，用于给插件 DLL 打版本。</summary>
    public string? Version { get; set; }

    /// <summary>构建前清空输出目录里的旧文件（只允许工作区内）。</summary>
    public bool CleanOutputDirectory { get; set; } = true;

    /// <summary>
    /// 额外 MSBuild 属性（输出为 -p:Key=Value）。按 key 排序后传入，保证命令确定性。
    /// 用于把 BepInExDir / GameDir / ManagedDir 等指向工作区暂存目录，避免构建依赖游戏目录里已装 BepInEx。
    /// </summary>
    public SortedDictionary<string, string>? Properties { get; set; }

    public BuildOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(Configuration))
        {
            throw new ArgumentException("构建配置不能为空。", nameof(Configuration));
        }

        if (Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout), "构建超时必须大于 0。");
        }

        if (Version is not null && Version.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '.' and not '-' and not '+'))
        {
            throw new ArgumentException($"版本号含非法字符：{Version}", nameof(Version));
        }

        if (Properties is not null)
        {
            foreach (var pair in Properties)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Any(ch => char.IsWhiteSpace(ch) || ch is '"' or '=' or ';'))
                {
                    throw new ArgumentException($"MSBuild 属性名不合法：{pair.Key}", nameof(Properties));
                }

                if (pair.Value is null || pair.Value.Any(ch => ch is '"' or '\r' or '\n'))
                {
                    throw new ArgumentException($"MSBuild 属性值不合法（不能含引号或换行）：{pair.Key}", nameof(Properties));
                }
            }
        }

        return this;
    }
}
