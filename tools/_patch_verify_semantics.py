import pathlib

p = pathlib.Path(r'D:\DSHWorkSpace\src\Forge.Cli\PipelineCommands.cs')
s = p.read_text(encoding='utf-8')
orig = s

# 1) verify: skip install-mode self-check when no patch is actually installed.
old = '''        var manifestPath = InstallManifestStore.DefaultPath(resolved.Workspace, resolved.Slug);
        var mode = options.Rollback ? VerifyMode.Rollback : VerifyMode.Install;
        var isRenpy = IsRenpy(resolved);
'''
new = '''        var manifestPath = InstallManifestStore.DefaultPath(resolved.Workspace, resolved.Slug);
        var mode = options.Rollback ? VerifyMode.Rollback : VerifyMode.Install;
        var isRenpy = IsRenpy(resolved);

        if (mode == VerifyMode.Install && !IsPatchInstalled(resolved, manifestPath))
        {
            // 回滚后账本文件仍保留（用于追溯），所以不能只看清单存在与否；
            // 这里核对“清单中本工具新增的文件是否还在、且哈希一致”，再决定是否跑安装后自检。
            var notInstalled = new Dictionary<string, object?>
            {
                ["engine"] = resolved.Detection.Family,
                ["mode"] = "install",
                ["overall"] = "not_installed",
                ["checks"] = 0,
                ["failed"] = 0,
                ["unverified"] = 1,
                ["note"] = "没有检测到已安装的补丁（未安装或已回滚）；跳过安装后自检。",
            };
            await stdout.WriteLineAsync("[提示] 当前没有检测到已安装的补丁（可能未安装或已回滚），跳过安装后自检；这不等于通过。").ConfigureAwait(false);
            await stdout.WriteLineAsync($"  清单：{manifestPath}").ConfigureAwait(false);
            return await WriteSuccessAsync(commandText, "未安装补丁：跳过安装后自检。", notInstalled, stdout, stderr, options.Json).ConfigureAwait(false);
        }
'''
assert old in s
s = s.replace(old, new, 1)

# 2) archive each verify report under a timestamped name (verify.md kept for compatibility).
old2 = '''        if (!write.Ok || write.Value is null)
        {
            throw new CliFailure(write.Error ?? ForgeError.Create(ForgeErrorCodes.Io, "初检报告写入失败。", null));
        }

        var failed = verifyReport.FailCount;'''
new2 = '''        if (!write.Ok || write.Value is null)
        {
            throw new CliFailure(write.Error ?? ForgeError.Create(ForgeErrorCodes.Io, "初检报告写入失败。", null));
        }

        string? archivedMarkdown = null;
        try
        {
            var directory = Path.GetDirectoryName(write.Value.MarkdownPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                archivedMarkdown = Path.Combine(directory, $"verify-{stamp}.md");
                File.Copy(write.Value.MarkdownPath, archivedMarkdown, overwrite: false);
            }
        }
        catch (IOException)
        {
            // 存档失败不影响主报告；数据里如实留 null。
            archivedMarkdown = null;
        }

        var failed = verifyReport.FailCount;'''
assert old2 in s
s = s.replace(old2, new2, 1)

# 3) expose archived path in JSON envelope.
old3 = '''            ["jsonPath"] = write.Value.JsonPath,
            ["markdownPath"] = write.Value.MarkdownPath,
        };'''
new3 = '''            ["jsonPath"] = write.Value.JsonPath,
            ["markdownPath"] = write.Value.MarkdownPath,
            ["archivedMarkdownPath"] = archivedMarkdown,
        };'''
assert old3 in s
s = s.replace(old3, new3, 1)

# 4) helper IsPatchInstalled.
anchor = '''    private static async Task<int> RunVerifyAsync('''
helper = '''    /// <summary>账本存在不等于补丁还在：核对清单里“本工具新增”的文件是否还存在且哈希一致。</summary>
    private static bool IsPatchInstalled(Resolved resolved, string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        var loaded = InstallManifestStore.Load(resolved.Workspace, manifestPath);
        if (!loaded.Ok || loaded.Value is null || loaded.Value.Files.Count == 0)
        {
            return false;
        }

        foreach (var entry in loaded.Value.Files)
        {
            if (entry.Action != DeployFileAction.Add)
            {
                continue;
            }

            var path = Path.Combine(resolved.GameDirectory, entry.RelativePath);
            if (!File.Exists(path))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Sha256After)
                || FileHashes.MatchesFile(path, entry.Sha256After))
            {
                return true;
            }
        }

        return false;
    }

'''
assert anchor in s
s = s.replace(anchor, helper + anchor, 1)

p.write_text(s, encoding='utf-8')
print('patched verify:', len(orig), '->', len(s))
