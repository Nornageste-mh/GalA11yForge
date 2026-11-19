using System.Text.Json;
using GalA11yForge.Cli;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Core.Tests;
using GalA11yForge.Cli;



public sealed class CliTests
{
    [Fact]
    public void InstallNotice_ForIl2CppWithoutSpeechRuntime_TellsUserItIsLogOnly()
    {
        var data = new Dictionary<string, object?>();
        var message = InstallNotice.Compose(isIl2Cpp: true, speechRuntimePath: null, data);

        Assert.Contains("安装完成", message, StringComparison.Ordinal);
        Assert.Contains("--speech-runtime", message, StringComparison.Ordinal);
        Assert.Contains("只写日志", message, StringComparison.Ordinal);
        Assert.Equal(true, data["speechRuntimeMissing"]);
    }

    [Fact]
    public void InstallNotice_WithSpeechRuntimeOrMono_StaysPlain()
    {
        var data = new Dictionary<string, object?>();
        var withRuntime = InstallNotice.Compose(true, "C:\\Tolk", data);
        Assert.StartsWith("安装完成。", withRuntime, StringComparison.Ordinal);
        Assert.DoesNotContain("没有指定 --speech-runtime", withRuntime, StringComparison.Ordinal);
        Assert.False(data.ContainsKey("speechRuntimeMissing"));
        var mono = InstallNotice.Compose(false, null, new Dictionary<string, object?>());
        Assert.StartsWith("安装完成。", mono, StringComparison.Ordinal);
        Assert.DoesNotContain("没有指定 --speech-runtime", mono, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AllOptions()
    {
        var result = CommandLineParser.Parse(
        [
            "detect", "--game", "D:\\Games\\Sample", "--out", "out", "--workspace", "ws",
            "--json", "--dry-run", "--verbose", "--advanced",
        ]);

        Assert.Null(result.Error);
        Assert.NotNull(result.Options);
        Assert.Equal(CliCommand.Detect, result.Options!.Command);
        Assert.Equal("D:\\Games\\Sample", result.Options.GamePath);
        Assert.Equal("out", result.Options.OutputPath);
        Assert.Equal("ws", result.Options.WorkspacePath);
        Assert.True(result.Options.Json);
        Assert.True(result.Options.DryRun);
        Assert.True(result.Options.Verbose);
        Assert.True(result.Options.Advanced);
    }

    [Fact]
    public void Parse_SupportsEqualsSyntax()
    {
        var result = CommandLineParser.Parse(["plan", "--game=D:\\Games\\Sample", "--workspace=ws"]);

        Assert.Null(result.Error);
        Assert.Equal(CliCommand.Plan, result.Options!.Command);
        Assert.Equal("D:\\Games\\Sample", result.Options.GamePath);
        Assert.Equal("ws", result.Options.WorkspacePath);
    }

    [Fact]
    public void Parse_OptionOverrides_AreCollected()
    {
        var result = CommandLineParser.Parse(
        [
            "analyze", "--game", "D:\\Games\\Sample", "--option", "il2cpp.dumpPath=D:\\dump.cs",
            "--option=ghidra.path=D:\\ghidra",
        ]);

        Assert.Null(result.Error);
        Assert.NotNull(result.Options);
        Assert.Equal(2, result.Options!.OptionOverrides.Count);
        Assert.Equal("D:\\dump.cs", result.Options.OptionOverrides["il2cpp.dumpPath"]);
        Assert.Equal("D:\\ghidra", result.Options.OptionOverrides["ghidra.path"]);
    }

    [Fact]
    public void Parse_OptionWithoutEquals_ReturnsArgumentError()
    {
        var result = CommandLineParser.Parse(["detect", "--option", "badformat"]);

        Assert.NotNull(result.Error);
        Assert.Equal(ForgeErrorCodes.ArgumentInvalid, result.Error!.Code);
        Assert.Contains("key=value", result.Error.Message);
    }

    [Fact]
    public void Parse_UnknownOption_ReturnsArgumentError()
    {
        var result = CommandLineParser.Parse(["detect", "--bogus"]);

        Assert.NotNull(result.Error);
        Assert.Equal(ForgeErrorCodes.ArgumentInvalid, result.Error!.Code);
        Assert.Contains("未知参数", result.Error.Message);
    }

    [Fact]
    public void Parse_MissingValue_ReturnsArgumentError()
    {
        var result = CommandLineParser.Parse(["detect", "--game"]);

        Assert.NotNull(result.Error);
        Assert.Equal(ForgeErrorCodes.ArgumentInvalid, result.Error!.Code);
        Assert.Contains("缺少值", result.Error.Message);
    }

    [Fact]
    public void Parse_NoArgs_ReturnsArgumentError()
    {
        var result = CommandLineParser.Parse([]);

        Assert.NotNull(result.Error);
        Assert.Equal(ForgeErrorCodes.ArgumentInvalid, result.Error!.Code);
    }

    [Fact]
    public void Parse_UnknownCommand_ReturnsArgumentError()
    {
        var result = CommandLineParser.Parse(["frobnicate"]);

        Assert.NotNull(result.Error);
        Assert.Contains("未知命令", result.Error!.Message);
    }

    [Fact]
    public void Parse_Help_IsRequested()
    {
        var result = CommandLineParser.Parse(["--help"]);

        Assert.True(result.HelpRequested);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData(ForgeErrorCodes.ArgumentInvalid, ExitCodes.ArgumentError)]
    [InlineData(ForgeErrorCodes.PathUnsafe, ExitCodes.ArgumentError)]
    [InlineData(ForgeErrorCodes.DependencyMissing, ExitCodes.DependencyMissing)]
    [InlineData(ForgeErrorCodes.NotImplemented, ExitCodes.DependencyMissing)]
    [InlineData(ForgeErrorCodes.AdapterNotFound, ExitCodes.DependencyMissing)]
    [InlineData(ForgeErrorCodes.EngineUnsupported, ExitCodes.EngineUnsupported)]
    [InlineData(ForgeErrorCodes.Partial, ExitCodes.PartialSuccess)]
    [InlineData(ForgeErrorCodes.Internal, ExitCodes.InternalError)]
    public void ExitCodes_MapFromErrorCodes(string code, int expected)
    {
        Assert.Equal(expected, ExitCodes.FromError(ForgeError.Create(code, "x")));
    }

    [Fact]
    public async Task Run_DetectWithoutGame_ReturnsArgumentError()
    {
        using var temp = new TempFolder();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["detect", "--workspace", temp.Root], stdout, stderr);

        Assert.Equal(ExitCodes.ArgumentError, exitCode);
        Assert.Contains("--game", stderr.ToString());
    }

    [Fact]
    public async Task Run_DetectNonexistentGame_ReturnsArgumentError()
    {
        using var temp = new TempFolder();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["detect", "--game", Path.Combine(temp.Root, "missing"), "--workspace", temp.Root],
            stdout,
            stderr);

        Assert.Equal(ExitCodes.ArgumentError, exitCode);
        Assert.Contains("不存在", stderr.ToString());
    }

