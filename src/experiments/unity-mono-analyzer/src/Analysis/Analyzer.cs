using System.Diagnostics;
using UnityMonoAnalyzer.Discovery;

namespace UnityMonoAnalyzer.Analysis;

public sealed class AnalyzeRequest
{
    public string GameInput { get; init; } = "";
    public string? ExeOverride { get; init; }
    public int TopN { get; init; } = 20;
    public string CommandLine { get; init; } = "";
    public int MaxQuickScanCandidates { get; init; } = 12;
}

/// <summary>提前失败的场景（参数错 / 不是 Unity Mono），带退出码。</summary>
public sealed class AnalyzeException : Exception
{
    public int ExitCode { get; }
    public AnalyzeException(int exitCode, string message) : base(message) => ExitCode = exitCode;
}

/// <summary>Discovery → 快速指标 → 排名 → 全量扫描 → 打分 → AnalysisResult。</summary>
public static class Analyzer
{
    public static AnalysisResult Run(AnalyzeRequest req)
    {
        var total = Stopwatch.StartNew();
        var result = new AnalysisResult();
        result.Run.StartedAtUtc = DateTime.UtcNow.ToString("o");
        result.Run.CommandLine = req.CommandLine;
        result.Game.InputPath = req.GameInput;
        string gameName = "";

        // ---------- 1. 输入解析 ----------
        var tDisc = Stopwatch.StartNew();
        var input = req.GameInput?.Trim() ?? "";
        if (input.Length == 0) throw new AnalyzeException(2, "--game 不能为空");
        string root;
        string inputKind = "directory";
        DiscoveryCandidate? forced = null;
        string? exeOverride = req.ExeOverride;

        if (File.Exists(input))
        {
            var ext = Path.GetExtension(input).ToLowerInvariant();
            if (ext == ".exe")
            {
                inputKind = "exe";
                exeOverride ??= input;
                root = Path.GetDirectoryName(Path.GetFullPath(input))!;
            }
            else if (ext == ".dll")
            {
                inputKind = "assembly";
                forced = CandidateFromAssembly(input);
                root = forced.GameDir;
            }
            else throw new AnalyzeException(2, $"--game 指向不支持的文件类型：{input}");
        }
        else if (Directory.Exists(input))
        {
            root = Path.GetFullPath(input);
        }
        else throw new AnalyzeException(2, $"路径不存在：{input}");

        result.Discovery.InputKind = inputKind;
        gameName = new DirectoryInfo(root).Name;

        if (!string.IsNullOrWhiteSpace(exeOverride))
        {
            var exe = Path.IsPathRooted(exeOverride) ? exeOverride : Path.Combine(root, exeOverride);
            if (!File.Exists(exe)) throw new AnalyzeException(2, $"--exe 指定的文件不存在：{exe}");
            var dataDir = EntrySelector.DataDirForExe(exe);
            if (!Directory.Exists(dataDir))
                throw new AnalyzeException(2, $"--exe 反查数据目录失败（期望 {Path.GetFileName(dataDir)} 与 exe 同目录）：{dataDir}");
            var asm = Path.Combine(dataDir, GameDiscovery.AssemblyRelativePath);
            if (!File.Exists(asm))
                throw new AnalyzeException(4, $"--exe 对应的数据目录里没有 Managed\\Assembly-CSharp.dll：{asm}（可能是 IL2CPP 或不是 Unity 游戏）");
            forced = new DiscoveryCandidate
            {
                DataDir = Path.GetFullPath(dataDir),
                DataDirName = Path.GetFileName(dataDir),
                GameDir = Path.GetFullPath(Path.GetDirectoryName(dataDir)!),
                AssemblyPath = asm,
                AssemblySize = AssemblyScanner.SafeLength(asm),
                ExePath = exe,
                ExeReason = "--exe 显式指定",
                Depth = 0,
            };
        }

        // ---------- 2. 发现候选 ----------
        List<DiscoveryCandidate> candidates;
        if (forced != null)
        {
            candidates = new List<DiscoveryCandidate> { forced };
        }
        else
        {
            candidates = GameDiscovery.Find(root);
            if (candidates.Count == 0)
            {
                var il2cpp = FindIl2Cpp(root);
                if (il2cpp != null)
                    throw new AnalyzeException(4,
                        $"检出 Unity IL2CPP 目标（{il2cpp}）：W2-B 只支持 Mono（Assembly-CSharp.dll），IL2CPP 需要另一套分析路径");
                throw new AnalyzeException(4,
                    $"不是 Unity Mono 游戏：在 {root} 下未找到 *_Data\\Managed\\Assembly-CSharp.dll（查找深度 3 层）");
            }
        }
        tDisc.Stop();
        result.Timing.DiscoveryMs = tDisc.ElapsedMilliseconds;

        // ---------- 3. 快速指标（用于排名）----------
        var tQuick = Stopwatch.StartNew();
        var quickTargets = candidates.Take(req.MaxQuickScanCandidates).ToList();
        if (candidates.Count > quickTargets.Count)
            result.Discovery.Notes.Add($"候选数据目录 {candidates.Count} 个，只对前 {quickTargets.Count} 个做快速扫描（按路径序）");
        foreach (var c in quickTargets)
        {
            var cw = Stopwatch.StartNew();
            try
            {
                var quickScan = AssemblyScanner.Scan(c.AssemblyPath, new ScanOptions { Quick = true });
                c.QuickMetrics = quickScan.Quick;
                result.Timing.Candidates.Add(new CandidateTiming { DataDirName = c.DataDirName, Ms = cw.ElapsedMilliseconds });
            }
            catch (Exception ex)
            {
                c.ScanError = ex.GetType().Name + ": " + ex.Message;
                result.Timing.Candidates.Add(new CandidateTiming { DataDirName = c.DataDirName, Ms = cw.ElapsedMilliseconds });
            }
        }
        tQuick.Stop();
        result.Timing.CandidateScanMs = tQuick.ElapsedMilliseconds;

        // ---------- 4. 排名/选择 ----------
        DiscoveryCandidate selected;
        var rootName = new DirectoryInfo(root).Name;
        if (forced != null)
        {
            selected = forced;
            result.Discovery.SelectionMode = inputKind == "assembly" ? "explicit-assembly" : "explicit";
            result.Discovery.Candidates.Add(forced);
        }
        else if (candidates.Count == 1)
        {
            selected = candidates[0];
            result.Discovery.SelectionMode = "single";
            result.Discovery.Candidates.Add(selected);
        }
        else
        {
            var ranked = CandidateRanker.Rank(candidates, rootName);
            result.Discovery.SelectionMode = "ranked";
            foreach (var r in ranked.OrderByDescending(x => x.Score))
            {
                r.Candidate.RankScore = r.Score;
                r.Candidate.RankReason = r.Reason;
                result.Discovery.Candidates.Add(r.Candidate);
            }
            selected = ranked[0].Candidate;
            if (CandidateRanker.IsAmbiguous(ranked))
            {
                result.Warnings.Add(
                    $"多个 Unity 程序集排名并列（{ranked[0].Candidate.DataDirName} 与 {ranked[1].Candidate.DataDirName} 同分），" +
                    "已按目录名/深度取第一个，请人工确认或用 --exe 指定");
                result.Discovery.Notes.Add("并列排名 → 部分成功（退出码 5）");
            }
            if (ranked[0].Candidate.ScanError.Length > 0)
                result.Warnings.Add($"首选数据目录扫描失败：{ranked[0].Candidate.ScanError}");
        }
        selected.Selected = true;

        // ---------- 5. 引擎/版本/部署环境 ----------
        var unity = UnityVersionDetector.Detect(selected.DataDir, selected.GameDir);
        result.Unity = new UnityInfo
        {
            Version = unity.Version,
            VersionSource = unity.Source,
            GlobalGameManagersPath = unity.EvidencePath,
            UnityPlayerDllPresent = File.Exists(Path.Combine(selected.GameDir, "UnityPlayer.dll")),
        };
        var (il2cppPresent, il2cppEv) = UnityVersionDetector.DetectIl2Cpp(selected.GameDir, selected.DataDir);
        result.Unity.Il2CppMarkersPresent = il2cppPresent;
        result.Unity.Il2CppEvidence = il2cppEv;
        if (string.IsNullOrEmpty(unity.Version))
            result.Warnings.Add("未能识别 Unity 版本（globalgamemanagers 与 UnityPlayer.dll 都无版本串）");

        var deployed = GameDiscovery.DetectDeployed(selected.GameDir);
        result.Game = new GameInfo
        {
            Name = gameName,
            InputPath = result.Game.InputPath,
            GameDir = selected.GameDir,
            DataDir = selected.DataDir,
            DataDirName = selected.DataDirName,
            ExePath = selected.ExePath,
            ExeReason = selected.ExeReason,
            Deployed = deployed.Deployed,
            DeployedEvidence = deployed.Evidence,
        };
        if (deployed.Deployed)
            result.Notes.Add($"该游戏目录已存在补丁/注入痕迹（{string.Join(", ", deployed.Evidence)}），只做静态只读分析，未改动任何文件");

        // ---------- 6. 全量扫描 ----------
        var tFull = Stopwatch.StartNew();
        ScanResult scan;
        try
        {
            scan = AssemblyScanner.Scan(selected.AssemblyPath, new ScanOptions());
        }
        catch (Exception ex)
        {
            throw new AnalyzeException(4, $"读取程序集失败：{Path.GetFileName(selected.AssemblyPath)} → {ex.GetType().Name}: {ex.Message}");
        }
        tFull.Stop();
        result.Timing.FullScanMs = tFull.ElapsedMilliseconds;

        result.Assembly = scan.Assembly;
        result.Stats = new ScanStats
        {
            Types = scan.Types,
            Methods = scan.MethodsTotal,
            MethodsWithBody = scan.MethodsWithBody,
            LdstrTotal = scan.LdstrTotal,
            CjkLiteralMethods = scan.CjkLiteralMethods,
            CjkGame = scan.CjkGame,
            TextFieldTypes = scan.TextFieldTypes,
            TextSinkCallSites = scan.SinkCallSiteCount,
            TextSinkCallerMethods = scan.SinkCallerMethods,
            UpstreamCandidateMethods = scan.UpstreamCandidateMethods,
            ResolveFailures = scan.ResolveFailures,
        };
        result.TextSinks = scan.Sinks;
        result.TextSinkHotspots = scan.Hotspots;
        result.FrameworkTraces = scan.Frameworks;

        if (scan.ResolveFailures.Count > 0)
            result.Warnings.Add($"有 {scan.ResolveFailures.Count} 个引用程序集未解析：{string.Join(", ", scan.ResolveFailures.Take(6))}（继承链判断可能不完整）");
        if (scan.SinkCallSiteCount == 0)
            result.Warnings.Add("未发现任何 TMP_Text/UI.Text 文本写入点：可能是纯自定义文本管线，需要运行期探针或换引擎方案");

        // ---------- 7. 打分 ----------
        var tScore = Stopwatch.StartNew();
        var scored = new List<TextCandidate>();
        foreach (var rec in scan.Candidates)
        {
            var f = ToFeatures(rec, scan.CjkGame);
            var s = Scoring.Score(f);
            scored.Add(ToCandidate(rec, f, s));
        }

        var ordered = scored
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.TextSinkCalls)
            .ThenBy(c => c.MethodFullName, StringComparer.Ordinal)
            .ToList();
        for (int i = 0; i < ordered.Count; i++) ordered[i].Rank = i + 1;

