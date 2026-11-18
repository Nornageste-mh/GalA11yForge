using System.Text;
using GalA11yForge.Core.Models;

namespace GalA11yForge.Adapters.Renpy.Tests;

/// <summary>
/// 合成 Ren'Py 安装目录夹具：临时目录里造 renpy/、game/、lib/、假 .rpa。
/// 不依赖任何真实游戏；测试结束递归删除。
/// </summary>
internal sealed class RenpyFixture : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public RenpyFixture(string flavor = "6.99", string gameName = "SyntheticGal")
    {
        Root = Path.Combine(Path.GetTempPath(), "gaforge-renpy-tests", Guid.NewGuid().ToString("N"));
        GameName = gameName;
        GameDirectory = Path.Combine(Root, "installed", gameName);
        Workspace = Path.Combine(Root, "workspace");

        Directory.CreateDirectory(Path.Combine(GameDirectory, "renpy"));
        Directory.CreateDirectory(Path.Combine(GameDirectory, "game"));
        Directory.CreateDirectory(Workspace);

        WriteText(Path.Combine(GameDirectory, gameName + ".exe"), string.Empty);
        WriteText(Path.Combine(GameDirectory, "a11y_say.vbs"), "' synthetic fixture\n");

        if (flavor == "6.99")
        {
            WriteText(Path.Combine(GameDirectory, "renpy", "vc_version.py"), "vc_version = 2187\n");
            WriteText(Path.Combine(GameDirectory, "renpy", "__init__.py"),
                """
                from renpy.vc_version import vc_version

                version_tuple = (6, 99, 12, 4, vc_version)
                version_name = "We get the job done."

                def version_only():
                    return ".".join(str(i) for i in version_tuple[:4])

                version = "Ren'Py " + version_only()
                """);
            Directory.CreateDirectory(Path.Combine(GameDirectory, "lib", "windows-i686"));
            WriteText(Path.Combine(GameDirectory, "lib", "windows-i686", "pythonw.exe"), string.Empty);
        }
        else if (flavor == "8.5")
        {
            WriteText(Path.Combine(GameDirectory, "renpy", "vc_version.py"),
                """
                version = '8.5.2.26010301'
                version_name = 'In Good Health'
                branch = 'fix'
                nightly = False
                official = True
                """);
            WriteText(Path.Combine(GameDirectory, "renpy", "__init__.py"),
                """
                from renpy.vc_version import version

                version_tuple = VersionTuple(*(int(i) for i in version.split(".")))
                """);
            Directory.CreateDirectory(Path.Combine(GameDirectory, "lib", "py3-windows-x86_64"));
            WriteText(Path.Combine(GameDirectory, "lib", "py3-windows-x86_64", "pythonw.exe"), string.Empty);
            WriteText(Path.Combine(GameDirectory, "renpy", "common", "00accessibility.rpy"), "# synthetic\n");
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(flavor), flavor, "只支持 6.99 / 8.5");
        }

        WriteHooks(includeGetScreen: flavor != "6.99");

        AddRpa("archive.rpa", 4096);
        AddRpa("images.rpa", 2048);

        WriteText(Path.Combine(GameDirectory, "game", "script.rpy"),
            "label start:\n    \"synthetic line\"\n    return\n");
        WriteText(Path.Combine(GameDirectory, "game", "options.rpy"),
            "define config.name = \"SyntheticGal\"\n");
    }

    public string Root { get; }

    public string GameName { get; }

    public string GameDirectory { get; }

    public string Workspace { get; }

    public string GeneratedDirectory => Path.Combine(Workspace, "games", "synthetic-gal", "generated");

    public GameContext Context(ProfileDocument? profile = null, string slug = "synthetic-gal") => new()
    {
        GameDirectory = GameDirectory,
        Slug = slug,
        WorkspaceRoot = Workspace,
        Profile = profile,
    };

    public RenpyFixture WithExistingOverlay()
    {
        WriteText(Path.Combine(GameDirectory, "game", "a11y_platform", "00_contract.rpy"), "# old patch\n");
        WriteText(Path.Combine(GameDirectory, "game", "a11y_game", "90_plugin.rpy"), "# old patch\n");
        return this;
    }

    public string WriteText(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, text.Replace("\r\n", "\n", StringComparison.Ordinal), Utf8NoBom);
        return path;
    }

    private void AddRpa(string name, int size)
    {
        var bytes = new byte[size];
        var header = Encoding.ASCII.GetBytes("RPA-3.0 0000000000000000 00000000\n");
        header.CopyTo(bytes, 0);
        for (var i = header.Length; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        var path = Path.Combine(GameDirectory, "game", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private void WriteHooks(bool includeGetScreen)
    {
        WriteText(Path.Combine(GameDirectory, "renpy", "ast.py"),
            """
            # synthetic ast
            class Say:
                def execute(self):
                    if renpy.config.say_menu_text_filter:
                        filtered = renpy.config.say_menu_text_filter(text)
                    return filtered
            """);

        WriteText(Path.Combine(GameDirectory, "renpy", "config.py"),
            """
            say_menu_text_filter = None
            tts_function = None
            periodic_callbacks = []
            """);

        WriteText(Path.Combine(GameDirectory, "renpy", "display", "focus.py"),
            """
            focus_list = []
            """);

        if (includeGetScreen)
        {
            WriteText(Path.Combine(GameDirectory, "renpy", "exports.py"),
                """
                def get_screen(name):
                    return None
                """);
        }
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
        catch (IOException)
        {
            // 测试清理失败不影响结论。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>与 RenpyFixture 无关的临时工作区，真实游戏只读用例用它承接生成物。</summary>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace(string name)
    {
        Root = Path.Combine(Path.GetTempPath(), "gaforge-renpy-tests", Guid.NewGuid().ToString("N"), name);
        Workspace = Path.Combine(Root, "workspace");
        Directory.CreateDirectory(Workspace);
    }

    public string Root { get; }

    public string Workspace { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
