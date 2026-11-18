using System.Globalization;
using System.Text;
using GalA11yForge.Core.Models;

namespace GalA11yForge.Adapters.Il2Cpp.Internal;

/// <summary>
/// 把一次静态扫描结果收敛成 <see cref="AnalysisReport"/>：候选打分、排序、覆盖度、风险、未验证项。
///
/// <para>
/// 与 Unity Mono 适配器的根本差异：Mono 能读 IL（<c>ldstr</c> 中文字面量、调用图），
/// IL2CPP 的 interop 方法体是 <c>il2cpp_runtime_invoke</c> 桩，<b>没有 IL 语义可用</b>。
/// 所以这里所有证据都来自元数据（类型/属性/参数/事件/框架），并且必须把
/// “运行时到底触发不触发”标成未验证，除非给了运行时探针 CSV。
/// </para>
/// </summary>
internal sealed class Il2CppAnalyzer
{
    internal const int MaxEvidenceLines = 8;

    /// <summary>字段兜底候选的最低有效分（B 级：可自动生成，但必须真机复验）。</summary>
    internal const double FieldFallbackMinScore = 45.0;

    /// <summary>
    /// 展示型字段兜底候选的优先分：让 Show/Display/Print 这类“明确把文本显示出来”的入口
    /// 排在 Apply/Handle/Update 这类辅助方法前面（0.2.12 真机选点顺序修正）。
    /// </summary>
    internal const double FieldFallbackPreferredScore = 52.0;

    /// <summary>对话宿主上的字段兜底候选（DialogueBubbleUI 等）再抬一档，优先占名额。</summary>
    internal const double FieldFallbackDialogueHostScore = 56.0;

    /// <summary>对话宿主 + Show/Display/Print 这类展示动词：同宿主里排最前。</summary>
    internal const double FieldFallbackDialoguePrimaryScore = 60.0;

    /// <summary>
    /// 判断一个没有 string 形参的实例方法是否值得做字段兜底：
    /// 声明类型像界面、类型上有已知文本字段、方法名像“显示/设置文本”，且不是属性访问器。
    /// </summary>
    private static bool IsFieldFallbackCandidate(Il2CppMethodFact method, Il2CppTypeFact? type)
    {
        if (type is null || method.IsStatic || method.IsPropertyMember || Il2CppTargets.IsHookable(method))
        {
            return false;
        }

        if (!Il2CppPatterns.FieldFallbackNameRx.IsMatch(method.Name))
        {
            return false;
        }

        if (!type.IsUiType && !Il2CppPatterns.TextHostTypeNameRx.IsMatch(type.Name))
        {
            return false;
        }

        // 0.2.12 真机：GiftCellView._text 会把饰品名当台词、LilithFolderView 会读界面字。
        // 装饰性容器（格子/列表项/文件夹/按钮…）上的文本不作为台词入口。
        if (Il2CppPatterns.FieldHostExcludeRx.IsMatch(type.Name))
        {
            return false;
        }

        return Il2CppFieldFallback.HasSupportedField(Il2CppFieldFallback.ParseMembers(type.TextMemberNames));
    }