        result.Stats.CandidatesScored = ordered.Count;
        result.Stats.TierA = ordered.Count(c => c.Tier == "A");
        result.Stats.TierB = ordered.Count(c => c.Tier == "B");
        result.Stats.TierC = ordered.Count(c => c.Tier == "C");
        result.Stats.BelowThreshold = ordered.Count(c => c.Tier == "excluded");
        result.Candidates = ordered.Where(c => c.Tier != "excluded").ToList();
        result.Stats.CandidatesIncluded = result.Candidates.Count;
        tScore.Stop();
        result.Timing.ScoringMs = tScore.ElapsedMilliseconds;

        if (result.Candidates.Count == 0)
            result.Warnings.Add("没有候选达到 C 级阈值（30 分）：本作文本管线可能不在 Assembly-CSharp 的常规 set_text 路径上");

        result.Coverage = Reporting.Coverage.Build(result.Stats, scan.CjkGame, scan.UpstreamCandidateMethods);
        string sinkSummary = scan.Sinks.Where(s => s.Role == "primary").Select(s => $"{s.Api}×{s.Calls}").ToList() is { Count: > 0 } sl
            ? string.Join(", ", sl)
            : "无";
        result.Notes.Add($"未解析程序集：{(scan.ResolveFailures.Count == 0 ? "0" : scan.ResolveFailures.Count.ToString())}；主文本写入 API：{sinkSummary}");

