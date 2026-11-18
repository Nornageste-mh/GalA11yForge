using System.Text.RegularExpressions;

namespace GalA11yForge.Adapters.Renpy.Internal;

/// <summary>
/// 只读盘点一个 Ren'Py 游戏目录。不解析 .rpa 内容，不写盘，不启动游戏；
/// 读不到的文件按“未确认”处理，让上层把它写进 unverified，而不是猜。
/// </summary>
internal static class RenpyInspector
{
    private static readonly Regex VersionAssignment = new(
        @"^\s*version\s*=\s*['""](?<v>[^'""]*)['""]",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex VersionTupleAssignment = new(
        @"version_tuple\s*=\s*\((?<body>[\s\S]*?)\)",
        RegexOptions.CultureInvariant);

    private static readonly Regex VersionNameAssignment = new(
        @"^\s*version_name\s*=\s*['""](?<v>[^'""]*)['""]",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex BranchAssignment = new(
        @"^\s*branch\s*=\s*['""](?<v>[^'""]*)['""]",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex NightlyAssignment = new(
        @"^\s*nightly\s*=\s*(?<v>True|False)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex OfficialAssignment = new(
        @"^\s*official\s*=\s*(?<v>True|False)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex NumericVersion = new(
        @"^[0-9]+(?:\.[0-9]+)+$",
        RegexOptions.CultureInvariant);

    public static RenpyGameFacts Inspect(string gameDirectory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
        var hasRenpy = Directory.Exists(Path.Combine(root, "renpy"));
        var hasGame = Directory.Exists(Path.Combine(root, "game"));
        var hasInit = File.Exists(Path.Combine(root, "renpy", "__init__.py"));

        if (!hasRenpy && !hasGame)
        {
            return new RenpyGameFacts { Root = root };
        }

        var version = ParseVersion(root);
        var libs = EnumerateChildDirectories(Path.Combine(root, "lib"))
            .Select(path => "lib/" + Path.GetFileName(path))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var (pythonMajor, bitness) = InferPython(root, libs);
        var rpa = EnumerateRpa(root);
        var loose = EnumerateLooseScripts(root);
        var overlays = EnumerateOverlayDirectories(root);
        var accessibility = FindAccessibilityScript(root);
        var nvda = EnumerateNvdaClients(root);
        var vbs = EnumerateRootVbs(root);
        var hooks = InspectHooks(root);
        var steamAppId = File.Exists(Path.Combine(root, "steam_appid.txt"))
            || File.Exists(Path.Combine(root, "game", "steam_appid.txt"));

        return new RenpyGameFacts
        {
            Root = root,
            HasRenpyDirectory = hasRenpy,
            HasGameDirectory = hasGame,
            HasRenpyInit = hasInit,
            ExecutableName = FindExecutable(root),
            EngineVersion = version.Version,
            VersionSource = version.Source,
            VersionName = version.VersionName,
            Branch = version.Branch,
            Nightly = version.Nightly,
            Official = version.Official,
            VersionTuple = version.Tuple,
            PythonMajor = pythonMajor,
            Bitness = bitness,
            PythonLibs = libs,
            RpaFiles = rpa,
            RpaBytes = rpa.Sum(item => item.Bytes),
            LooseRpy = loose.Rpy,
            LooseRpycCount = loose.Rpyc,
            OverlayDirectories = overlays,
            AccessibilityScript = accessibility.Path,
            AccessibilityBytes = accessibility.Bytes,
            NvdaClients = nvda,
            RootVbs = vbs,
            HasRootSayVbs = vbs.Any(name => Path.GetFileName(name).Equals("a11y_say.vbs", StringComparison.OrdinalIgnoreCase)),
            HasRootManifest = File.Exists(Path.Combine(root, "a11y_manifest.json")),
            IsSteamPath = root.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment.Equals("steamapps", StringComparison.OrdinalIgnoreCase)),
            HasSteamAppId = steamAppId,
            Hooks = hooks,
        };
    }

    private static VersionInfo ParseVersion(string root)
    {
        var vcPath = Path.Combine(root, "renpy", "vc_version.py");
        var initPath = Path.Combine(root, "renpy", "__init__.py");
        var vc = RenpySupport.ReadTextOrNull(vcPath);
        var init = RenpySupport.ReadTextOrNull(initPath);

        string? version = null;
        string? source = null;
        List<int> tuple = new();

        // 8.x：renpy/vc_version.py 里直接有 version = '8.5.2.26010301'。
        var vcMatch = vc is null ? Match.Empty : VersionAssignment.Match(vc);
        if (vcMatch.Success && TryNormalizeVersion(vcMatch.Groups["v"].Value, out var vcVersion))
        {
            version = vcVersion;
            source = $"renpy/vc_version.py:{RenpySupport.LineNumberAt(vc!, vcMatch.Index)}";
            tuple = SplitVersion(vcVersion);
        }

        // 6.99 / 7.x：version_tuple = (6, 99, 12, 4, vc_version) 在 renpy/__init__.py 里。
        if (version is null && init is not null)
        {
            var tupleMatch = VersionTupleAssignment.Match(init);
            if (tupleMatch.Success)
            {
                var body = tupleMatch.Groups["body"].Value;
                var numbers = new List<int>();
                foreach (var token in body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!int.TryParse(token, out var number) || number < 0)
                    {
                        break;
                    }

                    numbers.Add(number);
                }

                if (numbers.Count >= 2)
                {
                    version = string.Join('.', numbers);
                    tuple = numbers;
                    source = $"renpy/__init__.py:{RenpySupport.LineNumberAt(init, tupleMatch.Index)}";
                }
            }
        }

        // 最后兜底：某些魔改版只留下 version = "Ren'Py 7.4.11.2266" 这种字面量。
        if (version is null)
        {
            foreach (var (path, text, relative) in new[]
                     {
                         (vcPath, vc, "renpy/vc_version.py"),
                         (initPath, init, "renpy/__init__.py"),
                     })
            {
                _ = path;
                if (text is null)
                {
                    continue;
                }

                foreach (Match match in VersionAssignment.Matches(text))
                {
                    if (TryNormalizeVersion(match.Groups["v"].Value, out var literal))
                    {
                        version = literal;
                        source = $"{relative}:{RenpySupport.LineNumberAt(text, match.Index)}";
                        tuple = SplitVersion(literal);
                        break;
                    }
                }

                if (version is not null)
                {
                    break;
                }
            }
        }

        var versionName = FirstMatch(vc, init, VersionNameAssignment);
        var branch = FirstMatch(vc, init, BranchAssignment);
        var nightly = FirstBool(vc, init, NightlyAssignment);
        var official = FirstBool(vc, init, OfficialAssignment);

        return new VersionInfo(version, source, versionName, branch, nightly, official, tuple);
    }

    private static string? FirstMatch(string? vc, string? init, Regex regex)
    {
        foreach (var text in new[] { vc, init })
        {
            if (text is null)
            {
                continue;
            }

            var match = regex.Match(text);
            if (match.Success)
            {
                return match.Groups["v"].Value;
            }
        }

        return null;
    }

    private static bool? FirstBool(string? vc, string? init, Regex regex)
    {
        foreach (var text in new[] { vc, init })
        {
            if (text is null)
            {
                continue;
            }

            var match = regex.Match(text);
            if (match.Success)
            {
                return match.Groups["v"].Value == "True";
            }
        }

        return null;
    }

    private static bool TryNormalizeVersion(string raw, out string version)
    {
        version = string.Empty;
        var value = (raw ?? string.Empty).Trim();
        if (value.StartsWith("Ren'Py ", StringComparison.OrdinalIgnoreCase))
        {
            value = value["Ren'Py ".Length..].Trim();
        }

        value = value.TrimEnd('.');
        if (!NumericVersion.IsMatch(value))
        {
            return false;
        }

        version = value;
        return true;
    }

    private static List<int> SplitVersion(string version)
        => version.Split('.').Select(part => int.TryParse(part, out var number) ? number : -1)
            .Where(number => number >= 0)
            .ToList();

    private static (int Major, int Bitness) InferPython(string root, IReadOnlyList<string> libs)
    {
        if (libs.Count == 0)
        {
            return (0, 0);
        }

        var windows = libs.Where(name => name.Contains("windows", StringComparison.OrdinalIgnoreCase)).ToList();
        var candidate = windows.FirstOrDefault(name => name.Contains("py3", StringComparison.OrdinalIgnoreCase))
                        ?? windows.FirstOrDefault();
        if (candidate is not null)
        {
            var major = candidate.Contains("py3", StringComparison.OrdinalIgnoreCase)
                        || candidate.Contains("python3", StringComparison.OrdinalIgnoreCase)
                ? 3
                : 2;
            var bitness = candidate.Contains("x86_64", StringComparison.OrdinalIgnoreCase)
                          || candidate.Contains("amd64", StringComparison.OrdinalIgnoreCase)
                          || candidate.Contains("64", StringComparison.OrdinalIgnoreCase)
                ? 64
                : 32;
            return (major, bitness);
        }

        var anyPy3 = libs.Any(name => name.Contains("py3", StringComparison.OrdinalIgnoreCase)
                                      || name.Contains("python3", StringComparison.OrdinalIgnoreCase));
        var anyPy2 = libs.Any(name => name.Contains("py2", StringComparison.OrdinalIgnoreCase)
                                      || name.Contains("pythonlib2", StringComparison.OrdinalIgnoreCase));
        if (anyPy3)
        {
            return (3, 0);
        }

        return anyPy2 ? (2, 0) : (0, 0);
    }

    private static List<RenpyRpaInfo> EnumerateRpa(string root)
    {
        var game = Path.Combine(root, "game");
        if (!Directory.Exists(game))
        {
            return new List<RenpyRpaInfo>();
        }

        var list = new List<RenpyRpaInfo>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(game, "*.rpa", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                long bytes;
                try
                {
                    bytes = new FileInfo(path).Length;
                }
                catch (IOException)
                {
                    continue;
                }

                list.Add(new RenpyRpaInfo
                {
                    Name = Path.GetFileName(path),
                    Bytes = bytes,
                    Header = ReadHeader(path),
                });
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 只读盘点失败时保留空列表，由上层在证据/未验证里说明。
        }
        catch (IOException)
        {
        }

        return list;
    }

    private static string? ReadHeader(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16, FileOptions.SequentialScan);
            var buffer = new byte[8];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return null;
            }

