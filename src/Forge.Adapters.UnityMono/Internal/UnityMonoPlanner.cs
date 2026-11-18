using System.Globalization;
using GalA11yForge.Core.Abstractions;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Adapters.UnityMono.Internal;

/// <summary>
/// 把 AnalysisReport 的候选变成生成器可执行的 PatchPlan。
/// route 固定 automatic_with_confirmation：机器给方案，运行期仍要人工确认。
/// </summary>
internal sealed class UnityMonoPlanner : IPlanner
{
    public const int MaxHookPoints = 6;

    public Task<ForgeResult<PatchPlan>> PlanAsync(
        GameContext context,
        AnalysisReport analysis,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(Plan(context, analysis, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(ForgeResult<PatchPlan>.Failure(
                ForgeErrorCodes.Internal,
                "规划已取消。",
                "重试。"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ForgeResult<PatchPlan>.Failure(
                ForgeErrorCodes.Internal,
                $"Unity Mono 规划失败：{ex.Message}",
                "把该消息和 analysis 报告交给开发者。"));
        }
    }

    private ForgeResult<PatchPlan> Plan(
        GameContext context,
        AnalysisReport analysis,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context is null)
        {
            return ForgeResult<PatchPlan>.Failure(ForgeErrorCodes.ArgumentInvalid, "GameContext 不能为空。");
        }

        if (analysis is null)
        {
            return ForgeResult<PatchPlan>.Failure(ForgeErrorCodes.ArgumentInvalid, "AnalysisReport 不能为空。");
        }

        if (analysis.Detection is null ||
            !string.Equals(analysis.Detection.Family, UnityMonoAdapter.EngineId, StringComparison.OrdinalIgnoreCase))
        {
            return ForgeResult<PatchPlan>.Failure(
                ForgeErrorCodes.EngineUnsupported,
                "分析报告不是 unity_mono 的检测结果，不能生成 Unity Mono 补丁计划。");
        }

        if (analysis.Candidates.Count == 0)
        {
            return ForgeResult<PatchPlan>.Failure(
                ForgeErrorCodes.Partial,
                "分析报告没有文本候选，无法生成钩子计划。",
                "先解决分析阶段的未验证项；或人工指定入口方法。");
        }

        var views = new List<UnityMonoCandidateView>();
        var dialogueCandidateIds = new HashSet<string>(StringComparer.Ordinal);
        var parseIssues = new List<string>();
        foreach (var candidate in analysis.Candidates)
        {
            if (!UnityMonoTargets.TryParseSource(candidate.Source, out var parsed))
            {
                parseIssues.Add($"{candidate.Id}（{candidate.Source}）");
                continue;
            }

            var evidence = candidate.Evidence ?? new List<string>();
            if (candidate.Kind == TextCandidateKind.Dialogue)
            {
                dialogueCandidateIds.Add(candidate.Id);
            }

            var signature = evidence
                .FirstOrDefault(e => e.StartsWith("签名：", StringComparison.Ordinal)) ?? string.Empty;
            views.Add(new UnityMonoCandidateView
            {
                CandidateId = candidate.Id,
                MethodName = parsed!.MethodName,
                TypeFullName = parsed.TypeFullName,
                Signature = signature,
                StringParameter = ExtractStringParameter(signature),
                Score = candidate.Score,
                Confidence = ReadConfidence(evidence, candidate.Score),
                Evidence = evidence,
            });
        }

        views.Sort(CompareViews);
        var selected = new List<UnityMonoCandidateView>(MaxHookPoints);
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);
        // 已经有 3 个以上运行期实证过的入口（AnimateText/DOText/Choice/HandleMessageText/HandleMainText）时，
        // 只勾这些；否则把 PushButton / TalkEnd* 这类噪声也塞进补丁计划，反而让用户误以为覆盖了文本。
        var knownEntryCount = views.Count(v => UnityMonoTargets.PriorityOf(v.MethodName) < 10);
        foreach (var view in views)
        {
            if (knownEntryCount >= 3 && UnityMonoTargets.PriorityOf(view.MethodName) >= 10)
            {
                break;
            }

            var target = UnityMonoTargets.BuildHookTarget(new UnityMonoParsedSource
            {
                AssemblyFile = "Assembly-CSharp.dll",
                TypeFullName = view.TypeFullName,
                MethodName = view.MethodName,
            });
            if (!seenTargets.Add(target))
            {
                continue;
            }

            selected.Add(view);
            if (selected.Count >= MaxHookPoints)
            {
                break;
            }
        }

        // F2（0.2.74）：保底一个「台词类」入口。96 分/6 名额硬截断时，台词入口容易被菜单/元数据挤掉；
        // 这里发现一个都没有时，用当前最高分的台词候选换掉名额里最后一个。
        if (dialogueCandidateIds.Count > 0 &&
            !selected.Any(v => dialogueCandidateIds.Contains(v.CandidateId)))
        {
            var bestDialogue = views.FirstOrDefault(v => dialogueCandidateIds.Contains(v.CandidateId));
            if (bestDialogue is not null)
            {
                if (selected.Count >= MaxHookPoints)
                {
                    selected.RemoveAt(selected.Count - 1);
                }

                selected.Add(bestDialogue);
            }
        }

        if (selected.Count == 0)
        {
            return ForgeResult<PatchPlan>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                "所有候选的 Source 都无法解析成「类型::方法」目标。",
                "检查分析报告是否来自本适配器（Source 形如 assembly:Assembly-CSharp.dll!/Type::Method）。");
        }

