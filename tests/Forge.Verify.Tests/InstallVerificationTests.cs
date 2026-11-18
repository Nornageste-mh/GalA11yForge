using GalA11yForge.Core.Models;
using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify.Tests;

public sealed class InstallVerificationTests
{
    [Fact]
    public async Task Standard_install_passes_offline_checks()
    {
        using var fixture = new VerifyFixture();

        var report = await VerifyAsync(fixture);

        Assert.Equal(VerifyOverall.Pass, report.Overall);
        Assert.Equal(17, report.Checks.Count);
        Assert.All(report.Checks, check => Assert.Equal(VerifyCheckStatus.Pass, check.Status));
        Assert.False(report.Cleanup.Required);
        Assert.False(report.Cleanup.Uninstalled);
        Assert.Null(report.Cleanup.HashMatch);
        Assert.Contains(report.Unverified, item => item.Contains("未启动游戏", StringComparison.Ordinal));
        Assert.Contains(report.Unverified, item => item.Contains("未执行卸载回滚", StringComparison.Ordinal));
        ReportAssert.HasStatus(report, "m-07", VerifyCheckStatus.Pass);
    }

    [Fact]
    public async Task Hash_mismatch_fails_install_hash_check()
    {
        using var fixture = new VerifyFixture();
        fixture.WriteGame(VerifyFixture.WinHttpRelativePath, "tampered-loader");

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "m-05", VerifyCheckStatus.Fail);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Zero_byte_installed_file_fails_size_check()
    {
        using var fixture = new VerifyFixture();
        fixture.WriteGame(VerifyFixture.PluginRelativePath, string.Empty);
        var plugin = fixture.Manifest.Files.Single(item => item.RelativePath == VerifyFixture.PluginRelativePath);
        plugin.Sha256After = Hashes.Sha256(string.Empty);
        fixture.SaveManifest();

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "m-05", VerifyCheckStatus.Pass);
        ReportAssert.HasStatus(report, "m-06", VerifyCheckStatus.Fail);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Missing_installed_file_fails_presence_and_plugin_checks()
    {
        using var fixture = new VerifyFixture();
        File.Delete(fixture.PathOfGame(VerifyFixture.PluginRelativePath));

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "m-04", VerifyCheckStatus.Fail);
        ReportAssert.HasStatus(report, "m-05", VerifyCheckStatus.Fail);
        ReportAssert.HasStatus(report, "b-03", VerifyCheckStatus.Fail);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Missing_manifest_is_fail_not_exception()
    {
        using var fixture = new VerifyFixture();
        File.Delete(fixture.ManifestPath);

        var report = await VerifyAsync(fixture);

        Assert.Equal(VerifyOverall.Fail, report.Overall);
        ReportAssert.HasStatus(report, "m-01", VerifyCheckStatus.Fail);
        Assert.Single(report.Checks);
        Assert.Null(report.ManifestPath);
    }

