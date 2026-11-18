namespace GalA11yForge.Core.Tools;

/// <summary>RE 工具的类型：可执行文件、Python 包、npm 包。检查方式不同，报告里要分开说。</summary>
public enum ReToolKind
{
    Executable = 0,
    PythonPackage = 1,
    NpmPackage = 2,
}

/// <summary>
/// 一个外部 RE 工具的静态描述。许可证与下载地址只做“指路”，产品不自动下载、也不随包分发。
/// 许可证字段在 task-56 核实前一律写“待核实”，避免对外说错。
/// </summary>
public sealed record ReToolDescriptor(
    string Id,
    string DisplayName,
    ReToolKind Kind,
    IReadOnlyList<string> FileNames,
    string License,
    string DownloadUrl,
    string PlacementHint);

/// <summary>
/// RE 工具清单。顺序即报告顺序；与用户 2026-11-15 邮件里要求的四件套对应。
/// 检查只做“本机是否找得到”，不联网、不自动安装。
/// </summary>
public static class ReToolCatalog
{
    /// <summary>显式路径覆盖的 --option 键格式：re.&lt;id&gt;.path。</summary>
    public const string OptionPathFormat = "re.{0}.path";

    public static IReadOnlyList<ReToolDescriptor> All { get; } = new ReToolDescriptor[]
    {
        new(
            "il2cppdumper",
            "Il2CppDumper",
            ReToolKind.Executable,
            new[] { "Il2CppDumper.exe" },
            "MIT（可随包；当前产品不随包分发）",
            "https://github.com/Perfare/Il2CppDumper/releases",
            "解压后保持 Il2CppDumper.exe 在工具目录顶层；forge 会自动调用它把 GameAssembly.dll + global-metadata.dat 转成 dump.cs。"),
        new(
            "pyghidra",
            "pyghidra",
            ReToolKind.PythonPackage,
            Array.Empty<string>(),
            "Apache-2.0",
            "https://pypi.org/project/pyghidra/",
            "在用于分析的 Python 环境里 pip install pyghidra；版本需与本机 Ghidra 12.1.4 兼容。产品只做发现，不自动安装。"),
        new(
            "frida",
            "Frida（frida-tools）",
            ReToolKind.Executable,
            new[] { "frida-ps.exe", "frida.exe" },
            "wxWindows 3.1（不进发行包）",
            "https://frida.re/docs/installation/",
            "pip install frida-tools 后 frida-ps.exe 通常在 Python 的 Scripts 目录；仅用于研究/动态验证，不随产品分发。"),
        new(
            "frida-il2cpp-bridge",
            "frida-il2cpp-bridge",
            ReToolKind.NpmPackage,
            Array.Empty<string>(),
            "MIT（不进发行包）",
            "https://github.com/vfsfitvnm/frida-il2cpp-bridge",
            "npm 安装到研究工程；与 frida 一起用于动态枚举类/字段，不进入产品运行时。"),
        new(
            "dnspyex",
            "dnSpyEx",
            ReToolKind.Executable,
            new[] { "dnSpy.exe", "dnSpy.Console.exe" },
            "GPLv3（只在本机手动使用，不随产品分发）",
            "https://github.com/dnSpyEx/dnSpy/releases",
            "解压到工具目录；只供人工查看 .NET 程序集，产品不自动调用、不打包。"),
    };

    public static string PathOptionKey(string id) => string.Format(OptionPathFormat, id);
}