    public AnalysisReport Analyze(
        GameContext context,
        EngineDetection detection,
        Il2CppOptions options,
        Il2CppScanOutcome scan,
        Il2CppRuntimeEvidence? runtime,
        CancellationToken cancellationToken)
    {
        var candidates = new List<TextCandidate>();
        var scored = new List<(Il2CppScoreResult Score, Il2CppMethodFact Method, Il2CppTypeFact? Type, Il2CppRuntimeFact? Runtime, double Effective, bool FieldFallback)>();

        var threshold = Math.Max(30.0, options.MinConfidence * 100.0);
        var textEventTypes = scan.Events
            .Where(e => Il2CppPatterns.TextEventNameRx.IsMatch(e.EventName))
            .Select(e => e.TypeFullName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var method in scan.Methods)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (method.IsConstructor || method.Name.StartsWith("remove_", StringComparison.Ordinal))
            {
                continue;
            }

            if (Il2CppPatterns.DebugNameRx.IsMatch(method.Name) || Il2CppPatterns.LifecycleNameRx.IsMatch(method.Name))
            {
                continue;
            }

            scan.Types.TryGetValue(method.TypeFullName, out var type);

            if (type?.IsInterface == true)
            {
                // 接口方法不可 patch：Harmony 只能打具体类型的实现。
                continue;
            }

            // 0.2.12 字段兜底候选：没有 string 形参，但实例类型上有文本字段、方法名像“显示文本”。
            // Lilith 真机（research/50）证明这类方法运行期确实能读到当前台词，
            // 所以不能被 IsNonTextInflow 提前丢掉（HandleTypingStopped 就是 0 参实例方法）。
            var fieldFallback = IsFieldFallbackCandidate(method, type);

            if (!fieldFallback && Il2CppScoring.IsPlumbing(method, type) && !options.AllowBaseWidgetHooks)
            {
                continue;
            }

            if (!fieldFallback && Il2CppScoring.IsNonTextInflow(method) && !method.IsEventAdder)
            {
                continue;
            }

            var facts = runtime?.Find(method.TypeFullName, method.Name);
            var cap = scan.Source == Il2CppSourceKind.Dump ? Il2CppScoring.DumpScoreCap : 100;
            var score = Il2CppScoring.Score(method, type, textEventTypes.Contains(method.TypeFullName), facts, cap);
            var fallbackFloor = fieldFallback && Il2CppPatterns.FieldFallbackPrimaryNameRx.IsMatch(method.Name)
                ? FieldFallbackPreferredScore
                : FieldFallbackMinScore;

            // 对话宿主（DialogueBubbleUI 这类）再抬一档，避免 LocalizedText / MemoFullView 这类
            // 通用或界面工具类在低数量名额里把真正对白的入口挤掉（0.2.15 真机）。
            // 同一个宿主里，Show/Display/Print 这类“把文本显示出来”的方法要排在 Apply*/Handle* 前面。
            if (fieldFallback && type is not null && Il2CppPatterns.FieldHostPriorityRx.IsMatch(type.Name))
            {
                var hostFloor = Il2CppPatterns.FieldFallbackPrimaryNameRx.IsMatch(method.Name)
                    ? FieldFallbackDialoguePrimaryScore
                    : FieldFallbackDialogueHostScore;
                fallbackFloor = Math.Max(fallbackFloor, hostFloor);
            }

            var effective = fieldFallback ? Math.Max(score.Score, fallbackFloor) : score.Score;

            if (effective < threshold)
            {
                continue;
            }

            scored.Add((score, method, type, facts, effective, fieldFallback));
        }

        scored.Sort(static (a, b) =>
        {
            // 运行时真正读到非空台词的优先；其次静态分；再看是否直接文本出口；最后按全名稳定排序。
            var runtimeA = a.Runtime is { NonEmptyHits: > 0 } ? 1 : 0;
            var runtimeB = b.Runtime is { NonEmptyHits: > 0 } ? 1 : 0;
            if (runtimeA != runtimeB)
            {
                return runtimeB.CompareTo(runtimeA);
            }

            var byScore = b.Effective.CompareTo(a.Effective);
            if (byScore != 0)
            {
                return byScore;
            }

            // 同分时按 R18 实测名次：管理器入口 → 打印机 → 面板 → 控件，未知通道排在已知之后。
            var byRank = a.Score.Rank.CompareTo(b.Score.Rank);
            if (byRank != 0)
            {
                return byRank;
            }

            var sinkA = a.Score.HasDirectSink ? 1 : 0;
            var sinkB = b.Score.HasDirectSink ? 1 : 0;
            if (sinkA != sinkB)
            {
                return sinkB.CompareTo(sinkA);
            }

            return string.CompareOrdinal(a.Method.Signature, b.Method.Signature);
        });

