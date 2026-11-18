using System.Globalization;
using GalA11yForge.Core.Models;

namespace GalA11yForge.Adapters.Il2Cpp.Internal;

/// <summary>
/// 候选 → 补丁计划。路线选择由**原料质量**决定，而不是由候选分数决定：
/// 有 interop → <see cref="PlanRoute.AutomaticWithConfirmation"/>（能生成可编译工程）；
/// 只有 dump.cs → <see cref="PlanRoute.SemiAutomatic"/>（补丁目标要人工确认，工程只能验骨架）；
/// 什么都没有 → <see cref="PlanRoute.SemiAutomatic"/> + 阻塞人工待办。
/// </summary>
internal sealed class Il2CppPlanner
{
    internal const int MaxHookPoints = 6;

    /// <summary>
    /// 字段兜底最多自动放 2 个入口。其余进人工待办：字段兜底靠名字猜，多放只会把
    /// 界面字/工具类文本念出来（0.2.15 真机：Lilith 的 MemoFullView/LocalizedText 会占名额）。
    /// </summary>
    internal const int MaxFieldFallbackHooks = 2;

    /// <summary>人工待办上限：候选多时不要把计划撑成几十条重复项。</summary>
    internal const int MaxManualTodos = 8;

    /// <summary>“interop 后可自动接入”类待办上限：只列前几条，避免淹没真正阻塞的环境待办。</summary>
    internal const int MaxInteropTodos = 3;

    public PatchPlan Plan(
        GameContext context,
        AnalysisReport report,
        EngineDetection detection,
        Il2CppOptions options,
        Il2CppLayout layout)
    {
        var slug = Il2CppTargets.NormalizeSlug(context.Slug, context.GameDirectory);
        var route = layout.BestSource switch
        {
            Il2CppSourceKind.Interop => PlanRoute.AutomaticWithConfirmation,
            _ => PlanRoute.SemiAutomatic,
        };

        var points = new List<PatchPoint>();
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);
        var manualTodo = new List<ManualTodoItem>();
        var manualIndex = 0;
        var fieldHooksAdded = 0;
        var interopTodos = 0;
        var seenTodoTitles = new HashSet<string>(StringComparer.Ordinal);

        // 自动补丁点只收 A 级（≥70）：IL2CPP 静态分高分也可能运行期不触发，
        // 把 60 分的候选也塞进插件只会增加噪声与真机排查成本。
        void AddManualTodo(ManualTodoItem item)
        {
            if (manualTodo.Count >= MaxManualTodos)
            {
                return;
            }

            if (seenTodoTitles.Add(item.Title))
            {
                manualTodo.Add(item);
            }
        }

