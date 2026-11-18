using System.Security.Cryptography;
using System.Text.Json;
using GalA11yForge.Adapters.Renpy.Internal;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Adapters.Renpy.Tests;

public sealed class GenerationVerifyTests
{
    private readonly RenpyAdapter _adapter = new();

    [Fact]
    public async Task Generate_WritesElevenFilesWithConsistentManifestAndDeployPlan()
    {
        using var fixture = new RenpyFixture("6.99");
        var context = fixture.Context();
        var plan = await PlanAsync(context);
        var before = Snapshot(fixture.GameDirectory);

        var result = await _adapter.GenerateAsync(context, plan, fixture.GeneratedDirectory, CancellationToken.None);

        Assert.True(result.Ok, result.Error?.Message);
        var generation = result.Value!;
        Assert.Equal(Path.GetFullPath(fixture.GeneratedDirectory), generation.ProjectDirectory);
        Assert.Equal(11, generation.Artifacts.Count);
        Assert.Equal(11, Directory.GetFiles(generation.ProjectDirectory, "*", SearchOption.AllDirectories).Length);

        var manifestPath = Path.Combine(generation.ProjectDirectory, "a11y_manifest.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = manifest.RootElement;
        Assert.Equal("gala11y.renpy.overlay/1", root.GetProperty("schema").GetString());
        Assert.Equal("gala11y-forge-renpy", root.GetProperty("generator").GetString());
        Assert.Equal("0.2.0.1", root.GetProperty("patch_version").GetString());
        Assert.Equal("renpy", root.GetProperty("game").GetProperty("engine").GetString());
        Assert.Equal(9, root.GetProperty("files").GetArrayLength());
        foreach (var file in root.GetProperty("files").EnumerateArray())
        {
            var relative = file.GetProperty("path").GetString()!;
            var path = Path.Combine(generation.ProjectDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            var bytes = File.ReadAllBytes(path);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.Equal(hash, file.GetProperty("sha256").GetString());
            Assert.Equal(bytes.LongLength, file.GetProperty("bytes").GetInt64());
        }

        var deployPath = Path.Combine(generation.ProjectDirectory, "deploy_plan.json");
        using var deploy = JsonDocument.Parse(File.ReadAllText(deployPath));
        Assert.Equal("gala11y.renpy.deploy_plan/1", deploy.RootElement.GetProperty("schema").GetString());
        Assert.True(deploy.RootElement.GetProperty("conflict_overlay").GetBoolean() == false);
        Assert.Equal(10, deploy.RootElement.GetProperty("steps").GetArrayLength());
        Assert.Contains("只读", deploy.RootElement.GetProperty("guard").GetString(), StringComparison.Ordinal);

        Assert.True(HasUtf8Bom(Path.Combine(generation.ProjectDirectory, "uninstall.ps1")));
        Assert.False(HasUtf8Bom(Path.Combine(generation.ProjectDirectory, "game", "a11y_platform", "00_contract.rpy")));
        Assert.Equal("0", generation.Metadata["skippedExisting"]);
        Assert.Equal(before, Snapshot(fixture.GameDirectory));
    }

    [Fact]
    public async Task Generate_DefaultOutputDirectory_ThenVerifyPasses()
    {
        using var fixture = new RenpyFixture("8.5");
        var context = fixture.Context();
        var plan = await PlanAsync(context);

        var generated = await _adapter.GenerateAsync(context, plan, string.Empty, CancellationToken.None);

        Assert.True(generated.Ok, generated.Error?.Message);
        Assert.Equal(Path.GetFullPath(fixture.GeneratedDirectory), generated.Value!.ProjectDirectory);

        var verified = await _adapter.VerifyAsync(context, null, CancellationToken.None);

        Assert.True(verified.Ok, verified.Error?.Message);
        var report = verified.Value!;
        Assert.True(report.Passed, string.Join("; ", report.Checks.Where(c => !c.Passed).Select(c => c.Name + ":" + c.Detail)));
        Assert.True(report.Checks.Count >= 25, $"实际 {report.Checks.Count} 项");
        Assert.Contains(report.Unverified, item => item.Contains("未启动游戏", StringComparison.Ordinal));
        Assert.Contains(report.ManualTodo, item => item.Blocking && item.Title.Contains("真机", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Generate_ProfileOptions_AreRenderedAndStillVerify()
    {
        using var fixture = new RenpyFixture("8.5");
        var profile = new ProfileDocument();
        profile.Options["renpy.patchVersion"] = JsonSerializer.SerializeToElement("0.3.1.9");
        profile.Options["renpy.tts"] = JsonSerializer.SerializeToElement("sapi");
        profile.Options["renpy.sapiRate"] = JsonSerializer.SerializeToElement(2);
        var context = fixture.Context(profile);
        var plan = await PlanAsync(context);

        var generated = await _adapter.GenerateAsync(context, plan, fixture.GeneratedDirectory, CancellationToken.None);

        Assert.True(generated.Ok, generated.Error?.Message);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.GeneratedDirectory, "a11y_manifest.json")));
        Assert.Equal("0.3.1.9", manifest.RootElement.GetProperty("patch_version").GetString());

        var speech = File.ReadAllText(Path.Combine(fixture.GeneratedDirectory, "game", "a11y_platform", "01_speech.rpy"));
        Assert.Contains("_A11Y_TTS_PREF = \"sapi\"", speech, StringComparison.Ordinal);
        Assert.Contains("_A11Y_SAPI_RATE = 2", speech, StringComparison.Ordinal);
        // 0.2.44：SAPI 走 wscript 的分支用到 os.path/os.close，但不能裸 import os：
        // Ren'Py 的 renpy/common/00keymap.rpy 在 init -1600 会 `import os ... del os`，
        // 运行期再引用 os 就是 NameError（自造最小样本真机每句都报，语音根本没发出去）。
        // 所以契约文件用别名 _a11y_os，其它文件统一引用它。
        var contract = File.ReadAllText(Path.Combine(fixture.GeneratedDirectory, "game", "a11y_platform", "00_contract.rpy"));
        Assert.Contains("import os as _a11y_os", contract, StringComparison.Ordinal);
        Assert.Contains("_a11y_os.close(fd)", speech, StringComparison.Ordinal);
        var bareOs = System.Text.RegularExpressions.Regex.IsMatch(speech, @"(?<![_a-zA-Z0-9])os\.");
        Assert.False(bareOs, "生成物不该直接引用裸 os（引擎 00keymap 会 del os）");

        var verified = await _adapter.VerifyDirectoryAsync(context, fixture.GeneratedDirectory, CancellationToken.None);
        Assert.True(verified.Ok, verified.Error?.Message);
        Assert.True(verified.Value!.Passed);
    }

    [Fact]
    public async Task Generate_InvalidPatchVersion_ReturnsArgumentInvalid()
    {
        using var fixture = new RenpyFixture("8.5");
        var profile = new ProfileDocument();
        profile.Options["renpy.patchVersion"] = JsonSerializer.SerializeToElement("1.2");
        var context = fixture.Context(profile);
        var plan = await PlanAsync(context);

        var result = await _adapter.GenerateAsync(context, plan, fixture.GeneratedDirectory, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.ArgumentInvalid, result.Error!.Code);
    }

    [Fact]
    public async Task Generate_OutputInsideGameDirectory_ReturnsGameWriteDenied()
    {
        using var fixture = new RenpyFixture("8.5");
        var context = fixture.Context();
        var plan = await PlanAsync(context);

        var result = await _adapter.GenerateAsync(
            context, plan, Path.Combine(fixture.GameDirectory, "patch-output"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.GameWriteDenied, result.Error!.Code);
        Assert.False(Directory.Exists(Path.Combine(fixture.GameDirectory, "patch-output")));
    }

    [Fact]
    public async Task Generate_OutputOutsideWorkspace_ReturnsPathUnsafe()
    {
        using var fixture = new RenpyFixture("8.5");
        var context = fixture.Context();
        var plan = await PlanAsync(context);

        var result = await _adapter.GenerateAsync(
            context, plan, Path.Combine(fixture.Root, "outside-workspace"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.PathUnsafe, result.Error!.Code);
    }

    [Fact]
    public async Task Generated_SayBridge_UsesSynchronousSpeak_SoAudioSurvivesWscriptExit()
    {
        using var fixture = new RenpyFixture("6.99");
        var context = fixture.Context();
        var plan = await PlanAsync(context);

        var result = await _adapter.GenerateAsync(context, plan, fixture.GeneratedDirectory, CancellationToken.None);
        Assert.True(result.Ok, result.Error?.Message);

        var vbs = File.ReadAllText(Path.Combine(fixture.GeneratedDirectory, "a11y_say.vbs"));
        Assert.Contains("sapi.Speak text", vbs, StringComparison.Ordinal);
        Assert.DoesNotContain("sapi.Speak text, 3", vbs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_ExistingDifferentFile_IsNotOverwritten()
    {
        using var fixture = new RenpyFixture("8.5");
        var context = fixture.Context();
        var plan = await PlanAsync(context);
        var first = await _adapter.GenerateAsync(context, plan, fixture.GeneratedDirectory, CancellationToken.None);
        Assert.True(first.Ok, first.Error?.Message);

        var vbs = Path.Combine(fixture.GeneratedDirectory, "a11y_say.vbs");
        File.AppendAllText(vbs, "' local edit\n");

        var second = await _adapter.GenerateAsync(context, plan, fixture.GeneratedDirectory, CancellationToken.None);

        Assert.False(second.Ok);
        Assert.Equal(ForgeErrorCodes.ArtifactInvalid, second.Error!.Code);
        Assert.Contains("local edit", File.ReadAllText(vbs), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_SecondRunWithIdenticalFiles_SkipsContentAndSucceeds()
    {
        using var fixture = new RenpyFixture("8.5");
        var context = fixture.Context();
        var plan = await PlanAsync(context);

        var first = await _adapter.GenerateAsync(context, plan, fixture.GeneratedDirectory, CancellationToken.None);
        var second = await _adapter.GenerateAsync(context, plan, fixture.GeneratedDirectory, CancellationToken.None);

        Assert.True(first.Ok, first.Error?.Message);
        Assert.True(second.Ok, second.Error?.Message);
        // README 含生成时间，第二次渲染内容会不同；因为它与旧清单哈希一致，允许覆盖，不会被当成用户改动。
        Assert.True(second.Value!.Metadata.ContainsKey("overwrittenExisting"));
        Assert.Equal(11, Directory.GetFiles(fixture.GeneratedDirectory, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task Generate_UnsupportedRoute_ReturnsEngineUnsupported()
    {
        using var fixture = new RenpyFixture("8.5");
        var context = fixture.Context();
        var plan = await PlanAsync(context);
        plan.Route = PlanRoute.Unsupported;

        var result = await _adapter.GenerateAsync(context, plan, fixture.GeneratedDirectory, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.EngineUnsupported, result.Error!.Code);
    }

    [Fact]
    public void TemplateRenderer_MissingEmbeddedTemplate_ThrowsTypedException()
    {
        var exception = Assert.Throws<RenpyTemplateMissingException>(
            () => RenpyTemplateRenderer.Load("Templates/platform/not-there.rpy"));

        Assert.Contains("not-there.rpy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_MissingDirectory_ReturnsArtifactMissing()
    {
        using var fixture = new RenpyFixture("8.5");
        var context = fixture.Context();

        var result = await _adapter.VerifyDirectoryAsync(context, fixture.GeneratedDirectory, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.ArtifactMissing, result.Error!.Code);
    }

    [Fact]
    public async Task Verify_TamperedFile_FailsManifestHashCheck()
    {
        using var fixture = new RenpyFixture("8.5");
        var context = fixture.Context();
        await GenerateAsync(fixture);

        var dialogue = Path.Combine(fixture.GeneratedDirectory, "game", "a11y_platform", "02_dialogue.rpy");
        File.AppendAllText(dialogue, "# tampered\n");

        var result = await _adapter.VerifyDirectoryAsync(context, fixture.GeneratedDirectory, CancellationToken.None);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.False(result.Value!.Passed);
        Assert.Contains(result.Value.Checks, c => c.Name.StartsWith("C3", StringComparison.Ordinal) && !c.Passed);
    }

    [Fact]
    public async Task Verify_MissingArtifact_FailsCompletenessChecks()
    {
        using var fixture = new RenpyFixture("8.5");
        var context = fixture.Context();
        await GenerateAsync(fixture);

        File.Delete(Path.Combine(fixture.GeneratedDirectory, "game", "a11y_platform", "03_menu.rpy"));

        var result = await _adapter.VerifyDirectoryAsync(context, fixture.GeneratedDirectory, CancellationToken.None);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.False(result.Value!.Passed);
        Assert.Contains(result.Value.Checks, c => c.Name.StartsWith("A1", StringComparison.Ordinal) && !c.Passed);
        Assert.Contains(result.Value.Checks, c => c.Name.StartsWith("D1", StringComparison.Ordinal) && !c.Passed);
    }

    [Fact]
    public async Task Verify_RemovedKeyHook_FailsB6()
    {
        using var fixture = new RenpyFixture("8.5");
        var context = fixture.Context();
        await GenerateAsync(fixture);

        var keysPath = Path.Combine(fixture.GeneratedDirectory, "game", "a11y_platform", "05_keys.rpy");
        var text = File.ReadAllText(keysPath).Replace("config.underlay.append", "config.underlay.APPEND", StringComparison.Ordinal);
        File.WriteAllText(keysPath, text);

        var result = await _adapter.VerifyDirectoryAsync(context, fixture.GeneratedDirectory, CancellationToken.None);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.False(result.Value!.Passed);
        Assert.Contains(result.Value.Checks, c => c.Name.StartsWith("B6", StringComparison.Ordinal) && !c.Passed);
    }

    [Fact]
    public async Task Verify_ManifestForAnotherGame_Fails()
    {
        using var first = new RenpyFixture("8.5");
        using var second = new RenpyFixture("8.5");
        await GenerateAsync(first);

        var result = await _adapter.VerifyDirectoryAsync(second.Context(), first.GeneratedDirectory, CancellationToken.None);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.False(result.Value!.Passed);
        Assert.Contains(result.Value.Checks, c => c.Name.StartsWith("C3", StringComparison.Ordinal)
                                                 && c.Detail?.Contains("其他游戏", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Generated_DialogueGuard_SkipsEnginePredictionCalls()
    {
        using var fixture = new RenpyFixture("6.99");
        var context = fixture.Context();
        var plan = await PlanAsync(context);

        var result = await _adapter.GenerateAsync(context, plan, fixture.GeneratedDirectory, CancellationToken.None);

        Assert.True(result.Ok, result.Error?.Message);
        var dialogue = File.ReadAllText(Path.Combine(
            fixture.GeneratedDirectory, "game", "a11y_platform", "02_dialogue.rpy"));
        Assert.Contains("def _a11y_predicting():", dialogue, StringComparison.Ordinal);
        Assert.Contains("getattr(renpy.display, \"predict\", None)", dialogue, StringComparison.Ordinal);
        Assert.Contains("if _a11y_predicting():", dialogue, StringComparison.Ordinal);

        // 预测闸门必须出现在“记待播报”之前，否则 Say.predict 仍会提前念台词。
        var guard = dialogue.IndexOf("if _a11y_predicting():", StringComparison.Ordinal);
        var note = dialogue.IndexOf("st.pending_seq += 1", StringComparison.Ordinal);
        Assert.True(guard >= 0 && note > guard, $"guard={guard} note={note}");
    }

    [Fact]
    public async Task Generated_SpecialScreens_AnnounceInputAndConfirm()
    {
        using var fixture = new RenpyFixture("6.99");
        var context = fixture.Context();
        var plan = await PlanAsync(context);

        var result = await _adapter.GenerateAsync(context, plan, fixture.GeneratedDirectory, CancellationToken.None);

        Assert.True(result.Ok, result.Error?.Message);
        var platform = Path.Combine(fixture.GeneratedDirectory, "game", "a11y_platform");
        var menu = File.ReadAllText(Path.Combine(platform, "03_menu.rpy"));
        var focus = File.ReadAllText(Path.Combine(platform, "04_focus.rpy"));
        var contract = File.ReadAllText(Path.Combine(platform, "00_contract.rpy"));

        // 输入框 / 确认框 / 退出确认的关键词与状态字段
        Assert.Contains("_A11Y_SPECIAL_SCREENS", menu, StringComparison.Ordinal);
        Assert.Contains("(u\"input\", u\"输入框\")", menu, StringComparison.Ordinal);
        Assert.Contains("(u\"name_input\", u\"输入框\")", menu, StringComparison.Ordinal);
        Assert.Contains("(u\"confirm\", u\"确认\")", menu, StringComparison.Ordinal);
        Assert.Contains("def _a11y_special_tick():", menu, StringComparison.Ordinal);
        Assert.Contains("def _a11y_input_value(", menu, StringComparison.Ordinal);
        Assert.Contains("_a11y_clean_text", menu, StringComparison.Ordinal);
        Assert.Contains("_a11y_add_unique", menu, StringComparison.Ordinal);
        Assert.Contains("texts = [_a11y_translate_text(t) for t in texts]", menu, StringComparison.Ordinal);
        Assert.Contains("self.special_seen = {}", contract, StringComparison.Ordinal);
        Assert.Contains("self.input_value = u\"\"", contract, StringComparison.Ordinal);

        // 0.2.24：游戏自带旧 a11y_game（Framework 契约）时，它会调
        // A11yHost.validate()/.rpy.start()/.keys.install()。契约层必须有兼容桥，
        // 否则游戏启动即 AttributeError 崩溃（真机事故：永恒与星辰与日常）。
        Assert.Contains("_A11yLegacyBridge", contract, StringComparison.Ordinal);
        Assert.Contains("def validate(self):", contract, StringComparison.Ordinal);
        Assert.Contains("def log_exc(self", contract, StringComparison.Ordinal);
        Assert.Contains("CfgAutoEnable", contract, StringComparison.Ordinal);
        Assert.Contains("self.rpy = _A11yLegacyBridge", contract, StringComparison.Ordinal);
        Assert.Contains("self.keys = _A11yLegacyBridge", contract, StringComparison.Ordinal);

        // 0.2.26：raw=True 是翻译前 source text（真机：中文确认框念英文）；
        // 必须用 raw=False 取翻译后的显示文本。
        Assert.Contains("_tts_all(raw=False)", contract, StringComparison.Ordinal);

        // 0.2.27：确认框常用 imagebutton（按钮没有文字），仍要告诉用户
        // “有几个按钮、怎么操作”；焦点落到无文字按钮时也要有播报。
        Assert.Contains("def _a11y_screen_buttons(screen_name):", menu, StringComparison.Ordinal);
        Assert.Contains("有 %d 个按钮。按 Tab 切换，回车确认", menu, StringComparison.Ordinal);
        Assert.Contains("（没有可读文字，按 Tab 换一个，回车执行）", focus, StringComparison.Ordinal);

        // 必须真的挂进周期心跳，否则写了也不会跑
        Assert.Contains("_A11Y_SPECIAL_INTERVAL", focus, StringComparison.Ordinal);
        Assert.Contains("_a11y_special_tick()", focus, StringComparison.Ordinal);
        var call = focus.IndexOf("_a11y_special_tick()", StringComparison.Ordinal);
        var periodic = focus.IndexOf("def _a11y_periodic():", StringComparison.Ordinal);
        Assert.True(periodic >= 0 && call > periodic, $"periodic={periodic} call={call}");
    }

    [Fact]
    public async Task Generated_ChoiceMenu_ReadsPromptAboveOptions()
    {
        // 0.2.16：只剩选项、不读选项上方题目时，盲人用户不知道“在选什么”。
        using var fixture = new RenpyFixture("6.99");
        var context = fixture.Context();
        var plan = await PlanAsync(context);

        var result = await _adapter.GenerateAsync(context, plan, fixture.GeneratedDirectory, CancellationToken.None);

        Assert.True(result.Ok, result.Error?.Message);
        var menu = File.ReadAllText(Path.Combine(fixture.GeneratedDirectory, "game", "a11y_platform", "03_menu.rpy"));
        Assert.Contains("def _a11y_choice_prompt():", menu, StringComparison.Ordinal);
        Assert.Contains("option_keys", menu, StringComparison.Ordinal);
        Assert.Contains("提示：%s。选项 %d 个", menu, StringComparison.Ordinal);
        // 0.2.44：题目文字也是 Text，先前列选项时把题目当成第 1 项（编号错位）。
        // 现在选项只认按钮，题目单独走 prompt。
        Assert.Contains("def _a11y_choice_options():", menu, StringComparison.Ordinal);
        Assert.Contains("_a11y_duck_role(node) != u\"按钮\"", menu, StringComparison.Ordinal);
        Assert.Contains("for t in _a11y_choice_options()", menu, StringComparison.Ordinal);
        // 题目会被渲染成 textbutton(action=None)：这种"假按钮"不能算选项。
        Assert.Contains("def _a11y_button_has_no_action(node):", menu, StringComparison.Ordinal);
        Assert.Contains("if _a11y_button_has_no_action(d):", menu, StringComparison.Ordinal);
        // 题目进签名：题目变化（同一屏更换文字）也要重新播报。
        Assert.Contains("sig = prompt + u\"||\" + u\"|\".join(texts)", menu, StringComparison.Ordinal);

        // 0.2.26：屏幕里读到的是 source text，界面显示的是翻译后文本；
        // 播报前必须按当前语言翻译一次（真机：中文确认框播报英文原文）。
        Assert.Contains("def _a11y_translate_text(value):", menu, StringComparison.Ordinal);
        Assert.Contains("renpy.translate_string", menu, StringComparison.Ordinal);
        Assert.Contains("_a11y_translate_text(t)", menu, StringComparison.Ordinal);
    }

    private async Task GenerateAsync(RenpyFixture fixture)
    {
        var context = fixture.Context();
        var plan = await PlanAsync(context);
        var result = await _adapter.GenerateAsync(context, plan, fixture.GeneratedDirectory, CancellationToken.None);
        Assert.True(result.Ok, result.Error?.Message);
    }

    private async Task<PatchPlan> PlanAsync(GameContext context)
    {
        var detection = await _adapter.DetectAsync(context);
        Assert.True(detection.Ok, detection.Error?.Message);
        var analysis = await _adapter.AnalyzeAsync(context, detection.Value!);
        Assert.True(analysis.Ok, analysis.Error?.Message);
        var plan = await _adapter.PlanAsync(context, analysis.Value!);
        Assert.True(plan.Ok, plan.Error?.Message);
        return plan.Value!;
    }

    private static bool HasUtf8Bom(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }

    private static string[] Snapshot(string root) => Directory
        .EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => $"{Path.GetRelativePath(root, path)}|{new FileInfo(path).Length}")
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();
}
