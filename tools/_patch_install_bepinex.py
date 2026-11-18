import io, re, pathlib

p = pathlib.Path(r'D:\DSHWorkSpace\src\Forge.Cli\PipelineCommands.cs')
s = p.read_text(encoding='utf-8')
orig = s

# 1) install: resolve BepInEx package path (Mono) using shared resolver.
old_install = '''        var deploymentOptions = new DeploymentOptions
        {
            BepInExPackagePath = options.BepInExPath,
            AllowRedeploy = options.Force,
            PluginVersion = build.Metadata.TryGetValue("version", out var version) ? version : null,
            SpeechRuntimeDirectory = options.SpeechRuntimePath,
        };'''
new_install = '''        var deploymentOptions = new DeploymentOptions
        {
            AllowRedeploy = options.Force,
            PluginVersion = build.Metadata.TryGetValue("version", out var version) ? version : null,
            SpeechRuntimeDirectory = options.SpeechRuntimePath,
        };
        if (isIl2Cpp)
        {
            deploymentOptions.BepInExPackagePath = options.BepInExPath;
        }
        else if (!isRenpy)
        {
            // 与 build 阶段共用同一套“工作区 + exe 旁 bepinex/”查找逻辑，
            // 这样发布包里自带 app\\bepinex\\ 时 install 不必再传 --bepinex。
            var package = ResolveBepInExPackage(resolved, options);
            if (!package.Ok || string.IsNullOrWhiteSpace(package.Value))
            {
                throw new CliFailure(package.Error ?? ForgeError.Create(
                    ForgeErrorCodes.DependencyMissing,
                    "找不到 BepInEx 离线包，无法部署。",
                    "把 BepInEx_win_x64_5.4.23.5.zip 放进工作区缓存或 forge.exe 旁边的 bepinex\\ 目录，或用 --bepinex <zip> 指定路径。"));
            }

            deploymentOptions.BepInExPackagePath = package.Value;
        }
'''
assert old_install in s
s = s.replace(old_install, new_install, 1)

# 2) StageBepInExCore: reuse shared resolver (keep Open logic).
old_stage_head = '''    private static ForgeResult<string> StageBepInExCore(Resolved resolved, CliOptions options)
    {
        var searchRoots = new List<string> { resolved.Workspace.RootPath };
        void AddRoot(string? directory)
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                searchRoots.Add(directory);
            }
        }

        // 非单文件运行时 AppContext.BaseDirectory 就是 exe 目录；单文件发布时它可能指向解包临时目录，
        // 因此再用 Environment.ProcessPath（真实的 exe 路径）补一个根。
        AddRoot(AppContext.BaseDirectory);
        AddRoot(Path.GetDirectoryName(Environment.ProcessPath));

        var found = BepInExLocator.FindCandidates(searchRoots, options.BepInExPath);
        if (!found.Ok || found.Value is null || found.Value.Count == 0)
        {
            return ForgeResult<string>.Failure(found.Error ?? ForgeError.Create(
                ForgeErrorCodes.DependencyMissing,
                "找不到 BepInEx 5.4.23.5 离线包，无法准备构建依赖。",
                "把 BepInEx_win_x64_5.4.23.5.zip 放进工作区缓存或 forge.exe 旁边的 bepinex\\\\ 目录，或用 --bepinex <zip> 指定路径。"));
        }

        var package = BepInExPackage.Open(found.Value[0]);'''
new_stage_head = '''    private static ForgeResult<string> StageBepInExCore(Resolved resolved, CliOptions options)
    {
        var packagePath = ResolveBepInExPackage(resolved, options);
        if (!packagePath.Ok || string.IsNullOrWhiteSpace(packagePath.Value))
        {
            return ForgeResult<string>.Failure(packagePath.Error ?? ForgeError.Create(
                ForgeErrorCodes.DependencyMissing,
                "找不到 BepInEx 5.4.23.5 离线包，无法准备构建依赖。",
                "把 BepInEx_win_x64_5.4.23.5.zip 放进工作区缓存或 forge.exe 旁边的 bepinex\\\\ 目录，或用 --bepinex <zip> 指定路径。"));
        }

        var package = BepInExPackage.Open(packagePath.Value);'''
assert old_stage_head in s, 'stage head not found'
s = s.replace(old_stage_head, new_stage_head, 1)

# 3) Insert shared resolver before StageBepInExCore.
anchor = '''    /// <summary>把 BepInEx 离线包解到工作区暂存目录，返回 BepInEx\\core 路径；找不到包时返回失败（调用方可决定是否继续）。</summary>'''
assert anchor in s
resolver = '''    /// <summary>
    /// 在工作区、exe 所在目录（含单文件发布的真实 exe 目录）和 exe 旁的 bepinex\\ 里查找 BepInEx 离线包。
    /// build 与 install 共用，避免“构建能找到、安装找不到”的不一致。
    /// </summary>
    private static ForgeResult<string> ResolveBepInExPackage(Resolved resolved, CliOptions options)
    {
        var searchRoots = new List<string> { resolved.Workspace.RootPath };
        void AddRoot(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            foreach (var existing in searchRoots)
            {
                if (string.Equals(existing, directory, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            searchRoots.Add(directory);
        }

        AddRoot(AppContext.BaseDirectory);
        AddRoot(Path.GetDirectoryName(Environment.ProcessPath));

        var found = BepInExLocator.FindCandidates(searchRoots, options.BepInExPath);
        if (!found.Ok || found.Value is null || found.Value.Count == 0)
        {
            return ForgeResult<string>.Failure(found.Error ?? ForgeError.Create(
                ForgeErrorCodes.DependencyMissing,
                "找不到 BepInEx 5.4.23.5 离线包。",
                "把 BepInEx_win_x64_5.4.23.5.zip 放进工作区缓存或 forge.exe 旁边的 bepinex\\\\ 目录，或用 --bepinex <zip> 指定路径。"));
        }

        return ForgeResult<string>.Success(found.Value[0]);
    }

'''
s = s.replace(anchor, resolver + anchor, 1)

p.write_text(s, encoding='utf-8')
print('patched PipelineCommands:', len(orig), '->', len(s))