        foreach (var candidate in report.Candidates)
        {
            if (points.Count >= MaxHookPoints)
            {
                break;
            }

            // 0.2.12：字段兜底候选（B 级）在分析阶段只补到 45 分，
            // 但它不是“噪声低分候选”——Lilith 真机已证明能读到当前台词，所以单独放行。
            var target = Il2CppTargets.TryParse(candidate.Source);
            var fallbackFields = Il2CppFieldFallback.ParseEvidence(candidate.Evidence);
            var fieldOnly = target is not null
                            && !target.CanGenerateHook
                            && !target.HasStringParameter
                            && CanGenerateFieldFallback(target)
                            && Il2CppFieldFallback.HasSupportedField(fallbackFields);
            var minScore = fieldOnly ? Il2CppAnalyzer.FieldFallbackMinScore : 70.0;

            if (candidate.Score < minScore)
            {
                // async 方法即使被真机通道表打了高分也不能自动补（R21 实测会卡死游戏），
                // 这里给一条明确的人工待办，避免用户以为“漏掉了”。
                if (target is not null && IsAsyncTarget(target))
                {
                    AddManualTodo(new ManualTodoItem
                    {
                        Id = "M-" + (manualIndex++ + 10).ToString("000", CultureInfo.InvariantCulture),
                        Title = "async 方法不能自动补：" + target.Display,
                        Detail = "R21 真机实测（task-37）：Harmony 直接补 async 方法会破坏 await 状态机，" +
                                 "游戏抛 MethodAccessException 并卡在第一条台词。已从自动方案排除；" +
                                 "建议改用同为文本出口的非 async 方法，或走事件通道由人工订阅。",
                        Blocking = false,
                    });
                }

                continue;
            }

            if (target is null)
            {
                continue;
            }

            if (target.IsEventAdder)
            {
                AddManualTodo(BuildEventTodo(target, candidate));
                continue;
            }

            if (layout.BestSource != Il2CppSourceKind.Interop)
            {
                if (interopTodos >= MaxInteropTodos)
                {
                    continue;
                }

                interopTodos++;

                // 只有 dump.cs 时不能把候选写成 Hook：没有游戏程序集引用，生成的 typeof(...) 编译不过。
                AddManualTodo(new ManualTodoItem
                {
                    Id = "M-" + (manualIndex++ + 10).ToString("000", CultureInfo.InvariantCulture),
                    Title = "interop 后可自动接入：" + target.Display,
                    Detail = "当前只有 dump.cs，只能静态推断；装好 BepInEx 6 生成 interop 后重跑，可自动变成补丁点（" +
                             candidate.Score + " 分，探针证据见分析报告）。",
                    Blocking = false,
                });
                continue;
            }

            if (!target.CanGenerateHook)
            {
                // 0.2.12：没有 string 形参不等于没有救。真实运行期（research/50，task-57/58）证明
                // 部分游戏的文本只存在实例字段里（TMP 组件或 string 字段），可以按原生字段读。
                // 0.2.13：但只有 fieldOnly（没有 string 形参）才走字段兜底——形参无名仍是 string 通道，
                // 不能硬塞成字段补丁（Naninovel ChatMessage::set_messageText 实测编译不过）。
                if (fieldOnly)
                {
                    // 字段兜底太多会读界面字（0.2.15 真机），只自动放前两个，其余进人工待办。
                    if (fieldHooksAdded >= MaxFieldFallbackHooks)
                    {
                        AddManualTodo(new ManualTodoItem
                        {
                            Id = "M-" + (manualIndex++ + 10).ToString("000", CultureInfo.InvariantCulture),
                            Title = "字段兜底名额已满（人工确认）：" + target.Display,
                            Detail = "0.2.15 起每款游戏最多自动放 " + MaxFieldFallbackHooks +
                                     " 个字段兜底入口，避免把界面/工具字当台词读。这条候选需要人工确认是否接入：" +
                                     string.Join(" / ", candidate.Evidence.Take(3)),
                            Blocking = false,
                        });
                        continue;
                    }

                    if (points.Count >= MaxHookPoints || !seenTargets.Add(target.Target))
                    {
                        continue;
                    }

                    points.Add(BuildFieldHookPoint(points.Count + 1, target, candidate, fallbackFields));
                    fieldHooksAdded++;
                    continue;
                }

                AddManualTodo(new ManualTodoItem
                {
                    Id = "M-" + (manualIndex++ + 10).ToString("000", CultureInfo.InvariantCulture),
                    Title = "人工接入通道：" + target.Display,
                    Detail = "该方法无法自动生成补丁（没有可声明的 string 形参，或声明类型是泛型/抽象/接口类型）。" +
                             "需要在插件里用反射读实例字段，或改从事件/管理器侧接入。候选证据：" +
                             string.Join(" / ", candidate.Evidence.Take(4)),
                    Blocking = false,
                });
                continue;
            }

            if (!seenTargets.Add(target.Target))
            {
                continue;
            }

            points.Add(BuildHookPoint(points.Count + 1, target, candidate, candidate.Score));
        }

        // 兜底：未知框架可能没有一条候选到 70 分。此时只放一条最高分通道，
        // 并在计划里明确标注“低置信、必须真机确认”，而不是一个补丁都不生成。
        if (points.Count == 0 && layout.BestSource == Il2CppSourceKind.Interop)
        {
            var fallback = report.Candidates
                .Where(c => c.Score >= 50)
                .Select(c => (Candidate: c, Target: Il2CppTargets.TryParse(c.Source)))
                .FirstOrDefault(x => x.Target is { CanGenerateHook: true, IsEventAdder: false });

            if (fallback.Target is not null)
            {
                points.Add(BuildHookPoint(1, fallback.Target, fallback.Candidate, fallback.Candidate.Score));
                manualTodo.Insert(0, new ManualTodoItem
                {
                    Id = "M-000",
                    Title = "低置信兜底补丁：必须真机确认",
                    Detail = "没有任何候选达到 70 分，生成器只保留了最高分通道 " + fallback.Target.Display +
                             "（" + fallback.Candidate.Score + " 分）。先确认它是否真的收到台词，再考虑后续扩展。",
                    Blocking = false,
                });
            }
        }

        // 基础人工待办：环境、语音、真机验证。
        if (layout.BestSource != Il2CppSourceKind.Interop)
        {
            manualTodo.Insert(0, new ManualTodoItem
            {
                Id = "M-001",
                Title = "先让 BepInEx 6 生成 interop",
                Detail = "把 BepInEx 6.0.0-be.788 解到游戏目录并启动一次（R18 实测首启 229s、71.0MB、97 文件，需要窗口在前台）。" +
                         "完成后 BepInEx/interop/*.dll 才会出现，插件工程才可能编译通过。",
                Blocking = true,
            });
        }

