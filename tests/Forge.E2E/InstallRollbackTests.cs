using System.Security.Cryptography;
using System.Text.Json;
using Xunit.Sdk;

namespace Forge.E2E;

public sealed class InstallRollbackTests
{
    [E2EFact]
    public void Install_ThenRollback_RestoresGameDirectory()
    {
        var sandbox = E2ESandbox.Create("install-rollback");
        var game = sandbox.CopyFixtureGame();
        var before = HashSnapshot.Capture(game);

        Pipeline.Detect(sandbox, game);
        Pipeline.Analyze(sandbox, game);
        Pipeline.Plan(sandbox, game);
        Pipeline.Generate(sandbox, game);
        Pipeline.Build(
            sandbox,
            game,
            "install 需要 build 产物（DeployAsync 要求 BuildResult）。"
            + "若 code=E_DEPENDENCY_MISSING 或提示缺少 BepInEx 5.4.23.5 包/离线缓存，这是本机依赖问题，install/rollback 无法验证。");

        // 只对 sandbox 里的假游戏副本执行 install，绝不触碰真实游戏目录。
        var install = ForgeCli.Run("install", game, sandbox.Workspace, sandbox.OutRoot);
        E2EAssert.Succeeded(
            install,
            "install（对假游戏临时副本，非真实游戏）",
            allowPartial: false,
            hint: $"install 需要 build 成功且工作区/BepInEx 缓存可用；sandbox：{sandbox.Root}");

        var afterInstall = HashSnapshot.Capture(game);
        var installChanges = HashSnapshot.Diff(before, afterInstall);
        Assert.True(
            installChanges.Count > 0,
            $"install 返回成功但游戏目录没有任何文件变化（哈希完全一致），无法证明补丁已落盘。sandbox：{sandbox.Root}");

        var manifestPath = ArtifactLocator.RequireNewest(
            "部署账本",
            new[] { sandbox.Workspace, sandbox.OutRoot },
            "install-manifest.json",
            "deploy-manifest.json");
        var manifest = ReadManifest(manifestPath);
        Assert.True(manifest.Entries.Count > 0, $"A7：部署账本 files 为空：{manifestPath}");

        // A7-1：账本必须列全所有新增/修改/删除的文件。
        var manifestPaths = manifest.Entries
            .Select(entry => NormalizeRelative(entry.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unlisted = installChanges
            .Where(change => !manifestPaths.Contains(NormalizeRelative(change.RelativePath)))
            .Select(change => $"[{change.Kind}] {change.RelativePath}")
            .ToList();
        Assert.True(
            unlisted.Count == 0,
            $"A7：install 改动了 {installChanges.Count} 个文件，但账本未列全以下路径：{Environment.NewLine}- {Bullet(unlisted)}"
            + $"{Environment.NewLine}账本：{manifestPath}");

        // A7-2：Add/Replace 目标安装后应存在；需要还原的文件必须有备份且哈希匹配。
        var missingTargets = new List<string>();
        var missingBackups = new List<string>();
        var badBackupHashes = new List<string>();

        foreach (var entry in manifest.Entries)
        {
            var relative = NormalizeRelative(entry.RelativePath);
            var action = entry.Action.Trim().ToLowerInvariant();

            if (action is "add" or "replace")
            {
                var target = Path.Combine(game, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(target))
                {
                    missingTargets.Add(relative);
                }
            }

            if (!entry.ExistedBefore || action == "add")
            {
                continue;
            }

            var backup = ResolveBackup(manifestPath, sandbox.Workspace, manifest.BackupRoot, entry.BackupPath);
            if (backup is null || !File.Exists(backup))
            {
                missingBackups.Add($"{relative}（BackupPath={entry.BackupPath ?? "(空)"}）");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(entry.Sha256Before))
            {
                var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(backup))).ToLowerInvariant();
                if (!string.Equals(actual, entry.Sha256Before, StringComparison.OrdinalIgnoreCase))
                {
                    badBackupHashes.Add($"{relative}（期望 {Short(entry.Sha256Before)}，实际 {Short(actual)}）");
                }
            }
        }

        Assert.True(
            missingTargets.Count == 0,
            $"A7：账本中这些 Add/Replace 文件安装后不存在：{Environment.NewLine}- {Bullet(missingTargets)}"
            + $"{Environment.NewLine}账本：{manifestPath}");

        Assert.True(
            missingBackups.Count == 0,
            $"A7：这些被替换/删除的文件没有可用备份：{Environment.NewLine}- {Bullet(missingBackups)}"
            + $"{Environment.NewLine}账本：{manifestPath}");

        Assert.True(
            badBackupHashes.Count == 0,
            $"A7：备份文件哈希与账本 Sha256Before 不一致：{Environment.NewLine}- {Bullet(badBackupHashes)}"
            + $"{Environment.NewLine}账本：{manifestPath}");

        var rollback = RunRollback(sandbox, game, manifestPath, out var attempts);
        if (rollback is null || rollback.ExitCode != 0)
        {
            throw new XunitException(
                "install 成功，但没有可用的回滚入口，A8 无法自动验收。"
                + $"{Environment.NewLine}已尝试：{Environment.NewLine}- {Bullet(attempts)}"
                + (rollback is null ? string.Empty : $"{Environment.NewLine}最后一次输出：{Environment.NewLine}{rollback.Describe()}")
                + $"{Environment.NewLine}已知接口缺口：docs/06 的 CLI 命令列表没有 rollback；"
                + "src/Forge.Build/PluginDeployer 只有 RollbackFromFile/RollbackAsync API，尚未暴露成 CLI 命令。"
                + "请 Lead 提供 `forge rollback --manifest <install-manifest.json>`，或明确 uninstall.ps1 的稳定参数契约后重跑本测试。"
                + $"{Environment.NewLine}sandbox：{sandbox.Root}");
        }

        var afterRollback = HashSnapshot.Capture(game);
        var remaining = HashSnapshot.Diff(before, afterRollback);
        var added = afterRollback.Keys
            .Except(before.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(
            remaining.Count == 0,
            $"A8：rollback 后游戏目录与安装前不一致（哈希差异 {remaining.Count} 项）：{Environment.NewLine}"
            + $"{HashSnapshot.Describe(remaining)}{Environment.NewLine}sandbox：{sandbox.Root}");

        Assert.True(
            added.Count == 0,
            $"A8：rollback 后仍有 install 新增的文件未删除：{Environment.NewLine}- {Bullet(added)}"
            + $"{Environment.NewLine}回滚输出：{Environment.NewLine}{rollback.Describe()}{Environment.NewLine}sandbox：{sandbox.Root}");
    }

    private static CommandResult? RunRollback(
        E2ESandbox sandbox,
        string game,
        string manifestPath,
        out List<string> attempts)
    {
        attempts = new List<string>();

        // ① 契约外的探测：若 Lead 后续加了 forge rollback，这里直接可用。
        var cliRollback = ForgeCli.Run(
            "rollback",
            game,
            sandbox.Workspace,
            sandbox.OutRoot,
            new[] { "--manifest", manifestPath });

        if (cliRollback.ExitCode == 0)
        {
            return cliRollback;
        }

        attempts.Add($"forge rollback --manifest：退出码 {cliRollback.ExitCode}；{FirstMeaningfulLine(cliRollback)}");

        // ② 退而求其次：运行生成工程自带的反安装脚本，并按脚本 param 名映射已知参数。
        var uninstall = ArtifactLocator
            .FindFilesContaining(new[] { sandbox.Workspace, sandbox.OutRoot }, "uninstall", ".ps1")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (uninstall is null)
        {
            attempts.Add("未找到 uninstall*.ps1");
            return null;
        }

        var scriptArgs = BuildScriptArguments(uninstall, game, sandbox.Workspace, manifestPath);
        var scriptResult = ForgeCli.RunScript(uninstall, scriptArgs, sandbox.Root);
        attempts.Add($"uninstall 脚本：{scriptResult.Invocation} → 退出码 {scriptResult.ExitCode}");
        return scriptResult;
    }

    private static IReadOnlyList<string> BuildScriptArguments(
        string scriptPath,
        string game,
        string workspace,
        string manifestPath)
    {
        var text = File.ReadAllText(scriptPath);
        var parameterNames = new List<string>();

        var paramBlock = System.Text.RegularExpressions.Regex.Match(
            text,
            @"param\s*\((?<body>.*?)\)",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (paramBlock.Success)
        {
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                         paramBlock.Groups["body"].Value, @"\$(\w+)"))
            {
                var name = match.Groups[1].Value;
                if (!parameterNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    parameterNames.Add(name);
                }
            }
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GameDir"] = game,
            ["GameDirectory"] = game,
            ["Game"] = game,
            ["Workspace"] = workspace,
            ["WorkspaceRoot"] = workspace,
            ["Manifest"] = manifestPath,
            ["ManifestPath"] = manifestPath,
            ["InstallManifest"] = manifestPath,
        };

        var args = new List<string>();
        foreach (var name in parameterNames)
        {
            if (values.TryGetValue(name, out var value))
            {
                args.Add($"-{name}");
                args.Add(value);
            }
        }

        return args;
    }