        var slug = ResolveSlug(context);
        if (slug is null)
        {
            return ForgeResult<PatchPlan>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                "无法从 GameContext 得到合法 slug。",
                "用 --slug 指定小写 slug（字母/数字/点/下划线/连字符）。");
        }

        var points = new List<PatchPoint>(selected.Count);
        for (var i = 0; i < selected.Count; i++)
        {
            var view = selected[i];
            var id = "hook-" + (i + 1).ToString("D3", CultureInfo.InvariantCulture);
            var description = Describe(view);
            points.Add(new PatchPoint
            {
                Id = id,
                Kind = PatchPointKind.Hook,
                Target = UnityMonoTargets.BuildHookTarget(new UnityMonoParsedSource
                {
                    AssemblyFile = "Assembly-CSharp.dll",
                    TypeFullName = view.TypeFullName,
                    MethodName = view.MethodName,
                }),
                CandidateId = view.CandidateId,
                Confidence = ConfidenceValue(view),
                Description = description,
            });
        }

        var plan = new PatchPlan
        {
            GameSlug = slug,
            Route = PlanRoute.AutomaticWithConfirmation,
            Target = new PlanTarget
            {
                GamePath = context.GameDirectory,
                Engine = UnityMonoAdapter.EngineId,
                EngineVersion = analysis.Detection.Version,
                Bitness = analysis.Detection.Bitness,
            },
            Confidence = Math.Round(points.Average(p => p.Confidence), 3),
            PatchPoints = points,
            Artifacts = UnityMonoTargets.BuildArtifacts(slug, points),
            Risks = BuildRisks(analysis, selected),
            ManualTodo = BuildManualTodo(analysis, parseIssues, selected),
        };

        return ForgeResult<PatchPlan>.Success(plan);
    }

    private static string TargetLabel(UnityMonoCandidateView view)
    {
        var type = view.TypeFullName;
        var dot = type.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < type.Length)
        {
            type = type[(dot + 1)..];
        }

        return type + "::" + view.MethodName;
    }

    private static int CompareViews(UnityMonoCandidateView left, UnityMonoCandidateView right)
    {
        var result = UnityMonoTargets.PriorityOf(left.MethodName).CompareTo(
            UnityMonoTargets.PriorityOf(right.MethodName));
        if (result != 0)
        {
            return result;
        }

        // 0.2.64（F4 弱判定）：本程序集内没有调用者的候选可能是死代码，排在后面。
        result = IsLikelyDeadCode(left).CompareTo(IsLikelyDeadCode(right));
        if (result != 0)
        {
            return result;
        }

        result = right.Score.CompareTo(left.Score);
        if (result != 0)
        {
            return result;
        }

        return string.CompareOrdinal(left.CandidateId, right.CandidateId);
    }

    private static bool IsLikelyDeadCode(UnityMonoCandidateView view)
    {
        foreach (var line in view.Evidence)
        {
            if (!line.StartsWith("程序集内调用者", StringComparison.Ordinal))
            {
                continue;
            }

            return System.Text.RegularExpressions.Regex.IsMatch(
                line,
                @"调用者\s*0\s*个",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }

        return false;
    }

    private static string? ResolveSlug(GameContext context)
    {
        if (PathSafety.IsValidSlug(context.Slug))
        {
            return context.Slug;
        }

        try
        {
            var slug = PathSafety.SanitizeSlug(PathSafety.SlugFromGameDirectory(context.GameDirectory));
            return PathSafety.IsValidSlug(slug) ? slug : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or ForgePathException)
        {
            return null;
        }
    }

    private static string Describe(UnityMonoCandidateView view)
    {
        var rule = view.Evidence.FirstOrDefault(e => e.StartsWith("rules ", StringComparison.Ordinal));
        var s9 = rule is not null && rule.Contains("S9=20", StringComparison.Ordinal);
        var chain = ExtractPropertyChain(view);
        var fields = ExtractTextFields(view);
        var read = chain is not null
            ? $"正文入口没有 string 形参，文本在实例属性链 {chain} 上（生成器反射读取，不写死字段偏移）"
            : view.StringParameter is null && fields is not null
                ? $"实例文本字段（{fields}）：方法没有 string 形参，生成器会按实例字段兜底朗读"
                : UnityMonoTargets.InspectParameter(view);
        var signature = string.IsNullOrWhiteSpace(view.Signature)
            ? $"{view.TypeFullName}::{view.MethodName}"
            : view.Signature.StartsWith("签名：", StringComparison.Ordinal)
                ? view.Signature["签名：".Length..]
                : view.Signature;
        return
            $"读取 {view.TypeFullName}::{view.MethodName}：{read}。完整签名（Cecil）：{signature}。" +
            $"依据：候选 {view.CandidateId} 静态得分 {view.Score.ToString("0.#", CultureInfo.InvariantCulture)}，{view.Confidence}" +
            $"{(s9 ? "，S9 命中（string 参数 + 打字机/Tween sink）" : string.Empty)}；不含台词原文。" +
            "Hook 目标省略参数类型以兼容生成器白名单；生成器会把上面的形参类型交给运行期 MethodResolver 挑唯一重载（0.2.83）。" +
            DescribeTextFields(view) +
            DescribePropertyChain(view);
    }

    /// <summary>
    /// 0.2.48：把「文本属性链」证据带进 plan。Utage 这类框架的正文入口没有 string 形参，
    /// 文本在实例属性链上（如 Text.OriginalText），生成器据此生成反射读取的补丁。
    /// </summary>
    private static string? ExtractPropertyChain(UnityMonoCandidateView view)
    {
        var line = view.Evidence.FirstOrDefault(e => e.StartsWith("文本属性链：", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var chain = line["文本属性链：".Length..].Trim().TrimEnd('。');
        return string.IsNullOrWhiteSpace(chain) ? null : chain;
    }

    private static string DescribePropertyChain(UnityMonoCandidateView view)
    {
        var chain = ExtractPropertyChain(view);
        return chain is null ? string.Empty : $" 文本属性链：{chain}。";
    }

    /// <summary>
    /// 把候选证据里的实例文本字段名带进 plan：方法没有字符串参数时，生成器据此读实例字段兜底朗读。
    /// 只复制字段名，不读台词原文。
    /// </summary>
    private static string DescribeTextFields(UnityMonoCandidateView view)
    {
        var names = ExtractTextFields(view);
        return names is null ? string.Empty : $" 类型文本字段：{names}。";
    }

    /// <summary>
    /// 0.2.55：把「类型文本字段」证据抽出来。游戏自带管理器（例如《钟塔》的 DialogueSceneManager）
    /// 的台词入口没有 string 形参，文本在实例字段上；这类入口应算“能读”，不能算“只能做调用计数”。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex DialogueLikeTypeRx =
        new("(dialogue|dialog|adv|message|story|novel|scenario|text|scene|presenter|bubble|caption|subtitle|speech|say)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsDialogueLikeType(string? typeFullName) =>
        !string.IsNullOrWhiteSpace(typeFullName) && DialogueLikeTypeRx.IsMatch(typeFullName);

    // 0.2.60：整名匹配，避免 locationText / musicDuration 这类“带 text 但不是台词”的参数被误算。
    private static readonly System.Text.RegularExpressions.Regex DialogueParameterNameRx = new(
        "^(?:(?:dialogue|dialog|message|line|speech|say|caption|subtitle|content|body|sentence)(?:text|line|message)?|text|name|speaker|speakerName|characterName|nameText)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// 0.2.60：判断 string 形参名像不像台词（text/message/line/dialogue/…）。
    /// 像 locationText、musicDuration 这种就叫“不是正文入口”。
    /// </summary>
    private static bool IsDialogueTextParameter(UnityMonoCandidateView view) =>
        view.StringParameter is { Length: > 0 } name && DialogueParameterNameRx.IsMatch(name);

    private static string? ExtractTextFields(UnityMonoCandidateView view)
    {
        var line = view.Evidence.FirstOrDefault(e => e.StartsWith("类型文本字段：", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var names = line["类型文本字段：".Length..].Trim().TrimEnd('。');
        return string.IsNullOrWhiteSpace(names) ? null : names;
    }

    private static double ConfidenceValue(UnityMonoCandidateView view) => view.MethodName switch
    {
        "AnimateText" => 0.90,
        "DOText" => 0.85,
        "Choice" => 0.85,
        "HandleMessageText" => 0.80,
        "HandleMainText" => 0.70,
        "PageTextChange" => 0.80,
        "OnTextChanged" => 0.75,
        _ => view.Confidence switch
        {
            "high" => Math.Clamp(view.Score / 100.0, 0.5, 0.90),
            "medium" => Math.Clamp(view.Score / 100.0, 0.4, 0.80),
            _ => Math.Clamp(view.Score / 100.0, 0.25, 0.60),
        },
    };

    private static string ReadConfidence(List<string> evidence, double score)
    {
        foreach (var line in evidence)
        {
            if (line.Contains("confidence=high", StringComparison.Ordinal))
            {
                return "high";
            }

            if (line.Contains("confidence=medium", StringComparison.Ordinal))
            {
                return "medium";
            }

            if (line.Contains("confidence=low", StringComparison.Ordinal))
            {
                return "low";
            }
        }

        return score >= 70 ? "high" : score >= 50 ? "medium" : "low";
    }

    private static string? ExtractStringParameter(string signature)
    {
        var open = signature.IndexOf('(');
        var close = signature.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            return null;
        }

        var inner = signature[(open + 1)..close];
        foreach (var token in inner.Split(','))
        {
            var parameter = token.Trim();
            if (parameter.StartsWith("System.String ", StringComparison.Ordinal))
            {
                var name = parameter["System.String ".Length..].Trim();
                return name.Length > 0 ? name : null;
            }
        }

        return null;
    }

    private static List<RiskItem> BuildRisks(AnalysisReport analysis, IReadOnlyList<UnityMonoCandidateView> selected)
    {
        var targets = string.Join("、", selected.Take(4).Select(TargetLabel));
        var risks = new List<RiskItem>
        {
            new()
            {
                Id = "R-101",
                Severity = RiskSeverity.High,
                Description = $"静态计划未验证运行期调用顺序/参数非空；本计划 {selected.Count} 个入口（{targets}）需在 manual 模式复核。",
                Mitigation = "首轮只开 manual 模式，确认每个 Hook 命中后再开自动朗读。",
            },
            new()
            {
                Id = "R-102",
                Severity = RiskSeverity.Medium,
                Description = "Hook Target 只用「类型:方法」，省略了参数类型；0.2.83 起生成器会用证据里的完整签名在运行期挑唯一重载，但证据缺签名或重载靠泛型/继承区分时仍可能歧义。",
                Mitigation = "完整签名写在补丁点 Description（分析阶段已带）；若日志仍报歧义，人工补参数类型或只保留一个重载。",
            },
            new()
            {
                Id = "R-103",
                Severity = RiskSeverity.Medium,
                Description = "Choice 的选项文本与回显时机未由静态分析证明；实测回显延迟可达数十秒。",
                Mitigation = "加 choice_echo 事件与去重窗口，必要时人工确认选项回显。",
            },
            new()
            {
                Id = "R-104",
                Severity = RiskSeverity.Low,
                Description = "AnimateText 的 string 参数可能是本地化简中，DOText/原稿层可能是日文；跨层配对未验证。",
                Mitigation = "按 docs/14 的 language 配置显式选择朗读层，并保留两层 hash 供运行期配对。",
            },
        };

        if (analysis.Candidates.Count < 4)
        {
            risks.Add(new RiskItem
            {
                Id = "R-105",
                Severity = RiskSeverity.Medium,
                Description = $"候选只有 {analysis.Candidates.Count} 条，可能漏掉每句入口/选项入口。",
                Mitigation = "检查 coverage/unverified，必要时放宽阈值或人工补入口。",
            });
        }

        // 0.2.48：本机真实游戏（Mystery Lover 2 / Utage）复盘——只挑到系统对话框按钮时，
        // 装上后正常剧情一句都不会读，用户却会以为补丁可用。这里把风险提前说清楚。
        // 0.2.58：批量子代理实测发现“启动器/工具类也有文本字段”，会把 R-106 骗过去
        // （《我与你共度的七日》外层启动器 PPIViewer::Start 就是例子）。实例文本字段只有在
        // “对话/剧情类类型”上才算正文入口，否则仍按没有正文入口处理。
        // 0.2.60：只读「位置/音乐时长/标题」这类字符串参数不算正文入口；
        // 只剩这种钩子时必须报 R-106，别让“6 个钩子都挂上但一句台词不读”的计划通过。
        var hasDialogueEntry = selected.Any(v =>
            ExtractPropertyChain(v) is not null ||
            IsDialogueTextParameter(v) ||
            v.MethodName is "AnimateText" or "DOText" or "HandleMessageText" or "HandleMainText"
                or "PageTextChange" or "OnTextChanged" ||
            (ExtractTextFields(v) is not null && IsDialogueLikeType(v.TypeFullName)));
        if (!hasDialogueEntry)
        {
            risks.Add(new RiskItem
            {
                Id = "R-106",
                Severity = RiskSeverity.High,
                Description = "本计划里没有已识别的正文入口（台词管线），可能只在对话框/菜单弹出时朗读，正常剧情一句都不读。",
                Mitigation = "先运行期 manual 模式确认能否读到正文；读不到时改用 analyze --advanced 做原生分析或人工指定入口，别直接把补丁当可用。",
            });
        }

        var deadHooks = selected.Where(IsLikelyDeadCode).ToList();
        if (deadHooks.Count > 0)
        {
            risks.Add(new RiskItem
            {
                Id = "R-107",
                Severity = RiskSeverity.Medium,
                Description = $"计划里有 {deadHooks.Count} 个钩子在本程序集内没有调用者（可能是未运行的旧代码/死代码），装好后可能一句都不读：{string.Join("、", deadHooks.Take(3).Select(v => v.TypeFullName + "::" + v.MethodName))}。",
                Mitigation = "优先运行期确认这几个钩子是否真的被调用；若没有，人工换到实际运行的入口（F4 只做弱判定，不做控制流证明）。",
            });
        }

        return risks;
    }

    private static List<ManualTodoItem> BuildManualTodo(
        AnalysisReport analysis,
        List<string> parseIssues,
        IReadOnlyList<UnityMonoCandidateView> selected)
    {
        var targets = string.Join("、", selected.Select(TargetLabel));
        var todo = new List<ManualTodoItem>
        {
            new()
            {
                Id = "M-001",
                Title = "运行期 manual 模式复核 Hook 命中",
                Detail = $"确认本计划入口按预期被调用且能拿到文本：{targets}。只报命中不朗读时，检查入口有无字符串参数、实例文本字段或文本属性链。",
                Blocking = true,
            },
        };

        if (analysis.Candidates.Any(c => c.Kind == TextCandidateKind.Choice))
        {
            todo.Add(new ManualTodoItem
            {
                Id = "M-002",
                Title = "确认选项文本与回显去重",
                Detail = "选项文本/回显时机需运行期确认；按选项编号与文本哈希设短窗去重，避免重复朗读。",
                Blocking = true,
            });
        }

        if (analysis.Candidates.Any(c => c.Kind == TextCandidateKind.Dialogue))
        {
            todo.Add(new ManualTodoItem
            {
                Id = "M-003",
                Title = "确认朗读语言层与文本来源",
                Detail = "多语言版本需要确认朗读哪一层（如简中/原文）；在 profile 显式写 language，并保留哈希供配对。",
                Blocking = false,
            });
        }

        todo.Add(new ManualTodoItem
        {
            Id = "M-004",
            Title = "核对生成器的 Hook 目标解析",
            Detail = "生成器只接受基础类型参数；若运行期报同名重载歧义，按完整签名人工指定。",
            Blocking = false,
        });

        if (parseIssues.Count > 0)
        {
            todo.Add(new ManualTodoItem
            {
                Id = "M-005",
                Title = "人工处理无法解析来源的候选",
                Detail = "以下候选未进入 Hook 计划：" + string.Join("；", parseIssues.Take(5)),
                Blocking = false,
            });
        }

        if (analysis.Unverified.Count > 0)
        {
            todo.Add(new ManualTodoItem
            {
                Id = "M-006",
                Title = "逐条消化分析阶段的未验证项",
                Detail = string.Join("；", analysis.Unverified.Take(5)),
                Blocking = false,
            });
        }

        return todo;
    }
}
