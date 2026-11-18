using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Adapters.Renpy.Internal;

/// <summary>
/// 步骤五：离线静态校验。纯 C# 实现，不调用 python、不启动游戏、不写游戏目录。
/// 覆盖 Python 原型的 A1–A3、B1–B12、C1–C6，并额外检查编码、文件齐全、路径安全与占位符残留。
/// 注意：A3 只是结构自检（括号/引号/缩进/代码块），不等同 CPython 编译；README 已写明。
/// </summary>
internal sealed class RenpyStaticLinter
{
    private static readonly Regex PythonBlockHeader = new(
        @"^\s*(?:init(?:\s+-?\d+)?\s+)?python\s*:\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PythonTokenResidue = new(
        @"@@[A-Z0-9_]+@@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DrivePath = new(
        @"(?<![A-Za-z])[A-Za-z]:[\\/](?!/)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex KeyHandlerDefinition = new(
        @"def _a11y_key_\w+\(\):",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex KeyHandlerWithArgs = new(
        @"def _a11y_key_\w+\((self|ev)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SayFilterBody = new(
        @"def _a11y_say_filter\(s\):(.*?)(?=\n    def )",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public LinterResult Run(string outputRoot, string? expectedGameRoot = null)
    {
        var checks = new List<VerificationCheck>();
        var root = Path.GetFullPath(outputRoot);
        void Add(string name, bool passed, string? detail = null) =>
            checks.Add(new VerificationCheck { Name = name, Passed = passed, Detail = detail });

        if (!Directory.Exists(root))
        {
            Add("D0 输出目录存在", false, root);
            return new LinterResult(checks);
        }

        var platformDirectory = Path.Combine(root, "game", "a11y_platform");
        var rpyFiles = Directory.Exists(platformDirectory)
            ? Directory.EnumerateFiles(platformDirectory, "*.rpy", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList()
            : new List<string>();

        var expectedRpy = RenpyOverlayCatalog.PlatformFileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingRpy = expectedRpy.Where(n => !rpyFiles.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        var extraRpy = rpyFiles.Where(n => !expectedRpy.Contains(n)).ToList();
        Add("A1 平台文件齐全(6)", missingRpy.Count == 0 && extraRpy.Count == 0 && rpyFiles.Count == 6,
            $"实际 {rpyFiles.Count} 个: {string.Join(", ", rpyFiles)}"
            + (missingRpy.Count > 0 ? $"；缺少: {string.Join(", ", missingRpy)}" : string.Empty)
            + (extraRpy.Count > 0 ? $"；多余: {string.Join(", ", extraRpy)}" : string.Empty));

        var allText = new StringBuilder();
        var fileTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var readErrors = new List<string>();
        foreach (var file in RenpyOverlayCatalog.ContentFiles)
        {
            var path = Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var text = RenpySupport.ReadTextOrNull(path);
            if (text is null)
            {
                readErrors.Add($"{file.RelativePath}:不存在");
                fileTexts[file.RelativePath] = string.Empty;
                continue;
            }

            fileTexts[file.RelativePath] = text;
            allText.Append(text).Append('\n');
        }

        var rpyTexts = rpyFiles
            .Select(n => (Name: n, Text: RenpySupport.ReadTextOrNull(Path.Combine(platformDirectory, n)) ?? string.Empty))
            .ToList();

        var a2Bad = rpyTexts
            .Where(p => p.Text.Split('\n').FirstOrDefault()?.Contains("coding: utf-8", StringComparison.Ordinal) != true)
            .Select(p => p.Name)
            .ToList();
        Add("A2 每个 .rpy 首行 utf-8 声明", a2Bad.Count == 0,
            a2Bad.Count == 0 ? $"{rpyTexts.Count} 个文件" : string.Join(", ", a2Bad));

        var syntaxErrors = new List<string>();
        var blockTotal = 0;
        foreach (var (name, text) in rpyTexts)
        {
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var blocks = ExtractPythonBlocks(lines);
            blockTotal += blocks.Count;
            if (blocks.Count == 0)
            {
                syntaxErrors.Add($"{name}:没有 python 代码块");
                continue;
            }

            foreach (var (line, code) in blocks)
            {
                var error = CheckPythonBlockStructure(code);
                if (error is not null)
                {
                    syntaxErrors.Add($"{name}:{line} {error}");
                }
            }
        }

        Add("A3 python 块结构自检", syntaxErrors.Count == 0,
            $"{blockTotal} 个块" + (syntaxErrors.Count == 0 ? string.Empty : "；" + string.Join("; ", syntaxErrors.Take(5))));

        var dialogue = Text("02_dialogue.rpy");
        Add("B1 挂载 config.say_menu_text_filter",
            dialogue.Contains("renpy.config.say_menu_text_filter = _a11y_say_filter", StringComparison.Ordinal));

        var filterMatch = SayFilterBody.Match(dialogue);
        var filterBody = filterMatch.Success ? filterMatch.Groups[1].Value : string.Empty;
        Add("B2 filter 原样返回（红线）",
            filterBody.Contains("return out", StringComparison.Ordinal)
            && !filterBody.Contains("return _a11y_clean", StringComparison.Ordinal)
            && !filterBody.Contains("return text", StringComparison.Ordinal),
            filterMatch.Success ? "已找到函数体" : "未找到 _a11y_say_filter 函数");

        Add("B3 链式保留游戏自己的 filter",
            dialogue.Contains("_A11Y_ORIG_SAY_FILTER", StringComparison.Ordinal)
            && dialogue.Contains("orig(s)", StringComparison.Ordinal));

        var speech = Text("01_speech.rpy");
        Add("B4 接管 config.tts_function",
            speech.Contains("renpy.config.tts_function = _a11y_tts_function", StringComparison.Ordinal));

        var focus = Text("04_focus.rpy");
        Add("B5 强制 self_voicing=False",
            speech.Contains("self_voicing = False", StringComparison.Ordinal)
            && focus.Contains("self_voicing", StringComparison.Ordinal));

        var keys = Text("05_keys.rpy");
        Add("B6 键位在 init 1200 登记",
            keys.Contains("init 1200 python:", StringComparison.Ordinal)
            && keys.Contains("config.underlay.append", StringComparison.Ordinal));

        Add("B7 键处理函数无参数",
            KeyHandlerDefinition.IsMatch(keys)
            && !KeyHandlerWithArgs.IsMatch(keys)
            && keys.Contains("compile_event", StringComparison.Ordinal));

        Add("B8 使用引擎 compile_event 验证键名", keys.Contains("compile_event", StringComparison.Ordinal));

        var contract = Text("00_contract.rpy");
        Add("B9 _tts_all 6.99/8.x 双签名回退",
            contract.Contains("_tts_all(raw=True)", StringComparison.Ordinal)
            && contract.Contains("except TypeError", StringComparison.Ordinal)
            && contract.Contains("_tts_all()", StringComparison.Ordinal));

        var menu = Text("03_menu.rpy");
        Add("B10 focus_list 每次现取（不缓存引用）",
            menu.Contains("renpy.display.focus.focus_list", StringComparison.Ordinal)
            && menu.Contains("list(fl)", StringComparison.Ordinal)
            && !menu.Contains("focus_list = [", StringComparison.Ordinal));

        Add("B11 心跳登记 periodic_callbacks",
            focus.Contains("config.periodic_callbacks.append(_a11y_periodic)", StringComparison.Ordinal));

        Add("B12 日志 + a11y_off.flag 关闭开关",
            contract.Contains("a11y_speech.log", StringComparison.Ordinal)
            && contract.Contains("a11y_off.flag", StringComparison.Ordinal)
            && contract.Contains("_a11y_flag_disabled", StringComparison.Ordinal));

        var combined = allText.ToString();
        var driveHits = DrivePath.Matches(combined).Cast<Match>().Select(m => m.Value).Distinct().Take(3).ToList();
        Add("C1 无硬编码盘符/绝对路径", driveHits.Count == 0, string.Join(", ", driveHits));

        // 与原型 lint 对齐：C2 只扫 6 个 .rpy（README 里解释“磁盘优先于 .rpa”是说明文字，不算写盘）。
        var rpyCombined = string.Join("\n", rpyTexts.Select(p => p.Text)).ToLowerInvariant();
        var withoutRpaBytes = rpyCombined.Replace("rpa_bytes", string.Empty, StringComparison.Ordinal);
        Add("C2 不写 .rpa/不删游戏文件",
            !withoutRpaBytes.Contains("rpa", StringComparison.Ordinal)
            && speech.Contains("os.remove", StringComparison.Ordinal)
            && !rpyCombined.Contains("shutil.rmtree", StringComparison.Ordinal));

        var manifestPath = Path.Combine(root, RenpyOverlayCatalog.ManifestFileName);
        var c3 = CheckManifest(root, manifestPath, expectedGameRoot);
        Add("C3 清单 SHA256 全部对得上", c3.Ok, c3.Detail);

        var deployPath = Path.Combine(root, RenpyOverlayCatalog.DeployPlanFileName);
        var c4 = CheckDeployPlan(deployPath);
        Add("C4 deploy_plan 完整（含 guard）", c4.Ok, c4.Detail);

        var uninstall = Text("uninstall.ps1");
        Add("C5 卸载脚本核对哈希后才删",
            (uninstall.Contains("a11y_manifest.json", StringComparison.Ordinal)
             || uninstall.Contains("$Manifest", StringComparison.Ordinal))
            && uninstall.Contains("Get-FileHash", StringComparison.Ordinal)
            && uninstall.Contains("-Force", StringComparison.Ordinal));

        var big = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (new FileInfo(path).Length > 1 << 20)
                    {
                        big.Add(RenpySupport.Relative(root, path));
                    }
                }
                catch (IOException)
                {
                    // 单个文件读不到不阻断其余检查。
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            big.Add($"枚举失败: {ex.Message}");
        }

        Add("C6 无 >1MB 文件（不含游戏资源）", big.Count == 0, string.Join(", ", big.Take(3)));

        var expectedArtifacts = RenpyOverlayCatalog.CreateArtifacts()
            .Select(a => a.RelativePath)
            .ToList();
        var missingArtifacts = expectedArtifacts
            .Where(p => !File.Exists(Path.Combine(root, p.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();
        Add("D1 11 个生成物齐全", missingArtifacts.Count == 0,
            missingArtifacts.Count == 0 ? "11/11" : "缺少: " + string.Join(", ", missingArtifacts));

        var encodingProblems = new List<string>();
        foreach (var file in RenpyOverlayCatalog.ContentFiles)
        {
            var path = Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            if (hasBom != file.Utf8Bom)
            {
                encodingProblems.Add($"{file.RelativePath}:{(hasBom ? "不应有 BOM" : "缺 UTF-8 BOM")}");
            }

            try
            {
                StrictUtf8.GetString(hasBom ? bytes[3..] : bytes);
            }
            catch (DecoderFallbackException)
            {
                encodingProblems.Add($"{file.RelativePath}:不是合法 UTF-8");
            }
        }

        foreach (var jsonName in new[] { RenpyOverlayCatalog.ManifestFileName, RenpyOverlayCatalog.DeployPlanFileName })
        {
            var path = Path.Combine(root, jsonName);
            if (!File.Exists(path))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                encodingProblems.Add($"{jsonName}:不应有 BOM");
            }
        }

        Add("D2 编码与 BOM 符合约定", encodingProblems.Count == 0, string.Join(", ", encodingProblems));

        var pathProblems = new List<string>();
        if (File.Exists(manifestPath) && RenpySupport.ReadTextOrNull(manifestPath) is { } manifestText)
        {
            try
            {
                using var document = JsonDocument.Parse(manifestText);
                if (document.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var index = 0;
                    foreach (var item in files.EnumerateArray())
                    {
                        index++;
                        if (!item.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String)
                        {
                            pathProblems.Add($"files[{index}].path 缺失");
                            continue;
                        }

                        var relative = pathElement.GetString() ?? string.Empty;
                        try
                        {
                            var normalized = PathSafety.ValidateRelative(relative);
                            if (!seen.Add(normalized))
                            {
                                pathProblems.Add($"{relative}:重复");
                            }

                            var full = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
                            if (!PathSafety.IsUnder(root, full))
                            {
                                pathProblems.Add($"{relative}:越界");
                            }
                        }
                        catch (Exception ex) when (ex is ForgePathException or ArgumentException)
                        {
                            pathProblems.Add($"{relative}:非法相对路径");
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                pathProblems.Add($"清单不是合法 JSON: {ex.Message}");
            }
        }

        Add("D3 清单路径安全（相对/不越界/不重复）", pathProblems.Count == 0, string.Join(", ", pathProblems.Take(3)));

        var tokenHits = new List<string>();
        foreach (var (name, text) in fileTexts)
        {
            if (PythonTokenResidue.IsMatch(text))
            {
                tokenHits.Add(name);
            }
        }

        Add("D4 无 @@TOKEN@@ 残留", tokenHits.Count == 0, string.Join(", ", tokenHits));

        var vbsPath = Path.Combine(root, "a11y_say.vbs");
        var vbsAscii = File.Exists(vbsPath) && File.ReadAllBytes(vbsPath).All(b => b < 0x80);
        Add("D5 a11y_say.vbs 纯 ASCII", vbsAscii);

        if (readErrors.Count > 0)
        {
            Add("D6 内容文件可读", false, string.Join(", ", readErrors));
        }

        return new LinterResult(checks);

        string Text(string name) =>
            fileTexts.TryGetValue(name, out var value)
                ? value
                : RenpySupport.ReadTextOrNull(Path.Combine(root, "game", "a11y_platform", name)) ?? string.Empty;
    }

    private static (bool Ok, string Detail) CheckManifest(string root, string manifestPath, string? expectedGameRoot)
    {
        if (!File.Exists(manifestPath))
        {
            return (false, "缺少 a11y_manifest.json");
        }

        var text = RenpySupport.ReadTextOrNull(manifestPath);
        if (text is null)
        {
            return (false, "清单不可读");
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var rootElement = document.RootElement;
            var schema = rootElement.TryGetProperty("schema", out var s) ? s.GetString() : null;
            if (!string.Equals(schema, "gala11y.renpy.overlay/1", StringComparison.Ordinal))
            {
                return (false, $"schema 不符: {schema ?? "(缺失)"}");
            }

            if (expectedGameRoot is not null
                && rootElement.TryGetProperty("game", out var game)
                && game.TryGetProperty("root", out var gameRoot)
                && gameRoot.ValueKind == JsonValueKind.String
                && !string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot.GetString()!)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedGameRoot)),
                    StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"清单属于其他游戏: {gameRoot.GetString()}");
            }

            if (!rootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                return (false, "files 缺失或不是数组");
            }

            var count = 0;
            var bad = new List<string>();
            foreach (var item in files.EnumerateArray())
            {
                count++;
                if (!item.TryGetProperty("path", out var pathElement)
                    || pathElement.ValueKind != JsonValueKind.String)
                {
                    bad.Add($"files[{count}].path 缺失");
                    continue;
                }

                var relative = pathElement.GetString() ?? string.Empty;
                var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full))
                {
                    bad.Add($"{relative}:缺文件");
                    continue;
                }

                var bytes = File.ReadAllBytes(full);
                var actual = RenpySupport.Sha256Hex(bytes);
                var expectedHash = item.TryGetProperty("sha256", out var hash) ? hash.GetString() : null;
                if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    bad.Add($"{relative}:哈希不符");
                    continue;
                }

                if (item.TryGetProperty("bytes", out var size)
                    && size.ValueKind == JsonValueKind.Number
                    && size.TryGetInt64(out var expectedSize)
                    && expectedSize != bytes.LongLength)
                {
                    bad.Add($"{relative}:大小不符");
                }
            }

            if (count != RenpyOverlayCatalog.ContentFiles.Length)
            {
                bad.Add($"文件数 {count}，期望 {RenpyOverlayCatalog.ContentFiles.Length}");
            }

            return (bad.Count == 0, $"文件数={count}" + (bad.Count == 0 ? string.Empty : "；" + string.Join("; ", bad.Take(3))));
        }
        catch (JsonException ex)
        {
            return (false, $"清单不是合法 JSON: {ex.Message}");
        }
    }

    private static (bool Ok, string Detail) CheckDeployPlan(string deployPath)
    {
        if (!File.Exists(deployPath))
        {
            return (false, "缺少 deploy_plan.json");
        }

        var text = RenpySupport.ReadTextOrNull(deployPath);
        if (text is null)
        {
            return (false, "部署计划不可读");
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var schema = root.TryGetProperty("schema", out var s) ? s.GetString() : null;
            if (!string.Equals(schema, "gala11y.renpy.deploy_plan/1", StringComparison.Ordinal))
            {
                return (false, $"schema 不符: {schema ?? "(缺失)"}");
            }

            var guard = root.TryGetProperty("guard", out var g) ? g.GetString() : null;
            if (string.IsNullOrWhiteSpace(guard))
            {
                return (false, "guard 为空");
            }

            if (!root.TryGetProperty("steps", out var steps)
                || steps.ValueKind != JsonValueKind.Array
                || steps.GetArrayLength() == 0)
            {
                return (false, "steps 缺失或为空");
            }

            var copies = 0;
            var hasMkdir = false;
            foreach (var step in steps.EnumerateArray())
            {
                var op = step.TryGetProperty("op", out var o) ? o.GetString() : null;
                if (op == "mkdir"
                    && step.TryGetProperty("path", out var p)
                    && string.Equals(p.GetString(), RenpyOverlayCatalog.PlatformDirectory, StringComparison.Ordinal))
                {
                    hasMkdir = true;
                }

                if (op == "copy"
                    && step.TryGetProperty("target", out _)
                    && step.TryGetProperty("sha256", out var hash)
                    && !string.IsNullOrWhiteSpace(hash.GetString()))
                {
                    copies++;
                }
            }

            var ok = hasMkdir && copies >= RenpyOverlayCatalog.ContentFiles.Length;
            return (ok, $"copy={copies}, mkdir={(hasMkdir ? "有" : "缺")}");
        }
        catch (JsonException ex)
        {
            return (false, $"部署计划不是合法 JSON: {ex.Message}");
        }
    }

    private static List<(int Line, string Code)> ExtractPythonBlocks(string[] lines)
    {
        var blocks = new List<(int, string)>();
        var index = 0;
        while (index < lines.Length)
        {
            if (!PythonBlockHeader.IsMatch(lines[index]))
            {
                index++;
                continue;
            }

            var header = lines[index];
            var baseIndent = header.Length - header.TrimStart().Length;
            var body = new List<string>();
            var next = index + 1;
            while (next < lines.Length)
            {
                var line = lines[next];
                if (string.IsNullOrWhiteSpace(line))
                {
                    body.Add(string.Empty);
                    next++;
                    continue;
                }

                var indent = line.Length - line.TrimStart().Length;
                if (indent <= baseIndent)
                {
                    break;
                }

                body.Add(line);
                next++;
            }

            var dedent = body
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Length - l.TrimStart().Length)
                .DefaultIfEmpty(baseIndent + 4)
                .Min();

            var code = string.Join("\n", body.Select(l =>
                l.Length >= dedent ? l[dedent..] : l.TrimStart()));

            blocks.Add((index + 1, code));
            index = next;
        }

        return blocks;
    }