    private static ManifestData ReadManifest(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        if (!JsonHelpers.TryGetProperty(root, "files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            throw new XunitException(
                $"部署账本缺少 files 数组：{path}{Environment.NewLine}{JsonHelpers.ReadObjectText(path)}");
        }

        var backupRoot = JsonHelpers.TryGetString(root, out var rootBackup, "backupRoot") ? rootBackup : string.Empty;
        var entries = new List<ManifestEntry>();

        foreach (var item in files.EnumerateArray())
        {
            if (!JsonHelpers.TryGetString(item, out var relative, "relativePath", "path") || string.IsNullOrWhiteSpace(relative))
            {
                throw new XunitException(
                    $"部署账本条目缺少 relativePath：{path}{Environment.NewLine}{JsonHelpers.Format(item)}");
            }

            JsonHelpers.TryGetString(item, out var action, "action");
            JsonHelpers.TryGetString(item, out var backupPath, "backupPath");
            JsonHelpers.TryGetString(item, out var shaBefore, "sha256Before");

            var existedBefore = JsonHelpers.TryGetBool(item, out var existed, "existedBefore") && existed;
            if (!existedBefore && !string.IsNullOrWhiteSpace(shaBefore))
            {
                // 兼容没有 existedBefore 字段的旧账本：有原哈希即视为安装前存在。
                existedBefore = true;
            }

            entries.Add(new ManifestEntry(
                relative,
                action ?? string.Empty,
                existedBefore,
                string.IsNullOrWhiteSpace(backupPath) ? null : backupPath,
                string.IsNullOrWhiteSpace(shaBefore) ? null : shaBefore));
        }

        return new ManifestData(backupRoot, entries);
    }