    [Fact]
    public async Task Invalid_manifest_json_is_fail_not_crash()
    {
        using var fixture = new VerifyFixture();
        File.WriteAllText(fixture.ManifestPath, "{ this is not json");

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "m-01", VerifyCheckStatus.Fail);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Unsupported_manifest_schema_fails_structure_and_skips_dependants()
    {
        using var fixture = new VerifyFixture();
        fixture.Manifest.SchemaVersion = 2;
        fixture.SaveManifest();

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "m-01", VerifyCheckStatus.Pass);
        ReportAssert.HasStatus(report, "m-02", VerifyCheckStatus.Fail);
        ReportAssert.HasStatus(report, "m-04", VerifyCheckStatus.Skip);
        Assert.DoesNotContain(report.Checks, check => check.Id.StartsWith("b-", StringComparison.Ordinal));
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Missing_required_manifest_field_fails_structure()
    {
        using var fixture = new VerifyFixture();
        fixture.Manifest.BackupRoot = string.Empty;
        fixture.SaveManifest();

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "m-02", VerifyCheckStatus.Fail);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Manifest_game_path_mismatch_fails_structure()
    {
        using var fixture = new VerifyFixture();
        fixture.Manifest.GamePath = Path.Combine(fixture.Temp.Root, "some-other-game");
        fixture.SaveManifest();

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "m-02", VerifyCheckStatus.Fail);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Path_escape_in_manifest_fails_path_check()
    {
        using var fixture = new VerifyFixture();
        fixture.Manifest.Files.Add(new DeployFileEntry
        {
            RelativePath = "../outside.dll",
            Action = DeployFileAction.Add,
            Sha256After = Hashes.Sha256("outside"),
        });
        fixture.SaveManifest();

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "m-03", VerifyCheckStatus.Fail);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Duplicate_manifest_entry_fails_path_check()
    {
        using var fixture = new VerifyFixture();
        fixture.Manifest.Files.Add(new DeployFileEntry
        {
            RelativePath = VerifyFixture.PluginRelativePath,
            Action = DeployFileAction.Add,
            Sha256After = Hashes.Sha256(VerifyFixture.PluginContent),
        });
        fixture.SaveManifest();

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "m-03", VerifyCheckStatus.Fail);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Missing_backup_file_fails_backup_checks()
    {
        using var fixture = new VerifyFixture();
        File.Delete(Path.Combine(fixture.BackupRoot, VerifyFixture.DoorstopRelativePath));

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "k-01", VerifyCheckStatus.Fail);
        ReportAssert.HasStatus(report, "k-02", VerifyCheckStatus.Fail);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Backup_content_mismatch_fails_backup_hash()
    {
        using var fixture = new VerifyFixture();
        File.WriteAllText(
            Path.Combine(fixture.BackupRoot, VerifyFixture.DoorstopRelativePath),
            "[General]\nenabled=true\n");

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "k-01", VerifyCheckStatus.Pass);
        ReportAssert.HasStatus(report, "k-02", VerifyCheckStatus.Fail);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Backup_outside_workspace_backup_fails_location_check()
    {
        using var fixture = new VerifyFixture();
        var outsideBackup = fixture.PathOfGame("doorstop-backup.ini");
        File.WriteAllText(outsideBackup, VerifyFixture.DoorstopOriginal);

        var entry = fixture.Manifest.Files.Single(item => item.RelativePath == VerifyFixture.DoorstopRelativePath);
        entry.BackupPath = outsideBackup;
        fixture.SaveManifest();

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "k-01", VerifyCheckStatus.Fail);
        ReportAssert.HasStatus(report, "k-03", VerifyCheckStatus.Fail);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Baseline_tamper_of_original_file_fails()
    {
        using var fixture = new VerifyFixture();
        fixture.WriteGame("game.exe", "changed-exe");

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "m-07", VerifyCheckStatus.Fail);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Missing_default_baseline_skips_but_does_not_pass()
    {
        using var fixture = new VerifyFixture();
        File.Delete(fixture.BaselinePath);

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "m-07", VerifyCheckStatus.Skip);
        Assert.Equal(VerifyOverall.PassWithSkips, report.Overall);
        Assert.Contains(report.Unverified, item => item.Contains("基线", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_explicit_baseline_fails()
    {
        using var fixture = new VerifyFixture();
        File.Delete(fixture.BaselinePath);
        var options = fixture.Options();
        options.BaselinePath = fixture.BaselinePath;

        var report = await VerifyAsync(fixture, options);

        ReportAssert.HasStatus(report, "m-07", VerifyCheckStatus.Fail);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Invalid_baseline_json_fails_without_crash()
    {
        using var fixture = new VerifyFixture();
        File.WriteAllText(fixture.BaselinePath, "{ not-json");

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "m-07", VerifyCheckStatus.Fail);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Baseline_for_another_game_fails()
    {
        using var fixture = new VerifyFixture();
        fixture.Baseline.GamePath = Path.Combine(fixture.Temp.Root, "other-game");
        fixture.SaveBaseline();

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "m-07", VerifyCheckStatus.Fail);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Missing_bepinex_log_fails_log_checks_not_pass()
    {
        using var fixture = new VerifyFixture();
        File.Delete(fixture.LogPath);

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "b-05", VerifyCheckStatus.Fail);
        ReportAssert.HasStatus(report, "b-06", VerifyCheckStatus.Fail);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
        Assert.Contains(report.Unverified, item => item.Contains("日志", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Empty_bepinex_log_fails_log_checks_not_pass()
    {
        using var fixture = new VerifyFixture();
        File.WriteAllText(fixture.LogPath, string.Empty);

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "b-05", VerifyCheckStatus.Fail);
        ReportAssert.HasStatus(report, "b-06", VerifyCheckStatus.Fail);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Preloader_crash_log_is_reported_when_log_missing()
    {
        using var fixture = new VerifyFixture();
        fixture.WriteGame("preloader_20261114_200051_097.log", "MissingMethodException: Module.GetPEKind");
        File.Delete(fixture.LogPath);

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "b-06", VerifyCheckStatus.Fail);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
        var check = report.Checks.Single(item => item.Id == "b-06");
        Assert.Contains("预加载器日志", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Log_without_plugin_load_line_fails()
    {
        using var fixture = new VerifyFixture();
        File.WriteAllText(
            fixture.LogPath,
            "[Message:   BepInEx] BepInEx 5.4.23.5\n[Message:   BepInEx] Chainloader startup complete\n");

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "b-05", VerifyCheckStatus.Pass);
        ReportAssert.HasStatus(report, "b-06", VerifyCheckStatus.Fail);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Log_with_display_name_passes_when_dll_name_differs()
    {
        using var fixture = new VerifyFixture();
        const string pluginPath = "BepInEx/plugins/Build.A11y.dll";
        const string pluginContent = "generated-plugin-binary";
        fixture.WriteGame(pluginPath, pluginContent);
        fixture.Manifest.Files!.Add(new DeployFileEntry
        {
            RelativePath = pluginPath,
            Action = DeployFileAction.Add,
            Sha256After = Hashes.Sha256(pluginContent),
        });
        fixture.SaveManifest();
        File.WriteAllText(
            fixture.LogPath,
            "[Message:   BepInEx] BepInEx 5.4.23.5\n" +
            "[Info   :   BepInEx] Loading [Build 无障碍补丁 0.1.0.0]\n" +
            "[Message:   BepInEx] Chainloader startup complete\n");

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "b-06", VerifyCheckStatus.Pass);
    }

    [Fact]
    public async Task No_bepinex_layout_yields_skip_check()
    {
        using var fixture = new VerifyFixture(standardInstall: false);
        fixture.WriteGame("game.exe", "minimal-game");
        fixture.SetManifest(new DeployManifest
        {
            SchemaVersion = 1,
            DeploymentId = "dep-min",
            GamePath = fixture.GameDir,
            BackupRoot = Path.Combine(fixture.Workspace, "backup", "dep-min"),
            CreatedAt = "2026-11-14T00:00:00+08:00",
            Files = new List<DeployFileEntry>
            {
                new()
                {
                    RelativePath = "game.exe",
                    Action = DeployFileAction.Add,
                    Sha256After = Hashes.Sha256("minimal-game"),
                },
            },
        });
        fixture.SaveManifest();

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "b-01", VerifyCheckStatus.Skip);
        Assert.DoesNotContain(report.Checks, check => check.Id == "b-02");
        Assert.Equal(VerifyOverall.PassWithSkips, report.Overall);
    }

    [Fact]
    public async Task Environment_without_available_backend_skips()
    {
        using var fixture = new VerifyFixture();
        var options = fixture.Options(
            probe: true,
            environmentProbe: new FakeEnvironmentProbe(FakeEnvironmentProbe.None()));

        var report = await VerifyAsync(fixture, options);

        ReportAssert.HasStatus(report, "e-01", VerifyCheckStatus.Skip);
        Assert.Equal(VerifyOverall.PassWithSkips, report.Overall);
        Assert.Contains(report.Unverified, item => item.Contains("未发现可用语音后端", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Environment_with_available_backend_passes_but_still_unverified_sound()
    {
        using var fixture = new VerifyFixture();
        var options = fixture.Options(
            probe: true,
            environmentProbe: new FakeEnvironmentProbe(FakeEnvironmentProbe.AllAvailable()));

        var report = await VerifyAsync(fixture, options);

        ReportAssert.HasStatus(report, "e-01", VerifyCheckStatus.Pass);
        Assert.Equal(VerifyOverall.Pass, report.Overall);
        Assert.Contains(report.Unverified, item => item.Contains("真的出声", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Environment_probe_exception_skips_instead_of_crashing()
    {
        using var fixture = new VerifyFixture();
        var options = fixture.Options(probe: true, environmentProbe: new ThrowingEnvironmentProbe());

        var report = await VerifyAsync(fixture, options);

        ReportAssert.HasStatus(report, "e-01", VerifyCheckStatus.Skip);
        Assert.Equal(VerifyOverall.PassWithSkips, report.Overall);
    }

    [Fact]
    public async Task Non_existent_game_directory_returns_argument_error()
    {
        using var fixture = new VerifyFixture();
        var options = fixture.Options();
        options.GameDirectory = Path.Combine(fixture.Temp.Root, "does-not-exist");

        var result = await fixture.RunAsync(options);

        Assert.False(result.Ok);
        Assert.Equal("E_ARG_INVALID", result.Error!.Code);
    }

    [Fact]
    public async Task Missing_game_log_with_external_speech_log_skips_bepinex_log_checks()
    {
        using var fixture = new VerifyFixture();
        File.Delete(fixture.LogPath);
        var external = Path.Combine(fixture.Temp.Root, "external-speech.log");
        File.WriteAllText(external, "[Speech] 早上好\n[Speech] 今天天气不错\n");
        var options = fixture.Options();
        options.SpeechLogPath = external;

        var report = await VerifyAsync(fixture, options);

        ReportAssert.HasStatus(report, "b-05", VerifyCheckStatus.Skip);
        ReportAssert.HasStatus(report, "b-06", VerifyCheckStatus.Skip);
        ReportAssert.HasStatus(report, "s-02", VerifyCheckStatus.Pass);
        Assert.Equal(VerifyOverall.PassWithSkips, report.Overall);
        Assert.Contains(report.Unverified, item => item.Contains("外部语音日志", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Relative_speech_log_path_resolves_against_working_directory_first()
    {
        using var fixture = new VerifyFixture();
        File.Delete(fixture.LogPath);
        var name = "rel-speech-" + Guid.NewGuid().ToString("N") + ".log";
        File.WriteAllText(name, "[Speech] 早春\n[Speech] 下雨了\n");
        try
        {
            var options = fixture.Options();
            options.SpeechLogPath = name;

            var report = await VerifyAsync(fixture, options);

            ReportAssert.HasStatus(report, "s-01", VerifyCheckStatus.Pass);
            ReportAssert.HasStatus(report, "s-02", VerifyCheckStatus.Pass);
            Assert.Equal(VerifyOverall.PassWithSkips, report.Overall);
        }
        finally
        {
            File.Delete(name);
        }
    }

    private static async Task<VerifyReport> VerifyAsync(VerifyFixture fixture, VerifyOptions? options = null)
    {
        var result = await fixture.RunAsync(options);
        Assert.True(result.Ok, result.Error?.Message);
        return result.Value!;
    }
}