            var header = System.Text.Encoding.ASCII.GetString(buffer, 0, read).Trim('\0', ' ', '\r', '\n');
            return header.Length == 0 ? null : header;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (List<string> Rpy, int Rpyc) EnumerateLooseScripts(string root)
    {
        var game = Path.Combine(root, "game");
        var rpy = new List<string>();
        var rpyc = 0;
        if (!Directory.Exists(game))
        {
            return (rpy, rpyc);
        }

        Walk(game, string.Empty, 0);

        rpy.Sort(StringComparer.OrdinalIgnoreCase);
        return (rpy, rpyc);

        void Walk(string directory, string relative, int depth)
        {
            if (depth > 3)
            {
                return;
            }

            IEnumerable<string> files;
            IEnumerable<string> dirs;
            try
            {
                files = Directory.EnumerateFiles(directory).ToList();
                dirs = Directory.EnumerateDirectories(directory).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".rpy", StringComparison.OrdinalIgnoreCase))
                {
                    rpy.Add(relative.Length == 0 ? name : relative + "/" + name);
                }
                else if (name.EndsWith(".rpyc", StringComparison.OrdinalIgnoreCase))
                {
                    rpyc++;
                }
            }

            foreach (var child in dirs)
            {
                var name = Path.GetFileName(child);
                if (name.Equals("cache", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("saves", StringComparison.OrdinalIgnoreCase)
                    || name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Walk(child, relative.Length == 0 ? name : relative + "/" + name, depth + 1);
            }
        }
    }