    private static string? ResolveBackup(string manifestPath, string workspace, string backupRoot, string? backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            return null;
        }

        if (Path.IsPathRooted(backupPath))
        {
            return backupPath;
        }

        var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(backupRoot))
        {
            candidates.Add(Path.IsPathRooted(backupRoot)
                ? Path.Combine(backupRoot, backupPath)
                : Path.Combine(workspace, backupRoot, backupPath));

            if (!Path.IsPathRooted(backupRoot))
            {
                candidates.Add(Path.Combine(manifestDirectory, backupRoot, backupPath));
            }
        }

        candidates.Add(Path.Combine(workspace, "backup", backupPath));
        candidates.Add(Path.Combine(workspace, backupPath));
        candidates.Add(Path.Combine(manifestDirectory, backupPath));

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string NormalizeRelative(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private static string FirstMeaningfulLine(CommandResult result)
    {
        var text = result.Envelope?.Message;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = result.Combined
                .Replace("\r\n", "\n")
                .Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0);
        }

        text ??= "(无输出)";
        return text.Length <= 240 ? text : text[..240] + "…";
    }

    private static string Short(string? hash)
    {
        return string.IsNullOrEmpty(hash) ? "(空)" : hash.Length <= 12 ? hash : hash[..12];
    }

    private static string Bullet(IEnumerable<string> items)
    {
        return string.Join($"{Environment.NewLine}- ", items);
    }

    private sealed record ManifestData(string BackupRoot, List<ManifestEntry> Entries);

    private sealed record ManifestEntry(
        string RelativePath,
        string Action,
        bool ExistedBefore,
        string? BackupPath,
        string? Sha256Before);
}
