using System.Globalization;
using GalA11yForge.Core.Abstractions;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Adapters.UnityMono.Internal;

/// <summary>
/// 只读分析：定位 Unity Mono 程序集 → Cecil 扫描 → 规则打分 → AnalysisReport。
/// 输出确定：已知入口优先，其后按 (score, S9, typewriter, sink, fullName) 排序，Id 按最终顺序生成。
/// </summary>
internal sealed class UnityMonoAnalyzer : IEngineAnalyzer
{
    public const int MaxCandidates = 50;
    public const double DefaultMinConfidence = 0.30;

    private readonly UnityMonoScanner _scanner;

    public UnityMonoAnalyzer()
        : this(new UnityMonoScanner())
    {
    }

    public UnityMonoAnalyzer(UnityMonoScanner scanner)
    {
        _scanner = scanner;
    }

    public Task<ForgeResult<AnalysisReport>> AnalyzeAsync(
        GameContext context,
        EngineDetection detection,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(Analyze(context, detection, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(ForgeResult<AnalysisReport>.Failure(
                ForgeErrorCodes.Internal,
                "分析已取消。",
                "重试，或缩小扫描范围。"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ForgeResult<AnalysisReport>.Failure(
                ForgeErrorCodes.Internal,
                $"Unity Mono 分析失败：{ex.Message}",
                "保留游戏目录不变，把该消息和 analysis 日志交给开发者。"));
        }
    }

    private ForgeResult<AnalysisReport> Analyze(
        GameContext context,
        EngineDetection detection,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            return ForgeResult<AnalysisReport>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                "GameContext 不能为空。");
        }

        if (detection is null)
        {
            return ForgeResult<AnalysisReport>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                "EngineDetection 不能为空。",
                "先运行 detect，或由 CLI 传入 unity_mono 的检测结果。");
        }

        if (!string.Equals(detection.Family, UnityMonoAdapter.EngineId, StringComparison.OrdinalIgnoreCase))
        {
            return ForgeResult<AnalysisReport>.Failure(
                ForgeErrorCodes.EngineUnsupported,
                $"该适配器只处理 unity_mono，当前检测结果为 {detection.Family}。",
                "改用对应引擎的适配器；或人工确认引擎后重跑 detect。");
        }

        var layoutResult = UnityMonoDiscovery.Locate(context);
        if (!layoutResult.Ok)
        {
            return ForgeResult<AnalysisReport>.Failure(layoutResult.Error!);
        }

        var layout = layoutResult.Value!;
        var language = UnityMonoLanguage.Resolve(context.Profile);
        var minConfidence = NormalizeMinConfidence(context.Profile?.MinConfidence);

        var scanResult = _scanner.Scan(
            layout,
            language.Code,
            language.Source,
            minConfidence,
            cancellationToken);
        if (!scanResult.Ok)
        {
            return ForgeResult<AnalysisReport>.Failure(scanResult.Error!);
        }

        var outcome = scanResult.Value!;
        var threshold = Math.Max(30.0, minConfidence * 100.0);
        var scored = new List<UnityMonoScore>();
        foreach (var assembly in outcome.Assemblies)
        {
            foreach (var facts in assembly.Methods)
            {
                var score = UnityMonoScoring.Evaluate(facts);
                if (score is null || score.Score < threshold)
                {
                    continue;
                }

                scored.Add(score);
            }
        }

        scored.Sort(CompareScores);
        if (scored.Count > MaxCandidates)
        {
            scored.RemoveRange(MaxCandidates, scored.Count - MaxCandidates);
        }

        var candidates = new List<TextCandidate>(scored.Count);
        for (var i = 0; i < scored.Count; i++)
        {
            var score = scored[i];
            candidates.Add(ToCandidate(score, i + 1, layout, language));
        }

        var report = new AnalysisReport
        {
            GamePath = layout.GameDirectory,
            Detection = detection,
            Candidates = candidates,
            Coverage = BuildCoverage(outcome, language, scored),
            Risks = BuildRisks(outcome, language),
            Unverified = BuildUnverified(outcome, language),
        };

        if (language.Requested is not null && !language.Recognized)
        {
            report.Unverified.Add(
                $"profile 配置 language=\"{language.Requested}\" 不在 sc|tc|ja|en 内，已按默认 sc 分析；请确认配置。");
        }

        if (candidates.Count == 0)
        {
            report.Unverified.Add(
                "未找到达到阈值的文本候选；可能程序集被加密/裁剪，或该游戏的文本管线不在已覆盖规则内。");
        }