    /// <summary>
    /// 结构自检：制表符、括号/引号/三引号配平。返回 null 表示通过。
    /// 注意：这不是 CPython 编译，只做离线可重复的结构体检；真正的语法保证仍靠 Ren'Py 启动。
    /// </summary>
    private static string? CheckPythonBlockStructure(string code)
    {
        if (code.Contains('\t'))
        {
            return "包含制表符（Python 块要求空格缩进）";
        }

        var bracketError = CheckBrackets(code, out var tripleOpen);
        if (bracketError is not null)
        {
            return bracketError;
        }

        if (tripleOpen)
        {
            return "三引号字符串未闭合";
        }

        return null;
    }

    private static string? CheckBrackets(string code, out bool tripleOpen)
    {
        tripleOpen = false;
        var stack = new Stack<(char Close, int Position)>();
        var i = 0;
        while (i < code.Length)
        {
            var c = code[i];
            if (c == '#')
            {
                while (i < code.Length && code[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                var quote = c;
                if (i + 2 < code.Length && code[i + 1] == quote && code[i + 2] == quote)
                {
                    var close = code.IndexOf(new string(quote, 3), i + 3, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        tripleOpen = true;
                        return "三引号字符串未闭合";
                    }

                    i = close + 3;
                    continue;
                }

                i++;
                var closed = false;
                while (i < code.Length)
                {
                    if (code[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (code[i] == quote)
                    {
                        closed = true;
                        i++;
                        break;
                    }

                    if (code[i] == '\n')
                    {
                        return "单行字符串跨行未闭合";
                    }

                    i++;
                }

                if (!closed)
                {
                    return "字符串未闭合";
                }

                continue;
            }

            if (c is '(' or '[' or '{')
            {
                stack.Push((c switch { '(' => ')', '[' => ']', _ => '}' }, i));
                i++;
                continue;
            }

            if (c is ')' or ']' or '}')
            {
                if (stack.Count == 0)
                {
                    return $"多余的右括号 '{c}'";
                }

                var expected = stack.Pop();
                if (expected.Close != c)
                {
                    return $"括号不匹配：期望 '{expected.Close}'，实际 '{c}'";
                }
            }

            i++;
        }

        if (stack.Count > 0)
        {
            var top = stack.Peek();
            var line = code[..top.Position].Count(ch => ch == '\n') + 1;
            return $"括号未闭合 '{top.Close}'（第 {line} 行）";
        }

        return null;
    }

    internal sealed record LinterResult(IReadOnlyList<VerificationCheck> Checks)
    {
        public bool Passed => Checks.Count > 0 && Checks.All(c => c.Passed);
    }
}