        manualTodo.Add(new ManualTodoItem
        {
            Id = "M-002",
            Title = "放好 Tolk.dll",
            Detail = "把 Tolk.dll（32/64 位与游戏一致）放到插件目录。缺失时插件自动降级为日志输出，不会崩溃但读不出声。",
            Blocking = false,
        });

        manualTodo.Add(new ManualTodoItem
        {
            Id = "M-004",
            Title = "真机回归 3 行以上连续对话",
            Detail = "验证每行只朗读一次、无空串朗读、切场景后仍然生效。R18 只抓到 1 行，连续多行是最大的未验证项。",
            Blocking = false,
        });

        if (report.Candidates.Any(c => c.Kind == TextCandidateKind.Choice))
        {
            manualTodo.Add(new ManualTodoItem
            {
                Id = "M-005",
                Title = "选项朗读需要 ChoiceState 字段布局",
                Detail = "ChoiceHandlerPanel.AddChoiceButton(ChoiceState) 的形参非简单类型，v1 不自动生成。" +
                         "需要先用 dump.cs 确认 ChoiceState 的文本/顺序字段，再补一个 Postfix。",
                Blocking = false,
            });
        }

        if (layout.BestSource == Il2CppSourceKind.Dump)
        {
            manualTodo.Add(new ManualTodoItem
            {
                Id = "M-006",
                Title = "有 interop 后重新生成",
                Detail = "当前只有 dump.cs：补丁目标无法在编译期解析游戏类型，工程只能验骨架。装好 BepInEx 6 后重跑 analyze/generate。",
                Blocking = true,
            });
        }

        var confidence = points.Count == 0
            ? 0.0
            : Math.Round(points.Average(p => p.Confidence), 4);