        return ForgeResult<AnalysisReport>.Success(report);
    }

    private static double NormalizeMinConfidence(double? configured)
    {
        var value = configured ?? DefaultMinConfidence;
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return DefaultMinConfidence;
        }

        if (value > 1 && value <= 100)
        {
            value /= 100;
        }

        return Math.Clamp(value, 0, 1);
    }

    private static int CompareScores(UnityMonoScore left, UnityMonoScore right)
    {
        // 运行期已实证的常见入口（见 UnityMonoTargets.HookPriority）先于普通候选展示；
        // 否则真实游戏里大量 TalkEnd* 方法会把它们挤出 50 条上限。
        var leftPriority = UnityMonoTargets.PriorityOf(left.Facts.MethodName);
        var rightPriority = UnityMonoTargets.PriorityOf(right.Facts.MethodName);
        var result = leftPriority.CompareTo(rightPriority);
        if (result != 0)
        {
            return result;
        }

        result = right.Score.CompareTo(left.Score);
        if (result != 0)
        {
            return result;
        }

        result = right.S9.CompareTo(left.S9);
        if (result != 0)
        {
            return result;
        }

        result = right.Facts.TypewriterSinkCalls.Count.CompareTo(left.Facts.TypewriterSinkCalls.Count);
        if (result != 0)
        {
            return result;
        }

        result = right.Facts.TextSinkCalls.Count.CompareTo(left.Facts.TextSinkCalls.Count);
        if (result != 0)
        {
            return result;
        }

        return string.CompareOrdinal(left.Facts.FullName, right.Facts.FullName);
    }

    private static TextCandidate ToCandidate(
        UnityMonoScore score,
        int index,
        UnityMonoGameLayout layout,
        UnityMonoLanguage language)
    {
        var facts = score.Facts;
        var evidence = new List<string>
        {
            $"签名：[{facts.AssemblyFile}] {facts.Signature}",
            score.RuleSummary,
        };
        evidence.AddRange(score.Evidence);

        if (facts.CallerCount > 0)
        {
            evidence.Add(
                $"程序集内调用者 {facts.CallerCount} 个：{string.Join("，", facts.CallerSamples)}" +
                (facts.AnyCallerChoiceLike ? "（含 choice 调用者）" : string.Empty) + "。");
        }

        if (facts.TextFieldCount > 0)
        {
            evidence.Add($"类型文本字段：{string.Join("/", facts.TextFieldNames)}。");
        }

        evidence.Add(
            $"IL 统计：ldstr={facts.LdstrCount}，CJK 字面量方法={facts.CjkLiteralCount}，" +
            $"非台词候选={facts.NonDialogueLiteralCount}，指令数={facts.InstructionCount}（不保存台词原文）。");
        evidence.Add($"language={language.Code}（source={language.Source}）；language 只影响报告标注与朗读配置，静态规则不改写文本。");

        return new TextCandidate
        {
            Id = "tc-" + index.ToString("D3", CultureInfo.InvariantCulture),
            Kind = score.Kind,
            Source = BuildSource(facts),
            Score = score.Score,
            Evidence = evidence,
        };
    }

    private static string BuildSource(UnityMonoMethodFacts facts) =>
        $"assembly:{facts.AssemblyFile}!/{facts.TypeFullName}::{facts.MethodName}";

    private static CoverageReport BuildCoverage(
        UnityMonoScanOutcome outcome,
        UnityMonoLanguage language,
        IReadOnlyList<UnityMonoScore> scored)
    {
        var covered = new List<string>();
        var notCovered = new List<string>();
        var manual = new List<string>();

        var assemblies = outcome.Assemblies;
        var types = assemblies.Sum(a => a.TypesScanned);
        var methods = assemblies.Sum(a => a.MethodsScanned);
        var bodies = assemblies.Sum(a => a.MethodsWithBody);
        var sinkApis = assemblies.SelectMany(a => a.SinkApiCounts.Keys).Distinct(StringComparer.Ordinal).Count();
        var typewriterApis = assemblies.SelectMany(a => a.TypewriterApiCounts.Keys).Distinct(StringComparer.Ordinal).Count();

        covered.Add($"只读扫描 {assemblies.Count} 个程序集、{types} 个类型、{methods} 个方法（有 IL 体 {bodies} 个）。");
        covered.Add($"主程序集：{outcome.Layout.PrimaryAssemblyFile}；附加程序集 {outcome.Layout.AdditionalAssemblyPaths.Count} 个（Plugins/firstpass）。");
        covered.Add($"文本 sink API {sinkApis} 种、打字机/Tween sink API {typewriterApis} 种；S9 规则（string 参数 + 打字机 sink）已启用。");
        covered.Add($"语言配置：{language.Code}（来源 {language.Source}）；未知键不报错。");

        if (assemblies.Any(a => a.Truncated))
        {
            covered.Add("注意：至少一个程序集触发了类型/方法/指令上限，结果可能不完整（见 risks）。");
        }

        notCovered.Add("运行期调用顺序、参数是否为空、选项回显延迟：静态分析无法确认，需 manual 模式实测。");
        notCovered.Add("IL2CPP、加壳/加密/裁剪后的程序集：本适配器没有绕过方案。");
        notCovered.Add("Unity 资源包内的文本（TextAsset/ScriptableObject/本地化表）：当前只扫程序集 IL 与字段类型。");
        notCovered.Add("多语言资源的分层（日文原稿 vs 简中运行时文本）：只按 profile/language 标注，未做跨层配对。");
        notCovered.Add("Plugins/ 之外的第三方运行时插件（BepInEx 插件、原生 DLL）：未分析。");

        // 0.2.49：不再写别款游戏的入口名与实测秒数——按本次实际候选动态生成。
        // 起因：用户给《Mystery Lover 2》（Utage）装补丁后没有剧情朗读，
        // 报告里却还写着 DOText/AnimateText 和“67.7/416.2 秒”，像是在说另一款游戏。
        var knownEntries = scored
            .Where(score => IsKnownTextEntry(score.Facts))
            .Select(score => score.Facts.MethodName)
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToList();

        if (knownEntries.Count > 0)
        {
            manual.Add($"用 manual 模式跑一遍游戏，确认这些入口在剧情里真的被调用：{string.Join("、", knownEntries)}。");
        }
        else
        {
            manual.Add("本游戏没有识别到已知正文入口：用 manual 模式跑一遍，确认候选入口是否真的在剧情里被调用，否则需要人工补入口。");
        }

        manual.Add("确认选项回显事件与时机：静态分析给不出秒数，必要时在插件里加 choice_echo 去重窗口。");
        manual.Add("确认 language 层：同一段文本若有原稿层与运行时层，确认用哪一层做 hash 与朗读。");
        manual.Add("若 Harmony 报 AmbiguousMatchException，按完整签名补参数类型或只保留一个重载。");

        return new CoverageReport
        {
            Covered = covered,
            NotCovered = notCovered,
            ManualTodo = manual,
        };
    }

    private static bool IsKnownTextEntry(UnityMonoMethodFacts facts) =>
        UnityMonoTargets.IsKnownHookMethod(facts.MethodName)
        || MonoTextPatterns.TryUtagePropertyChain(facts.TypeFullName, facts.MethodName, out _);

    private static List<RiskItem> BuildRisks(UnityMonoScanOutcome outcome, UnityMonoLanguage language)
    {
        var risks = new List<RiskItem>
        {
            new()
            {
                Id = "R-001",
                Severity = RiskSeverity.High,
                Description = "静态分析不能证明运行期文本顺序/参数非空；候选入口需要 manual 模式实测。",
                Mitigation = "按 docs/14 默认 manual 模式先跑一局，再切换自动朗读。",
            },
            new()
            {
                Id = "R-002",
                Severity = RiskSeverity.Medium,
                Description = "S9 与类型级 S1 是启发式规则：依赖“string 参数 + 打字机 sink”和同类型 TMP 字段，可能漏掉无 Tween 的打字机实现。",
                Mitigation = "保留未验证清单；用运行期 hook 命中率复核，必要时人工补签名。",
            },
            new()
            {
                Id = "R-003",
                Severity = RiskSeverity.Medium,
                Description = "生成器 HookTargetParser 只接受基础类型参数，plan 的 Target 省略了 TMPro/自定义结构参数；同名重载可能歧义。",
                Mitigation = "Target 用 Type:Method，完整签名写在 Description/Evidence；运行期报歧义时人工指定重载。",
            },
            new()
            {
                Id = "R-004",
                Severity = RiskSeverity.Low,
                Description = "附加程序集读取失败只会跳过并记入 notes，不会让整次分析失败。",
                Mitigation = "查看 coverage/unverified 与 notes，确认目标文本入口是否在跳过的程序集里。",
            },
        };

        if (language.Requested is not null && !language.Recognized)
        {
            risks.Add(new RiskItem
            {
                Id = "R-005",
                Severity = RiskSeverity.Low,
                Description = $"language=\"{language.Requested}\" 未识别，已回退 sc。",
                Mitigation = "改为 sc|tc|ja|en 之一后重跑。",
            });
        }

        if (outcome.Assemblies.Any(a => a.Truncated))
        {
            risks.Add(new RiskItem
            {
                Id = "R-006",
                Severity = RiskSeverity.High,
                Description = "扫描触发类型/方法/指令上限，结果被截断，可能漏掉候选。",
                Mitigation = "缩小到单个程序集或人工指定入口；当前上限见 README。",
            });
        }

        return risks;
    }

    private static List<string> BuildUnverified(UnityMonoScanOutcome outcome, UnityMonoLanguage language)
    {
        var list = new List<string>
        {
            "运行期未实测（本适配器只在只读静态分析阶段；manual/auto 模式由后续运行期步骤验证）。",
            "选项 Choice 的文本来源与回显时机未由 IL 证明。",
            "文本入口的 string 参数是否与显示层一致（多语言/双层文本时），未做跨层配对。",
            "Harmony Postfix 的参数名/类型/顺序需与实际运行程序集一致；生成器会按证据为含字符串参数的钩子接线朗读，但仍以运行期日志 [Speech] 为准。",
        };

        if (!language.WasConfigured)
        {
            list.Add("profile 未显式配置 language，按 docs/14 默认 sc 分析；如需 tc/ja/en 请显式配置。");
        }

        if (outcome.Layout.AdditionalAssemblyPaths.Count > 0)
        {
            list.Add("附加程序集只做同一套规则扫描；未确认它们是否随游戏实际加载。");
        }

        return list;
    }
}