    private static List<string> EnumerateOverlayDirectories(string root)
    {
        var game = Path.Combine(root, "game");
        if (!Directory.Exists(game))
        {
            return new List<string>();
        }

        try
        {
            return Directory.EnumerateDirectories(game, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => name is not null && name.Contains("a11y", StringComparison.OrdinalIgnoreCase))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return new List<string>();
        }
        catch (IOException)
        {
            return new List<string>();
        }
    }

    private static (string? Path, long Bytes) FindAccessibilityScript(string root)
    {
        foreach (var relative in new[] { "renpy/common/00accessibility.rpy", "game/00accessibility.rpy" })
        {
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                try
                {
                    return (relative, new FileInfo(full).Length);
                }
                catch (IOException)
                {
                    return (relative, 0);
                }
            }
        }

        return (null, 0);
    }

    private static List<RenpyNvdaClientInfo> EnumerateNvdaClients(string root)
    {
        var results = new List<RenpyNvdaClientInfo>();
        var directories = new List<string> { root, Path.Combine(root, "game") };
        var lib = Path.Combine(root, "lib");
        if (Directory.Exists(lib))
        {
            directories.AddRange(EnumerateChildDirectories(lib));
        }

        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "nvdaControllerClient*.dll", SearchOption.TopDirectoryOnly))
                {
                    var relative = RenpySupport.Relative(root, file);
                    if (results.Any(item => item.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var name = Path.GetFileName(file);
                    var bitness = name.Contains("64", StringComparison.OrdinalIgnoreCase) ? 64
                        : name.Contains("32", StringComparison.OrdinalIgnoreCase) ? 32
                        : 0;
                    results.Add(new RenpyNvdaClientInfo
                    {
                        RelativePath = relative,
                        Bitness = bitness,
                        Bytes = new FileInfo(file).Length,
                    });
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return results.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> EnumerateRootVbs(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.vbs", SearchOption.TopDirectoryOnly)
                .Select(path => Path.GetFileName(path))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException)
        {
            return new List<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    private static string? FindExecutable(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.exe", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => name is not null
                               && !name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
                               && !name.Equals("python.exe", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static RenpyHookFacts InspectHooks(string root)
    {
        var ast = RenpySupport.ReadLinesOrEmpty(Path.Combine(root, "renpy", "ast.py"));
        var config = RenpySupport.ReadLinesOrEmpty(Path.Combine(root, "renpy", "config.py"));
        var focus = RenpySupport.ReadLinesOrEmpty(Path.Combine(root, "renpy", "display", "focus.py"));
        var exports = RenpySupport.ReadTextOrNull(Path.Combine(root, "renpy", "exports.py"));

        return new RenpyHookFacts
        {
            AstSayFilterLines = RenpySupport.FindLineNumbers(ast, "say_menu_text_filter"),
            ConfigSayFilterLine = FindDefinitionLine(config, "say_menu_text_filter"),
            ConfigTtsFunctionLine = FindDefinitionLine(config, "tts_function"),
            ConfigPeriodicCallbacksLine = FindDefinitionLine(config, "periodic_callbacks"),
            FocusListLine = RenpySupport.FindLineNumbers(focus, "focus_list", limit: 1).FirstOrDefault(),
            HasGetScreenExport = exports is null
                ? null
                : exports.Contains("def get_screen", StringComparison.Ordinal),
        };
    }

    private static int? FindDefinitionLine(string[] lines, string name)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(name + " =", StringComparison.Ordinal)
                || trimmed.StartsWith(name + "=", StringComparison.Ordinal))
            {
                return i + 1;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateChildDirectories(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        string[] children;
        try
        {
            children = Directory.GetDirectories(directory);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var child in children.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            yield return child;
        }
    }

    private sealed record VersionInfo(
        string? Version,
        string? Source,
        string? VersionName,
        string? Branch,
        bool? Nightly,
        bool? Official,
        List<int> Tuple);
}