        return new PatchPlan
        {
            SchemaVersion = 1,
            GameSlug = slug,
            Route = route,
            Target = new PlanTarget
            {
                GamePath = context.GameDirectory,
                Engine = string.IsNullOrWhiteSpace(detection.Family) ? "unity_il2cpp" : detection.Family,
                EngineVersion = detection.Version,
                Bitness = detection.Bitness,
            },
            Confidence = confidence,
            PatchPoints = points,
            Artifacts = Il2CppTargets.BuildArtifacts(slug, points),
            Risks = BuildPlanRisks(layout, points),
            ManualTodo = manualTodo,
        };
    }

    /// <summary>async/状态机方法：Harmony 直接补会破坏 await（R21 实测 MethodAccessException 卡死）。</summary>
    private static bool IsAsyncTarget(Il2CppTarget target) =>
        target.MethodName.EndsWith("Async", StringComparison.Ordinal)
        || target.MethodName.StartsWith("Async", StringComparison.Ordinal)
        || target.MethodName.Contains("AsyncStateMachine", StringComparison.Ordinal);

    private static PatchPoint BuildHookPoint(int index, Il2CppTarget target, TextCandidate candidate, double score)
    {
        var scoreLabel = score.ToString("0.#", CultureInfo.InvariantCulture);
        var kindLabel = KindLabel(candidate.Kind);

        return new PatchPoint
        {
            Id = "hook-" + index.ToString("000", CultureInfo.InvariantCulture),
            Kind = PatchPointKind.Hook,
            Target = target.Target,
            CandidateId = candidate.Id,
            Confidence = Math.Round(score / 100.0, 4),
            Description = target.CSharpTypeName + "::" + target.MethodName + "（" + kindLabel + "，分 " + scoreLabel +
                          "）→ " + (candidate.Kind == TextCandidateKind.Dialogue
                              ? "读台词/说话人并交给 A11yRuntime"
                              : "读" + kindLabel + "文本并交给 A11yRuntime"),
        };
    }

    /// <summary>字段兜底补丁点：描述里带 <see cref="Il2CppFieldFallback.Marker"/>，生成器据此走原生字段读取分支。</summary>
    private static PatchPoint BuildFieldHookPoint(
        int index,
        Il2CppTarget target,
        TextCandidate candidate,
        IReadOnlyList<Il2CppTextField> fields)
    {
        return new PatchPoint
        {
            Id = "hook-" + index.ToString("000", CultureInfo.InvariantCulture),
            Kind = PatchPointKind.Hook,
            Target = target.Target,
            CandidateId = candidate.Id,
            Confidence = Math.Round(candidate.Score / 100.0, 4),
            Description = target.CSharpTypeName + "::" + target.MethodName + "（" + KindLabel(candidate.Kind) +
                          "，分 " + candidate.Score.ToString("0.#", CultureInfo.InvariantCulture) + "）→ " +
                          Il2CppFieldFallback.FormatFields(fields) + "（实例方法）",
        };
    }

    private static string KindLabel(TextCandidateKind kind) => kind switch
    {
        TextCandidateKind.Dialogue => "台词",
        TextCandidateKind.Choice => "选项",
        TextCandidateKind.Menu => "菜单",
        _ => "文本",
    };

    /// <summary>字段兜底要求声明类型能写进 typeof(...)（与 string 参数补丁同一限制）。</summary>
    private static bool CanGenerateFieldFallback(Il2CppTarget target)
    {
        var type = target.TypeFullName;
        return !type.Contains('`') && !type.Contains('<') && !type.Contains('/');
    }

    private static ManualTodoItem BuildEventTodo(Il2CppTarget target, TextCandidate candidate)
    {
        var args = target.ParameterTypes.FirstOrDefault() ?? "Il2CppSystem.Action";
        return new ManualTodoItem
        {
            Id = "M-003",
            Title = "文本事件通道（推荐但需人工订阅）：" + target.Display,
            Detail = "IL2CPP 下事件不能像 Mono 那样直接 +=，必须转换委托：" +
                     "var del = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<" +
                     args + ">(handler); " +
                     target.CSharpTypeName + ".add_" + target.MethodName["add_".Length..] + "(del); " +
                     "并且必须在引擎初始化完成后订阅。候选证据：" + string.Join(" / ", candidate.Evidence.Take(3)),
            Blocking = false,
        };
    }

    private static List<RiskItem> BuildPlanRisks(Il2CppLayout layout, IReadOnlyList<PatchPoint> points)
    {
        var risks = new List<RiskItem>
        {
            new()
            {
                Id = "R-301",
                Severity = RiskSeverity.Medium,
                Description = "补丁点全部是静态推断：IL2CPP 无法从字节码证明方法运行期收到台词，必须真机验证才会朗读。",
                Mitigation = "生成插件带空串过滤与去重；先跑 1 行确认再放量。",
            },
            new()
            {
                Id = "R-302",
                Severity = RiskSeverity.High,
                Description = "IL2CPP 版本漂移：游戏更新后 interop 缓存（assembly-hash.txt）失效，方法槽位与类型可能变化，补丁静默失效。",
                Mitigation = "插件启动时打印已解析的补丁点数；生成器记录 assembly-hash 到工程 README。",
            },
            new()
            {
                Id = "R-303",
                Severity = RiskSeverity.Low,
                Description = "生成工程不含任何游戏资源或台词文本，仅代码与配置；朗读内容在运行期从游戏内存读取。",
                Mitigation = "无需处置，保持不把样本文本写进证据文件。",
            },
        };

        if (points.Count == 0)
        {
            risks.Add(new RiskItem
            {
                Id = "R-304",
                Severity = RiskSeverity.High,
                Description = "没有任何自动补丁点：生成的工程只包含运行时与说明，不会朗读。",
                Mitigation = "先解决原料问题（interop/dump），或按 M-003 手工接入事件通道。",
            });
        }

        if (layout.BestSource == Il2CppSourceKind.Dump)
        {
            risks.Add(new RiskItem
            {
                Id = "R-305",
                Severity = RiskSeverity.High,
                Description = "dump.cs 路线生成的工程缺少游戏程序集引用，只能编译通过骨架（类型解析会在运行期失败）。",
                Mitigation = "路线已降为 SemiAutomatic；M-006 要求先装 BepInEx 6。",
            });
        }

        var hasDialogueEntry = points.Any(p =>
            p.Description.Contains("读台词/说话人", StringComparison.Ordinal) ||
            p.Description.Contains("文本属性链", StringComparison.Ordinal) ||
            p.Description.Contains("类型文本字段", StringComparison.Ordinal));
        if (points.Count > 0 && !hasDialogueEntry)
        {
            risks.Add(new RiskItem
            {
                Id = "R-306",
                Severity = RiskSeverity.High,
                Description = "计划里没有任何台词类入口（补丁点只读选项/菜单/元数据），正常剧情可能一句都不读。",
                Mitigation = "先补 interop 或 dump 让正文入口进入候选；或按 M-003 手工接入事件通道，装完后以 BepInEx\\LogOutput.log 为准。",
            });
        }

        return risks;
    }
}