    [Fact]
    public async Task Run_Detect_EmptyGame_ReturnsUnknownSuccess_NoFakeEngine()
    {
        using var temp = new TempFolder();
        var game = temp.Sub("Game");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["detect", "--game", game, "--workspace", temp.Root, "--json"],
            stdout,
            stderr);

        Assert.Equal(ExitCodes.Success, exitCode);
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("detect", json.RootElement.GetProperty("command").GetString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("unknown", data.GetProperty("family").GetString());
        Assert.False(data.GetProperty("known").GetBoolean());
        Assert.Equal(0d, data.GetProperty("confidence").GetDouble());
        Assert.Equal(game, data.GetProperty("game").GetString());
    }

    [Fact]
    public async Task Run_Doctor_WithoutExternalRegistry_UsesBuiltInAdapters()
    {
        using var temp = new TempFolder();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["doctor", "--workspace", temp.Root, "--json"],
            stdout,
            stderr);

        // 内置适配器（Ren'Py）随程序提供；外置 adapter.json 只是可选的扩展点。
        Assert.Equal(ExitCodes.Success, exitCode);
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        var checks = json.RootElement.GetProperty("data").GetProperty("checks");
        Assert.True(checks.GetArrayLength() >= 4);
        var adapters = checks.EnumerateArray()
            .First(check => check.GetProperty("name").GetString() == "adapters");
        Assert.True(adapters.GetProperty("passed").GetBoolean());
        Assert.Contains("内置适配器", adapters.GetProperty("detail").GetString());
        var bepInEx = checks.EnumerateArray()
            .First(check => check.GetProperty("name").GetString() == "bepinex-bundle");
        Assert.True(bepInEx.GetProperty("optional").GetBoolean());
        Assert.Contains("BepInEx", bepInEx.GetProperty("detail").GetString());
        var bepInEx6 = checks.EnumerateArray()
            .First(check => check.GetProperty("name").GetString() == "bepinex6-bundle");
        Assert.True(bepInEx6.GetProperty("optional").GetBoolean());
        Assert.Contains("BepInEx 6", bepInEx6.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Run_Doctor_WithAdapters_ReturnsSuccess()
    {
        using var temp = new TempFolder();
        var adaptersDir = Path.Combine(temp.Root, "adapters");
        Directory.CreateDirectory(adaptersDir);
        await File.WriteAllTextAsync(Path.Combine(adaptersDir, "adapter.json"), """
        {
          "schemaVersion": 1,
          "adapters": [
            { "name": "renpy", "engine": "renpy", "assembly": "Forge.Adapters.RenPy.dll",
              "type": "GalA11yForge.Adapters.RenPy.RenPyAdapter", "capabilities": ["detect"] }
          ]
        }
        """);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["doctor", "--workspace", temp.Root, "--json"],
            stdout,
            stderr);

        Assert.Equal(ExitCodes.Success, exitCode);
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("doctor", json.RootElement.GetProperty("command").GetString());
        Assert.Equal(Path.GetFullPath(temp.Root), json.RootElement.GetProperty("data").GetProperty("workspace").GetString());
    }