        result.Timing.TotalMs = total.ElapsedMilliseconds;
        result.ExitCode = result.Warnings.Count > 0 ? 5 : 0;
        return result;
    }

    private static DiscoveryCandidate CandidateFromAssembly(string dll)
    {
        var managed = Path.GetDirectoryName(Path.GetFullPath(dll))!;
        var dataDir = Path.GetDirectoryName(managed)!;
        var gameDir = Path.GetDirectoryName(dataDir)!;
        var dataDirName = Path.GetFileName(dataDir);
        var exe = EntrySelector.Pick(gameDir, dataDirName);
        return new DiscoveryCandidate
        {
            DataDir = dataDir,
            DataDirName = dataDirName,
            GameDir = gameDir,
            AssemblyPath = Path.GetFullPath(dll),
            AssemblySize = AssemblyScanner.SafeLength(dll),
            ExePath = exe.Path ?? "",
            ExeReason = exe.Reason,
            Depth = 0,
        };
    }

    private static string? FindIl2Cpp(string root)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "GameAssembly.dll", SearchOption.AllDirectories))
                return f;
        }
        catch { }
        try
        {
            foreach (var d in Directory.EnumerateDirectories(root, "il2cpp_data", SearchOption.AllDirectories))
                return d;
        }
        catch { }
        return null;
    }

    public static CandidateFeatures ToFeatures(MethodRecord rec, bool gameHasCjk)
    {
        var sites = rec.SinkSites;
        int constant = sites.Count(s => s.Source == "constant");
        int dynamic_ = sites.Count(s => s.Source is "parameter" or "field" or "local" or "call");
        int maxConst = sites.Where(s => s.Source == "constant").Select(s => s.ConstantLength).DefaultIfEmpty(0).Max();

        bool viaCaller = false;
        foreach (var key in rec.CallerSamples)
        {
            var idx = key.LastIndexOf("::", StringComparison.Ordinal);
            if (idx <= 0) continue;
            var typePart = key[..idx];
            if (TextPatterns.MatchKnownFramework(typePart) != null || TextPatterns.HasCustomDialogueTrace(typePart))
            {
                viaCaller = true;
                break;
            }
        }

        return new CandidateFeatures
        {
            TypeFullName = rec.TypeFullName,
            TypeName = rec.TypeName,
            MethodName = rec.MethodName,
            MethodFullName = rec.MethodFullName,
            ParameterCount = rec.ParameterCount,
            HasStringParameter = rec.HasStringParameter,
            FrameworkId = rec.FrameworkId,
            OnKnownFrameworkType = rec.FrameworkId.Length > 0,
            OnCustomDialogueType = TextPatterns.HasCustomDialogueTrace(rec.TypeFullName),
            FrameworkHitViaCaller = viaCaller,
            IsDialogueNamedType = rec.IsDialogueNamed,
            TextSinkCalls = sites.Count,
            SinkApis = sites.Select(s => s.Api).Distinct(StringComparer.Ordinal).ToList(),
            CjkLiteralCount = rec.CjkLiteralCount,
            LiteralCount = rec.LdstrCount,
            NonDialogueLiteralCount = rec.NonDialogueLiteralCount,
            ConstantSinkCalls = constant,
            DynamicSinkCalls = dynamic_,
            MaxConstantLength = maxConst,
            TextFieldCount = rec.TextFieldCount,
            CallerCount = rec.CallerCount,
            UpstreamHasChoiceType = rec.AnyCallerChoiceLike,
            AllCallersInitLike = rec.AllCallersInitLike,
            GameHasCjk = gameHasCjk,
            DiscoveredBySink = rec.DiscoveredBySink,
            IsSemanticNamed = rec.IsSemanticNamed,
            IsConstructor = rec.MethodName is ".ctor" or ".cctor",
        };
    }

    public static TextCandidate ToCandidate(MethodRecord rec, CandidateFeatures f, ScoreResult s)
    {
        var sites = rec.SinkSites;
        string valueSource;
        if (sites.Count == 0) valueSource = "none";
        else
        {
            var distinct = sites.Select(x => x.Source).Distinct(StringComparer.Ordinal).ToList();
            valueSource = distinct.Count == 1 ? distinct[0] : "mixed";
        }

        var frameworkCategory = "none";
        if (f.OnKnownFrameworkType) frameworkCategory = "known";
        else if (f.OnCustomDialogueType || f.FrameworkHitViaCaller) frameworkCategory = "custom";

        return new TextCandidate
        {
            Kind = s.Kind,
            Score = s.Total,
            Tier = s.Tier,
            Confidence = s.Confidence,
            MethodFullName = rec.MethodFullName,
            TypeFullName = rec.TypeFullName,
            MethodName = rec.MethodName,
            Signature = rec.Signature,
            DiscoveredBy = rec.DiscoveredBySink ? "direct-text-setter" : "upstream-of-text-setter",
            RuleScores = s.Rules,
            Evidence = s.Evidence,
            TextSinkCalls = sites.Count,
            TextSinkApis = sites.Select(x => x.Api).Distinct(StringComparer.Ordinal).ToList(),
            ValueSource = valueSource,
            CjkLiteralCount = rec.CjkLiteralCount,
            LiteralCount = rec.LdstrCount,
            CallerCount = rec.CallerCount,
            CallerSamples = rec.CallerSamples,
            TypeHoldsTextFields = rec.TextFieldCount > 0,
            TextFieldCount = rec.TextFieldCount,
            Framework = f.OnKnownFrameworkType ? TextPatterns.FrameworkDisplayName(f.FrameworkId)
                : f.OnCustomDialogueType ? "自定义 *Dialog*/*Talk*"
                : f.FrameworkHitViaCaller ? "调用者位于对话框架类型"
                : "",
            FrameworkCategory = frameworkCategory,
            OnKnownFrameworkType = f.OnKnownFrameworkType,
            Flags = ReviewFlags.Build(rec, f),
        };
    }
}
