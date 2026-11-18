using System.Text;

namespace GalA11yForge.Detection.Tests;

/// <summary>
/// 临时游戏目录夹具：建在 %TEMP%\gala11y-detection-tests\&lt;label&gt;-&lt;guid&gt;，
/// Dispose 时递归删除；不触碰任何真实游戏目录。
/// </summary>
internal sealed class TempGameDirectory : IDisposable
{
    public TempGameDirectory(string label)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "gala11y-detection-tests",
            label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateDirectory(string relativePath)
    {
        var fullPath = Resolve(relativePath);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    public string WriteText(string relativePath, string content) =>
        WriteBytes(relativePath, Encoding.UTF8.GetBytes(content));

    public string WriteBytes(string relativePath, byte[] bytes)
    {
        var fullPath = Resolve(relativePath);
        var parent = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllBytes(fullPath, bytes);
        return fullPath;
    }

    public string Resolve(string relativePath) =>
        System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响测试结论，交给系统清理。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>构造最小合法 PE 头与 Unity 头部字节。</summary>
internal static class PeFixtures
{
    /// <summary>MZ + e_lfanew=0x80 + PE\0\0 + Machine；其余补零。</summary>
    public static byte[] PeImage(ushort machine, int size = 1024)
    {
        var bytes = new byte[Math.Max(size, 0x400)];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        bytes[0x80] = (byte)'P';
        bytes[0x81] = (byte)'E';
        bytes[0x82] = 0;
        bytes[0x83] = 0;
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        return bytes;
    }

    /// <summary>Unity globalgamemanagers：版本串放在实测的统一偏移 48。</summary>
    public static byte[] UnityManagers(string version)
    {
        var bytes = new byte[256];
        Encoding.ASCII.GetBytes(version).CopyTo(bytes, 48);
        return bytes;
    }

    /// <summary>XP3 文件头魔数（11 字节）。</summary>
    public static byte[] Xp3Magic() => new byte[]
    {
        0x58, 0x50, 0x33, 0x0D, 0x0A, 0x20, 0x0A, 0x1A, 0x8B, 0x67, 0x01,
    };
}

/// <summary>按指纹库规则拼出各引擎的最小可识别目录。</summary>
internal static class GameFixtures
{
    public static TempGameDirectory UnityMono()
    {
        var game = new TempGameDirectory("unity-mono");
        game.WriteBytes("Game.exe", PeFixtures.PeImage(0x8664));
        game.WriteBytes("UnityPlayer.dll", new byte[200_000]);
        game.WriteBytes("Game_Data/Managed/Assembly-CSharp.dll", new byte[50_000]);
        game.WriteBytes("Game_Data/globalgamemanagers", PeFixtures.UnityManagers("2022.3.43f1"));
        game.WriteBytes("winhttp.dll", new byte[8]);
        game.WriteText("BepInEx/doorstop_config.ini", "[General]\nenabled=true\n");
        return game;
    }

    public static TempGameDirectory UnityIl2Cpp()
    {
        var game = new TempGameDirectory("unity-il2cpp");
        game.WriteBytes("Game.exe", PeFixtures.PeImage(0x014c));
        game.WriteBytes("GameAssembly.dll", new byte[64]);
        game.WriteBytes("Game_Data/il2cpp_data/Metadata/global-metadata.dat", new byte[32]);
        return game;
    }

    public static TempGameDirectory Renpy()
    {
        var game = new TempGameDirectory("renpy");
        game.WriteText("renpy/__init__.py", "version_tuple = (7, 4, 11, 0, vc_version)\nversion = \"Ren'Py \" + version_only\n");
        game.WriteText("renpy/vc_version.py", "vc_version = 2266\n");
        game.WriteText("game/script.rpy", "label start:\n    return\n");
        game.WriteBytes("game/audio.rpa", new byte[64]);
        game.WriteBytes("DDLC.exe", PeFixtures.PeImage(0x014c));
        return game;
    }

    public static TempGameDirectory KirikiriZ()
    {
        var game = new TempGameDirectory("kirikiri-z");
        game.WriteBytes("data.xp3", PeFixtures.Xp3Magic());
        game.WriteBytes("data.xp3.sig", new byte[212]);

        // 参考实现与实测都要求 exe 里的 "TVP(KIRIKIRI)" 按 UTF-16LE 搜索。
        var exe = PeFixtures.PeImage(0x014c, 4096);
        Encoding.Unicode.GetBytes("TVP(KIRIKIRI)").CopyTo(exe, 0x200);
        game.WriteBytes("game.exe", exe);
        return game;
    }

    public static TempGameDirectory GameCreatorElectron()
    {
        var game = new TempGameDirectory("gamecreator");
        game.WriteText("index.html", "<html><body>GameCreator 橙光</body></html>");
        game.WriteText("script.js", "console.log('gc');");
        game.WriteBytes("resources/app.asar", new byte[64]);
        return game;
    }

    public static TempGameDirectory MkxpRgss()
    {
        var game = new TempGameDirectory("mkxp");
        game.WriteBytes("Game.rgssad", new byte[] { 0x52, 0x47, 0x53, 0x53, 0x41, 0x44, 0x00, 0x01 });
        game.WriteBytes("Game.exe", PeFixtures.PeImage(0x014c));
        return game;
    }

    public static TempGameDirectory WebGal()
    {
        var game = new TempGameDirectory("webgal");
        game.WriteText("game/config.txt", "Game_name: 测试;\n");
        game.CreateDirectory("game/scene");
        return game;
    }

    /// <summary>泛 Electron / NW.js 布局：只有 resources/app.asar，没有橙光的 script.js。</summary>
    public static TempGameDirectory ElectronGeneric()
    {
        var game = new TempGameDirectory("electron");
        game.WriteBytes("resources/app.asar", new byte[64]);
        game.WriteBytes("game.exe", PeFixtures.PeImage(0x8664));
        return game;
    }
}