        var max = options.MaxCandidates;
        var index = 0;
        foreach (var item in scored)
        {
            if (index >= max)
            {
                break;
            }

            index++;
            var evidence = BuildEvidence(item.Method, item.Type, item.Score, item.Runtime, options, scan);
            if (item.FieldFallback)
            {
                evidence.Add("字段兜底加权：方法没有 string 形参，但声明类型上有可读文本字段，规则分补到 " +
                             item.Effective + " 分（B 级：生成原生字段读取补丁，运行期是否触发仍需真机复验）");
            }

            candidates.Add(new TextCandidate
            {
                Id = "tc-" + index.ToString("000", CultureInfo.InvariantCulture),
                Kind = ClassifyKind(item.Method, item.Type),
                Source = Il2CppTargets.BuildSource(item.Method, item.Method.AssemblyFile),
                Score = item.Effective,
                Evidence = evidence,
            });
        }

        var report = new AnalysisReport
        {
            SchemaVersion = 1,
            GamePath = context.GameDirectory,
            Detection = detection,
            Candidates = candidates,
            Coverage = BuildCoverage(scan, runtime, options, candidates),
            Risks = BuildRisks(scan, runtime, candidates),
            Unverified = BuildUnverified(scan, runtime, options, candidates),
        };

        return report;
    }

    private static TextCandidateKind ClassifyKind(Il2CppMethodFact method, Il2CppTypeFact? type)
    {
        if (Il2CppPatterns.ChoiceNameRx.IsMatch(method.Name) ||
            (type is not null && Il2CppPatterns.ChoiceNameRx.IsMatch(type.Name)))
        {
            return TextCandidateKind.Choice;
        }

        if (method.Name.Contains("Menu", StringComparison.OrdinalIgnoreCase) ||
            (type is not null && type.Name.Contains("Menu", StringComparison.OrdinalIgnoreCase)))
        {
            return TextCandidateKind.Menu;
        }

        return TextCandidateKind.Dialogue;
    }

    private static List<string> BuildEvidence(
        Il2CppMethodFact method,
        Il2CppTypeFact? type,
        Il2CppScoreResult score,
        Il2CppRuntimeFact? runtime,
        Il2CppOptions options,
        Il2CppScanOutcome scan)
    {
        var evidence = new List<string>
        {
            "签名：[" + method.AssemblyFile + "] " + method.Signature,
        };

        if (method.IsEventAdder && method.EventName is not null)
        {
            var eventFact = scan.Events.FirstOrDefault(e =>
                string.Equals(e.TypeFullName, method.TypeFullName, StringComparison.Ordinal) &&
                string.Equals(e.EventName, method.EventName, StringComparison.Ordinal));
            evidence.Add("事件：add_" + method.EventName + "(" +
                         (eventFact is null ? "未知处理器" : string.Join("|", eventFact.HandlerArgumentTypeNames)) +
                         ")，IL2CPP 下委托必须 ConvertDelegate 后再订阅");
        }
        else if (method.IsPropertyMember && method.PropertyName is not null)
        {
            evidence.Add("属性：" + method.PropertyName + " : " + (method.PropertyTypeName ?? "?") +
                         (method.IsPropertyMember && method.Name.StartsWith("set_", StringComparison.Ordinal)
                             ? "（setter）"
                             : "（accessor）"));
        }

        if (type is not null)
        {
            evidence.Add("类型：" + type.FullName + "（" + (type.BaseTypeName ?? "无基类") + "）" +
                         (type.FrameworkId is null ? string.Empty : "，框架=" + type.FrameworkId));

            // 字段兜底（0.2.12）的跨阶段契约：计划器与生成器都按前缀解析这一行。
            // 先过滤掉调试/路径类字段（_diag、PrefabPath…），只留真正可读的文本字段。
            // 静态方法没有实例，读不了实例字段，必须显式标出来，避免生成注定失败的补丁。
            var markerFields = Il2CppFieldFallback.ParseMembers(type.TextMemberNames);
            if (markerFields.Count > 0)
            {
                var markerText = string.Join("/", markerFields.Select(f => f.Name + ":" + f.SimpleType));
                evidence.Add(Il2CppFieldFallback.Marker + markerText +
                             (method.IsStatic
                                 ? "（静态方法：没有实例，字段兜底不可用）"
                                 : "（实例方法）"));
            }
        }

        if (score.Known)
        {
            evidence.Add("已知通道：R18 实测探针 " + score.KnownProbe + "（role=" + score.Role + "，实测名次优先于通用规则）");
        }

        evidence.Add(score.RuleLine);

        if (runtime is null)
        {
            evidence.Add("运行时未验证：静态元数据无法判断该方法运行期是否触发（需要真机探针）");
        }
        else if (!runtime.Tested)
        {
            evidence.Add("运行时未测：探针证据里没有调用次数（未验证）");
        }
        else if (runtime.NonEmptyHits > 0)
        {
            evidence.Add("运行时实测：" + runtime.Calls + " 次调用 / " + runtime.NonEmptyHits +
                         " 次非空" + (runtime.Verdict.Length == 0 ? string.Empty : "（" + runtime.Verdict + "）"));
        }
        else
        {
            evidence.Add("运行时未命中：" + runtime.Calls + " 次调用全部为空串 → 静态分不可信" +
                         (runtime.Verdict.Length == 0 ? string.Empty : "（" + runtime.Verdict + "）"));
        }

        var hookable = Il2CppTargets.IsHookable(method);
        if (!hookable)
        {
            var fallbackFields = type is null ? [] : Il2CppFieldFallback.ParseEvidence(evidence);
            if (!method.IsStatic && Il2CppFieldFallback.HasSupportedField(fallbackFields))
            {
                evidence.Add("可字段兜底：没有 string 形参，但实例上有文本字段，生成器会按原生字段读取兜底朗读");
            }
            else
            {
                evidence.Add("不可自动打补丁：没有 string 形参，文本只能从字段/事件侧人工接入");
            }
        }
        else
        {
            var complex = method.Parameters.Count(p => !Il2CppTargets.IsSimpleType(p.TypeName));
            if (complex > 0)
            {
                evidence.Add("补丁只声明简单类型形参：" + complex + " 个复杂形参（" +
                             string.Join(";", method.Parameters.Where(p => !Il2CppTargets.IsSimpleType(p.TypeName)).Select(p => p.TypeName)) +
                             "）不注入");
            }
        }

        evidence.Add("language=" + options.Language);
        evidence.Add("原文未落盘：静态分析只读元数据，未读取任何台词文本");

        return evidence.Take(MaxEvidenceLines).ToList();
    }

    private static CoverageReport BuildCoverage(
        Il2CppScanOutcome scan,
        Il2CppRuntimeEvidence? runtime,
        Il2CppOptions options,
        IReadOnlyList<TextCandidate> candidates)
    {
        var covered = new List<string>
        {
            "IL2CPP 引擎识别与布局探测（GameAssembly.dll / global-metadata.dat / BepInEx 6）",
        };

        if (scan.Source == Il2CppSourceKind.Interop)
        {
            covered.Add("interop 元数据静态分析：" + scan.AssemblyCount + " 个程序集 / " +
                        scan.TypeCount + " 类型 / " + scan.MethodCount + " 方法 / " + scan.Events.Count + " 事件");
            covered.Add("生成可编译的 BepInEx 6 IL2CPP 插件工程（HarmonyX 补丁，声明类型安全解析）");
        }
        else if (scan.Source == Il2CppSourceKind.Dump)
        {
            covered.Add("dump.cs 降级静态分析：" + scan.TypeCount + " 类型 / " + scan.MethodCount +
                        " 方法（无属性/事件结构）");
            covered.Add("生成 BepInEx 6 插件骨架（补丁目标需人工确认后再启用）");
        }

        covered.Add("候选清单：" + candidates.Count + " 条，等级 A/B/C 由规则 " +
                    Il2CppScoring.RuleVersion + " 给出");

        var notCovered = new List<string>
        {
            "无需人工介入的全自动安装：IL2CPP 必须先手动装 BepInEx 6 并成功启动一次（R18 实测首启 229s / 71MB / 97 文件）",
            "字符串实参的静态确认：interop 方法体是调用桩，静态侧看不到任何台词内容",
            "选项朗读：ChoiceHandlerPanel.AddChoiceButton(ChoiceState) 需要 ChoiceState 字段布局，v1 不自动生成",
            "多行连续对话：R18 只观察到 1 行，连续多行未实测（最大未验证项）",
        };

        if (scan.Source == Il2CppSourceKind.Dump)
        {
            notCovered.Add("属性/事件结构：dump.cs 没有 event 表，属性 setter 只能按方法名推断");
        }

        if (runtime is null)
        {
            notCovered.Add("运行时验证：没有提供 il2cpp.runtimeEvidence 探针 CSV，所有候选都停留在“未验证”");
        }

        if (!options.AllowBaseWidgetHooks)
        {
            notCovered.Add("基础控件通道：TMP_Text.set_text / UnityEngine.UI.Text.set_text 默认不生成补丁（观测到 5785 次 UI 噪声）");
        }

        var manualTodo = new List<string>();
        if (scan.Source != Il2CppSourceKind.Interop)
        {
            manualTodo.Add("在游戏目录安装 BepInEx 6.0.0-be.788 并启动一次，生成 BepInEx/interop/*.dll（首启约 229s，需要窗口在前台）");
        }

        manualTodo.Add("准备 Tolk.dll（NVDA/ZDSR/SAPI 桥），放到插件目录；缺失时插件自动降级为日志输出");
        manualTodo.Add("真机回归：进入游戏跑 3 行以上连续对话，确认每行只朗读一次且无空串");

        return new CoverageReport
        {
            Covered = covered,
            NotCovered = notCovered,
            ManualTodo = manualTodo,
        };
    }

    private static List<RiskItem> BuildRisks(
        Il2CppScanOutcome scan,
        Il2CppRuntimeEvidence? runtime,
        IReadOnlyList<TextCandidate> candidates)
    {
        var risks = new List<RiskItem>
        {
            new()
            {
                Id = "R-201",
                Severity = RiskSeverity.High,
                Description = "BepInEx 6 首次启动需要 229s 且必须有可用图形窗口；无 interop 时补丁工程无法编译（无元数据可引用）。",
                Mitigation = "生成器把“安装 BepInEx 6 并启动一次”列为阻塞人工待办；interop 缓存键 assembly-hash.txt 变化后需重新生成。",
            },
            new()
            {
                Id = "R-202",
                Severity = RiskSeverity.High,
                Description = "子类继承的方法必须打在声明类型上；打错类型会抛 HarmonyException: Patching exception in method null（R18 实测）。",
                Mitigation = "补丁点强制携带声明类型，生成代码用 AccessTools.Method(声明类型, 方法名, 参数类型) 解析并在 Prepare 失败时静默跳过。",
            },
            new()
            {
                Id = "R-203",
                Severity = RiskSeverity.Medium,
                Description = "文本 setter 大量以空串触发（R18：set_Text 6 次调用中 5 次为空，set_PrintedText 14 次中 13 次为空），按调用计数会得出错误结论。",
                Mitigation = "A11yRuntime 过滤空白串；分析阶段只把非空次数当证据。",
            },
            new()
            {
                Id = "R-204",
                Severity = RiskSeverity.Medium,
                Description = "同一行台词会从多个通道重复触发（R18 同一 8 字行命中 6 个通道，SHA1 相同）。",
                Mitigation = "A11yRuntime 用 (hash, 帧号) 窗口去重，同一行只朗读一次。",
            },
            new()
            {
                Id = "R-205",
                Severity = RiskSeverity.Medium,
                Description = "IL2CPP interop 没有 IL 语义：无法静态证明某个方法真的收到了台词，静态分高但运行时不触发是常见情况（RevealableTMProText.set_Text 即如此）。",
                Mitigation = "报告把未提供运行时证据的候选标为“未验证”；提供候选钩子证据 CSV 后才升 A 级。",
            },
            new()
            {
                Id = "R-206",
                Severity = RiskSeverity.Medium,
                Description = "UniTask 等结构体返回值不能 await、不能碰 __result；生成器只生成 Prefix/Postfix 读参数。",
                Mitigation = "生成模板不生成 transpiler、不读写 __result；非简单参数方法不进自动补丁。",
            },
        };

        if (scan.Source == Il2CppSourceKind.Dump)
        {
            risks.Add(new RiskItem
            {
                Id = "R-207",
                Severity = RiskSeverity.High,
                Description = "仅 dump.cs 时没有 interop 程序集：生成的工程无法解析游戏类型，编译只能验骨架（本适配器会标 unverified）。",
                Mitigation = "路线降为 SemiAutomatic，并在人工待办里要求先跑一次 BepInEx 6 再重新生成。",
            });
        }

        if (runtime is null)
        {
            risks.Add(new RiskItem
            {
                Id = "R-208",
                Severity = RiskSeverity.Medium,
                Description = "没有运行时探针证据：无法区分“真正出口”和“运行时不触发的同名通道”。",
                Mitigation = "在 profile 里配置 il2cpp.runtimeEvidence=<candidate-hooks.csv 路径> 后重新分析。",
            });
        }

        if (candidates.Count == 0)
        {
            risks.Add(new RiskItem
            {
                Id = "R-209",
                Severity = RiskSeverity.High,
                Description = "没有产出任何候选：可能是该游戏未使用已知对话框架，或 interop 程序集被扫描上限截断。",
                Mitigation = "查看 evidence 里的扫描统计与 warnings；必要时提高 il2cpp.maxCandidates 或人工指定目标。",
            });
        }

        return risks;
    }

    private static List<string> BuildUnverified(
        Il2CppScanOutcome scan,
        Il2CppRuntimeEvidence? runtime,
        Il2CppOptions options,
        IReadOnlyList<TextCandidate> candidates)
    {
        var unverified = new List<string>();

        if (runtime is null)
        {
            unverified.Add("所有候选的“运行期会收到台词”均未验证（没有运行时证据 CSV）");
        }
        else
        {
            var untested = candidates.Count(c => !runtime.IsEmpty && c.Evidence.Any(e => e.StartsWith("运行时未测", StringComparison.Ordinal)));
            if (untested > 0)
            {
                unverified.Add(untested + " 条候选在运行时证据里没有调用次数（未测）");
            }
        }

        unverified.Add("连续多行对话的朗读节奏与去重窗口未实测（R18 只抓到 1 行）");
        unverified.Add("Tolk 在 IL2CPP 插件进程内的加载与 NVDA 实际输出未实测");

        if (scan.Source == Il2CppSourceKind.Dump)
        {
            unverified.Add("dump.cs 路线没有属性/事件结构，setter 与事件的对应关系是推断的");
        }

        if (options.AllowBaseWidgetHooks)
        {
            unverified.Add("已开启基础控件通道（allowBaseWidgetHooks）：会把 UI 文案一起读出来，仅诊断用");
        }

        foreach (var warning in scan.Warnings.Take(5))
        {
            unverified.Add("扫描告警：" + warning);
        }

        return unverified;
    }

    /// <summary>给 CLI/日志用的一句话摘要（不包含任何台词内容）。</summary>
    public static string DescribeScan(Il2CppScanOutcome scan)
    {
        var sb = new StringBuilder();
        sb.Append("source=").Append(scan.Source);
        sb.Append(", assemblies=").Append(scan.AssemblyCount);
        sb.Append(", types=").Append(scan.TypeCount);
        sb.Append(", methods=").Append(scan.MethodCount);
        sb.Append(", events=").Append(scan.Events.Count);
        sb.Append(", elapsed=").Append(scan.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append('s');
        return sb.ToString();
    }
}