    [Fact]
    public async Task Run_HelpAndVersion_ReturnSuccess()
    {
        var helpOut = new StringWriter();
        var helpErr = new StringWriter();
        var versionOut = new StringWriter();
        var versionErr = new StringWriter();

        var helpCode = await CliApplication.RunAsync(["--help"], helpOut, helpErr);
        var versionCode = await CliApplication.RunAsync(["--version"], versionOut, versionErr);

        Assert.Equal(ExitCodes.Success, helpCode);
        Assert.Contains("退出码", helpOut.ToString());
        Assert.Equal(ExitCodes.Success, versionCode);
        Assert.Contains("GalA11y Demo", versionOut.ToString());
    }

    [Fact]
    public async Task Run_Analyze_UnknownEngine_ReturnsEngineUnsupported()
    {
        using var temp = new TempFolder();
        var game = temp.Sub("Game");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["analyze", "--game", game, "--workspace", temp.Root, "--json", "--dry-run", "--advanced"],
            stdout,
            stderr);

        // 空目录不是已知引擎：明确报 E_ENGINE_UNSUPPORTED（退出码 4），不假装成功。
        Assert.Equal(ExitCodes.EngineUnsupported, exitCode);
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(ForgeErrorCodes.EngineUnsupported, json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Run_Analyze_NonAsciiGameName_UsesStableHashSlug()
    {
        using var temp = new TempFolder();
        var game = temp.Sub("我与你共度的七日");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["analyze", "--game", game, "--workspace", temp.Root, "--json", "--dry-run"],
            stdout,
            stderr);

        // 中文目录名不应被判成“非法游戏标识”：工作区 slug 用完整路径的稳定哈希兜底，
        // 流程因此能走到引擎识别，对空目录明确返回 E_ENGINE_UNSUPPORTED（退出码 4）。
        Assert.Equal(ExitCodes.EngineUnsupported, exitCode);
        Assert.DoesNotContain("E_ARG_INVALID", stdout.ToString());

        var gamesRoot = Path.Combine(temp.Root, "games");
        var slugDirs = Directory.Exists(gamesRoot) ? Directory.GetDirectories(gamesRoot) : [];
        Assert.Single(slugDirs);
        Assert.StartsWith("game-", Path.GetFileName(slugDirs[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_SpeechRuntimeOption()
    {
        var result = CommandLineParser.Parse(
            ["install", "--game", "D:\\Games\\Sample", "--speech-runtime", "D:\\Tolk\\win-x64"]);

        Assert.Null(result.Error);
        Assert.Equal("D:\\Tolk\\win-x64", result.Options!.SpeechRuntimePath);
    }

    [Fact]
    public async Task Run_Build_Il2CppWithoutInterop_GivesActionableDependencyMissing()
    {
        using var temp = new TempFolder();
        var game = CreateFakeIl2CppGame(temp);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["build", "--game", game, "--workspace", temp.Root, "--json"],
            stdout,
            stderr);

        var output = stdout.ToString();

        // IL2CPP 没有 interop 时必须明确报 E_DEPENDENCY_MISSING 并指路，而不是“没有构建器”或假成功。
        Assert.Equal(ExitCodes.DependencyMissing, exitCode);
        Assert.DoesNotContain("E_NOT_IMPLEMENTED", output);
        using var json = JsonDocument.Parse(output);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(ForgeErrorCodes.DependencyMissing, json.RootElement.GetProperty("code").GetString());
        Assert.Contains("interop", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BepInEx", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_Install_Il2CppWithoutInterop_GivesActionableDependencyMissing()
    {
        using var temp = new TempFolder();
        var game = CreateFakeIl2CppGame(temp);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["install", "--game", game, "--workspace", temp.Root, "--json"],
            stdout,
            stderr);

        var output = stdout.ToString();

        // 以前 IL2CPP 的 install 直接 E_NOT_IMPLEMENTED；现在应先要求 interop，再谈安装。
        Assert.Equal(ExitCodes.DependencyMissing, exitCode);
        Assert.DoesNotContain("E_NOT_IMPLEMENTED", output);
        using var json = JsonDocument.Parse(output);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(ForgeErrorCodes.DependencyMissing, json.RootElement.GetProperty("code").GetString());
    }

    /// <summary>
    /// 造一个“能被识别成 Unity IL2CPP、但没有 interop/dump”的最小游戏目录：
    /// GameAssembly.dll + *_Data/il2cpp_data/Metadata/global-metadata.dat（合法 magic/版本）。
    /// </summary>
    private static string CreateFakeIl2CppGame(TempFolder temp)
    {
        var game = temp.Sub("FakeIl2Cpp");
        File.WriteAllBytes(Path.Combine(game, "GameAssembly.dll"), new byte[64]);
        File.WriteAllBytes(Path.Combine(game, "UnityPlayer.dll"), new byte[32]);

        var metadataDirectory = Path.Combine(game, "FakeIl2Cpp_Data", "il2cpp_data", "Metadata");
        Directory.CreateDirectory(metadataDirectory);
        var header = new byte[16];
        BitConverter.GetBytes(0xFAB11BAFu).CopyTo(header, 0);
        BitConverter.GetBytes(29).CopyTo(header, 4);
        File.WriteAllBytes(Path.Combine(metadataDirectory, "global-metadata.dat"), header);

        return game;
    }

    [Fact]
    public void Response_WithDetails_SerializesDetailsForScriptsAndGui()
    {
        var response = new CliResponse
        {
            Ok = false,
            Command = "verify",
            Code = ForgeErrorCodes.Partial,
            Message = "回滚完成但有部分失败。",
            Suggestion = "按详情逐项处理。",
            Details = new Dictionary<string, string>
            {
                ["plugin/a11y.dll"] = "删除失败：文件被占用",
                ["backup"] = "缺少安装前哈希",
            },
        };

        var json = JsonSerializer.Serialize(response, GalA11yForge.Core.Serialization.ForgeJson.Compact);

        Assert.Contains("\"details\":", json);
        Assert.Contains("plugin/a11y.dll", json);
        Assert.Contains("backup", json);
    }
    
    [Fact]
    public async Task ParseErrorWithJsonFlag_StillReturnsJsonOnStdout()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliApplication.RunAsync(new[] { "frobnicate", "--json" }, stdout, stderr);

        Assert.Equal(2, exit);
        var text = stdout.ToString().Trim();
        Assert.StartsWith("{", text, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(text);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("E_ARG_INVALID", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void Compose_ReportsHookCountAndWatcherFallback()
    {
        var data = new Dictionary<string, object?>();

        var message = InstallNotice.Compose(isIl2Cpp: false, speechRuntimePath: null, data: data, hookCount: 6, hasWatcherFallback: true);

        Assert.Contains("6 个钩子", message, StringComparison.Ordinal);
        Assert.Contains("文本兜底轮询", message, StringComparison.Ordinal);
        Assert.Equal(6, data["hookCount"]);
        Assert.Equal(true, data["watcherFallback"]);
    }

    [Fact]
    public void Compose_AddsSpeechLogHintForUnityAndRenpy()
    {
        var unityData = new Dictionary<string, object?>();
        var unity = InstallNotice.Compose(
            false, null, unityData, hookCount: 2, hasWatcherFallback: false,
            gameDirectory: @"C:\Games\Demo", engineFamily: "unity_mono");

        Assert.Contains("LogOutput.log", unity);
        Assert.Contains("forge verify", unity);
        Assert.EndsWith("BepInEx\\LogOutput.log", unityData["speechLogHint"]!.ToString());

        var renpyData = new Dictionary<string, object?>();
        var renpy = InstallNotice.Compose(
            false, null, renpyData, gameDirectory: @"C:\Games\Demo", engineFamily: "renpy");

        Assert.Contains("a11y_speech.log", renpy);
        Assert.EndsWith("a11y_speech.log", renpyData["speechLogHint"]!.ToString());
    }

    [Fact]
    public void Japanese_voice_warning_only_fires_for_japanese_text_without_voice()
    {
        Assert.True(JapaneseVoiceNotice.ShouldWarn(false, ["language=ja（source=idstring）"]));
        Assert.False(JapaneseVoiceNotice.ShouldWarn(true, ["language=ja（source=idstring）"]));
        Assert.False(JapaneseVoiceNotice.ShouldWarn(false, ["language=sc（source=charset）"]));
        Assert.False(JapaneseVoiceNotice.ShouldWarn(false, Array.Empty<string?>()));
    }
}
