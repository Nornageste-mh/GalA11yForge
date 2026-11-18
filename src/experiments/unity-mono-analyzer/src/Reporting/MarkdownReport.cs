using System.Globalization;
using System.Text;
using UnityMonoAnalyzer.Analysis;

namespace UnityMonoAnalyzer.Reporting;

/// <summary>Markdown 报告（中文、人读）。不含任何游戏原文台词。</summary>
public static class MarkdownReport
{
    public static string Render(AnalysisResult r, int topN = 20)
    {
        var sb = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;
        var primarySinks = r.TextSinks.Where(s => s.Role == "primary").ToList();
        var ignoredSinks = r.TextSinks.Where(s => s.Role != "primary").ToList();
        var top = r.Candidates.Take(Math.Max(1, topN)).ToList();

        // 建议优先复核：A/B 级里排除系统 UI、示例代码、本地化刷新后的前 3 条。
        // 依据：本轮实测中这些标记精确命中了"启动器/确认框/换语言刷新"造成的头名污染（见 ReviewFlags 注释）。
        var shortlist = top
            .Where(c => !ReviewFlags.HasNoise(c.Flags) && (c.Tier == "A" || c.Tier == "B"))
            .OrderByDescending(c => c.Score).ThenByDescending(c => c.TextSinkCalls)
            .Take(3).ToList();

        sb.AppendLine("# Unity Mono 文本钩子静态分析报告");
        sb.AppendLine();
        sb.AppendLine($"> 工具：{r.Run.Tool} {r.Run.ToolVersion}（Mono.Cecil {r.Run.CecilVersion}） · schema {r.SchemaVersion}");
        sb.AppendLine($"> 生成时间（UTC）：{r.Run.StartedAtUtc}");
        sb.AppendLine($"> 复现命令：`{Esc(r.Run.CommandLine)}`");
        sb.AppendLine();

        // ---------- 1. 结论摘要 ----------
        sb.AppendLine("## 1. 结论摘要");
        sb.AppendLine();
        sb.AppendLine($"- 游戏：{(r.Game.Name.Length > 0 ? r.Game.Name : "（未命名）")}");
        sb.AppendLine($"- 游戏目录：`{Esc(r.Game.GameDir)}`");
        sb.AppendLine($"- 数据目录：`{Esc(r.Game.DataDirName)}`（`{Esc(r.Game.DataDir)}`）");
        sb.AppendLine(r.Game.ExePath.Length > 0
            ? $"- 启动入口：`{Esc(Path.GetFileName(r.Game.ExePath))}`（{Esc(r.Game.ExeReason)}）"
            : "- 启动入口：未识别（目录内没有可用 exe）");
        sb.AppendLine(r.Unity.Version.Length > 0
            ? $"- Unity 版本：{r.Unity.Version}（来源 {r.Unity.VersionSource}）"
            : "- Unity 版本：未识别");
        sb.AppendLine($"- 程序集：`{Esc(Path.GetFileName(r.Assembly.Path))}` {Size(r.Assembly.SizeBytes)}（sha256 `{r.Assembly.Sha256[..Math.Min(12, r.Assembly.Sha256.Length)]}…`）");
        sb.AppendLine($"- 规模：{r.Stats.Types.ToString("N0", culture)} 类型 / {r.Stats.Methods.ToString("N0", culture)} 方法（{r.Stats.MethodsWithBody.ToString("N0", culture)} 有方法体）/ {r.Stats.LdstrTotal.ToString("N0", culture)} 条 ldstr");
        sb.AppendLine($"- 文本写入点：{r.Stats.TextSinkCallSites} 处，来自 {r.Stats.TextSinkCallerMethods} 个方法" +
                      (primarySinks.Count > 0 ? $"（{string.Join("、", primarySinks.Select(s => $"{s.Api}×{s.Calls}"))}）" : ""));
        sb.AppendLine($"- 框架痕迹：{(r.FrameworkTraces.Count == 0 ? "无" : string.Join("、", r.FrameworkTraces.Select(f => $"{f.DisplayName}（{f.TypeCount} 类型）")))}");
        sb.AppendLine($"- 候选分层：A {r.Stats.TierA} / B {r.Stats.TierB} / C {r.Stats.TierC}（入选 {r.Stats.CandidatesIncluded} 条，低于阈值 {r.Stats.BelowThreshold} 条；共打分 {r.Stats.CandidatesScored} 条）");
        sb.AppendLine($"- CJK 字面量：{(r.Stats.CjkGame ? $"{r.Stats.CjkLiteralMethods} 个方法命中" : "未检出（英文游戏或文本在资源内）")}");
        sb.AppendLine($"- 分析耗时：{r.Timing.TotalMs} ms（发现 {r.Timing.DiscoveryMs} / 候选扫描 {r.Timing.CandidateScanMs} / 全量扫描 {r.Timing.FullScanMs} / 打分 {r.Timing.ScoringMs}）");
        var first = shortlist.FirstOrDefault() ?? top.FirstOrDefault(c => c.Tier == "A") ?? top.FirstOrDefault(c => c.Tier == "B") ?? top.FirstOrDefault();
        sb.AppendLine(first != null
            ? $"- **建议优先确认**：`{Esc(first.MethodFullName)}`（{first.Tier} 级 {Fmt(first.Score)} 分，{first.Kind}）"
            : "- **建议优先确认**：无候选");
        sb.AppendLine($"- 退出码：{r.ExitCode}（0 成功 / 4 不是 Unity Mono / 5 部分成功）");
        sb.AppendLine();

        // ---------- 2. 候选表 ----------
        sb.AppendLine($"## 2. 候选钩子点（Top {top.Count}）");
        sb.AppendLine();
        if (top.Count == 0)
        {
            sb.AppendLine("> 没有候选达到 C 级阈值（30 分）。请查看 §7 覆盖声明与 §9 告警。");
        }
        else
        {
            sb.AppendLine("| # | 级别 | 分数 | 置信 | 类型 | 方法 | 写入点 | 标记 | 证据摘要 |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var c in top)
            {
                sb.AppendLine($"| {c.Rank} | {c.Tier} | {Fmt(c.Score)} | {c.Confidence} | {Esc(c.Kind)} | `{Esc(c.MethodFullName)}` | {c.TextSinkCalls} | {Esc(string.Join(",", c.Flags))} | {Esc(Summarize(c))} |");
            }
        }
        sb.AppendLine();
        sb.AppendLine("> 级别：A≥70 且 S1>0（可生成 Hook 草案 + 运行期回放）；B≥50（降级，人工勾选）；C≥30（只记录观察）。");
        sb.AppendLine("> 分数是静态启发式，不等于可 Hook 性；A 级同样必须过 §8 的运行期确认。");
        sb.AppendLine();

        // ---------- 2.1 建议优先复核 ----------
        sb.AppendLine("### 2.1 建议优先复核（已排除系统 UI / 示例代码 / 本地化刷新）");
        sb.AppendLine();
        if (shortlist.Count == 0)
        {
            sb.AppendLine("> 全部 A/B 候选都带 `system-ui`/`sample-code`/`i18n-refresh` 标记——说明本作的头名可能是菜单或换语言刷新，"
                          + "不要直接照 Top1 下 Hook，请从 §3 的规则得分与调用者人工挑。");
        }
        else
        {
            sb.AppendLine("| # | 级别 | 分数 | 方法 | 标记 | 为什么被挑出来 |");
            sb.AppendLine("|---|---|---|---|---|---|");
            int i = 1;
            foreach (var c in shortlist)
            {
                var why = c.Flags.Count == 0 ? "无噪声标记，直接写入文本" : string.Join("；", c.Flags.Select(Explain));
                sb.AppendLine($"| {i++} | {c.Tier} | {Fmt(c.Score)} | `{Esc(c.MethodFullName)}` | {Esc(string.Join(",", c.Flags))} | {Esc(why)} |");
            }
        }
        sb.AppendLine();
        sb.AppendLine("> 标记含义：" + string.Join("；", new[]
        {
            ReviewFlags.SystemUi + "=确认框/设置/存档/暂停等系统 UI",
            ReviewFlags.I18nRefresh + "=换语言/初始化时整窗重写",
            ReviewFlags.SampleCode + "=框架自带 Examples/Samples",
            ReviewFlags.FrameworkInternal + "=框架内部实现（游戏侧入口通常在其上游）",
            ReviewFlags.MultiWrite + "=一处方法多次写文本",
            ReviewFlags.HardcodedText + "=全是硬编码字面量",
            ReviewFlags.UpstreamOnly + "=只在调用链上游包了一层",
        }) + "。标记只做证据标注，**不参与** R4 打分。");
        sb.AppendLine();

        // ---------- 3. 候选详情 ----------
        var detail = top.Take(8).ToList();
        sb.AppendLine($"## 3. 候选详情（前 {detail.Count} 条）");
        sb.AppendLine();
        foreach (var c in detail)
        {
            sb.AppendLine($"### #{c.Rank} `{Esc(c.ShortTypeName())}` · {c.Tier} 级 · {Fmt(c.Score)} 分 · {Esc(c.Kind)} · 置信 {c.Confidence}");
            sb.AppendLine();
            sb.AppendLine($"- 方法全名：`{Esc(c.MethodFullName)}`");
            sb.AppendLine($"- 签名：`{Esc(c.Signature)}`（{c.TextSinkCalls} 处文本写入，来源 {Esc(c.ValueSource)}）");
            sb.AppendLine($"- 发现途径：{Esc(c.DiscoveredBy)}；框架：{(c.Framework.Length > 0 ? Esc(c.Framework) : "无")}");
            var rules = string.Join(" / ", c.RuleScores.Where(kv => kv.Value != 0).Select(kv => $"{kv.Key} {(kv.Value > 0 ? "+" : "")}{Fmt(kv.Value)}"));
            sb.AppendLine($"- 规则得分：{(rules.Length > 0 ? rules : "全部未命中")}");
            foreach (var e in c.Evidence) sb.AppendLine($"  - {Esc(e)}");
            sb.AppendLine($"- 调用者（{c.CallerCount}）：{(c.CallerSamples.Count == 0 ? "无（入口方法或事件回调）" : string.Join("、", c.CallerSamples.Take(4).Select(x => $"`{Esc(x)}`")))}");
            if (c.TextFieldCount > 0) sb.AppendLine($"- 所属类型持有文本字段：{c.TextFieldCount} 个");
            sb.AppendLine();
        }

        // ---------- 4. 文本写入 API ----------
        sb.AppendLine("## 4. 识别到的文本写入 API");
        sb.AppendLine();
        sb.AppendLine("| API | 调用次数 | 调用者方法数 | 是否参与打分 |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var s in r.TextSinks)
            sb.AppendLine($"| `{Esc(s.Api)}` | {s.Calls} | {(s.Role == "primary" ? s.CallerMethods.ToString(culture) : "—")} | {(s.Role == "primary" ? "是（S3）" : "否（仅统计）")} |");
        if (r.TextSinks.Count == 0) sb.AppendLine("| （无） | 0 | — | — |");
        sb.AppendLine();
        if (ignoredSinks.Count > 0)
            sb.AppendLine($"> 非台词 setter（{string.Join("、", ignoredSinks.Select(s => s.Api).Distinct().Take(6))}）只统计不参与打分，避免输入框/下拉框文本污染候选。");
        sb.AppendLine();

        // ---------- 4.1 文本写入最集中的类型 ----------
        if (r.TextSinkHotspots.Count > 0)
        {
            sb.AppendLine("### 4.1 文本写入最集中的类型（Top 12）");
            sb.AppendLine();
            sb.AppendLine("> 这里统计**全部** set_text 调用点（不限于候选），用来回答\"文本到底从哪个类流出\"。");
            sb.AppendLine("> 候选漏掉的主对话类常常出现在这张表里，值得对照 §2 检查是否漏报。");
            sb.AppendLine();
            sb.AppendLine("| 类型 | 写入点 | 方法数 | 示例方法 |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var h in r.TextSinkHotspots.Take(12))
                sb.AppendLine($"| `{Esc(h.TypeFullName)}` | {h.Calls} | {h.Methods} | `{Esc(h.SampleMethod)}` |");
            sb.AppendLine();
        }

        // ---------- 5. 框架痕迹 ----------
        sb.AppendLine("## 5. 对话框架痕迹");
        sb.AppendLine();
        if (r.FrameworkTraces.Count == 0)
        {
            sb.AppendLine("未命中已知对话框架表，也没有 `*Dialog*`/`*Talk*` 命名。");
        }
        else
        {
            sb.AppendLine("| 框架 | 类别 | 类型数 | 方法数 | 样例类型 |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var f in r.FrameworkTraces)
                sb.AppendLine($"| {Esc(f.DisplayName)} | {(f.Category == "known" ? "已知框架" : "自定义命名")} | {f.TypeCount} | {f.MethodCount} | {string.Join("<br>", f.SampleTypes.Take(3).Select(t => $"`{Esc(t)}`"))} |");
        }
        sb.AppendLine();

        // ---------- 6. 候选数据目录排名 ----------
        if (r.Discovery.Candidates.Count > 1)
        {
            sb.AppendLine("## 6. 候选数据目录排名");
            sb.AppendLine();
            sb.AppendLine("| 选择 | 数据目录 | 深度 | 分数 | 类型/方法 | 写入方法 | CJK 方法 | 框架类型 | 理由 |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var c in r.Discovery.Candidates)
            {
                sb.AppendLine($"| {(c.Selected ? "✅" : "")} | `{Esc(c.DataDirName)}` | {c.Depth} | {Fmt(c.RankScore)} | {c.QuickMetrics.Types}/{c.QuickMetrics.Methods} | {c.QuickMetrics.TextSinkCallerMethods} | {c.QuickMetrics.CjkLiteralMethods} | {c.QuickMetrics.KnownFrameworkTypes} | {Esc(c.RankReason)} |");
            }
            sb.AppendLine();
            sb.AppendLine("> 启动器的文本写入点常常多于真游戏（research/01-norna 教训），因此排序权重里框架/对话命名/CJK 远高于写入点数量。");
            sb.AppendLine();
        }

        // ---------- 7. 覆盖声明 ----------
        int section = r.Discovery.Candidates.Count > 1 ? 7 : 6;
        sb.AppendLine($"## {section}. 覆盖声明");
        sb.AppendLine();
        sb.AppendLine("### 能分析");
        foreach (var x in r.Coverage.Can) sb.AppendLine($"- {x}");
        sb.AppendLine();
        sb.AppendLine("### 不能分析（明确的盲区）");
        foreach (var x in r.Coverage.Cannot) sb.AppendLine($"- {x}");
        sb.AppendLine();
        var inapplicable = r.Coverage.Dimensions.Where(d => !d.Applicable).ToList();
        if (inapplicable.Count > 0)
        {
            sb.AppendLine("### 本作不适用的打分维度");
            foreach (var d in inapplicable) sb.AppendLine($"- {d.Id}（{(d.Points > 0 ? "+" : "")}{d.Points}）：{d.Reason}");
            sb.AppendLine();
        }

        // ---------- 8. 人工确认清单 ----------
        sb.AppendLine($"## {section + 1}. 人工确认清单（运行期必做，静态拿不到）");
        sb.AppendLine();
        foreach (var x in r.Coverage.RuntimeConfirmations) sb.AppendLine($"- [ ] {x}");
        sb.AppendLine();
        sb.AppendLine("### 本作专项检查项");
        foreach (var line in SpecialChecks(r)) sb.AppendLine($"- [ ] {line}");
        sb.AppendLine();

        // ---------- 9. 备注与告警 ----------
        sb.AppendLine($"## {section + 2}. 备注与告警");
        sb.AppendLine();
        if (r.Warnings.Count == 0 && r.Notes.Count == 0) sb.AppendLine("- 无");
        foreach (var w in r.Warnings) sb.AppendLine($"- ⚠️ {w}");
        foreach (var n in r.Notes) sb.AppendLine($"- ℹ️ {n}");
        foreach (var e in r.Errors) sb.AppendLine($"- ❌ {e}");
        sb.AppendLine();

        // ---------- 10. 复现 ----------
        sb.AppendLine($"## {section + 3}. 复现与产物");
        sb.AppendLine();
        sb.AppendLine("```pwsh");
        sb.AppendLine("# 在 src\\experiments\\unity-mono-analyzer 目录下");
        sb.AppendLine($"D:\\dotnet\\dotnet.exe run -- --game \"{r.Game.InputPath}\"" +
                      (r.Game.ExePath.Length > 0 && r.Discovery.SelectionMode is "explicit" or "explicit-assembly" ? $" --exe \"{r.Game.ExePath}\"" : "") +
                      $" --out <输出目录>");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine($"- 机器可读产物：`analysis.json`（schemaVersion {r.SchemaVersion}，字段见 README）");
        sb.AppendLine($"- 程序集 sha256：`{r.Assembly.Sha256}`");
        sb.AppendLine($"- 候选上限：{r.Stats.CandidatesIncluded} 条入选 / {r.Stats.BelowThreshold} 条低于阈值未展开");
        sb.AppendLine();
        return sb.ToString();
    }

    private static IEnumerable<string> SpecialChecks(AnalysisResult r)
    {
        var primary = r.TextSinks.Where(s => s.Role == "primary").ToList();
        bool tmp = primary.Any(s => s.Api.Contains("TMP_Text", StringComparison.Ordinal));
        bool ugui = primary.Any(s => s.Api.Contains("UI.Text", StringComparison.Ordinal));
        if (tmp && ugui) yield return "本作同时存在 TMP 与 uGUI.Text 两条写入路径：必须两条都 Hook 并按内容去重（否则会漏句/重复朗读）";
        else if (tmp) yield return "本作只检出 TMP 写入路径：确认是否有 uGUI.Text 面板（设置/存档）被遗漏";
        else if (ugui) yield return "本作只检出 uGUI.Text 写入路径：确认是否使用 TMP 但纹理/字体走自绘";

        var top = r.Candidates.FirstOrDefault(c => c.Tier == "A") ?? r.Candidates.FirstOrDefault();
        if (top != null) yield return $"从候选 `{top.MethodFullName}` 下断点，确认一次对话会产生几次 set_text 调用";
        if (r.Stats.CjkGame) yield return "确认 CJK 字面量所在的候选是否就是台词路径（有些只是界面按钮文案）";
        else yield return "本作无 CJK 字面量：说明台词来自资源文件，静态打分只能靠框架/命名，可靠性下降";
        if (r.Game.Deployed) yield return $"游戏目录已有注入痕迹（{string.Join(", ", r.Game.DeployedEvidence.Take(3))}），需确认是否与本工具产物冲突";
        if (r.Stats.UpstreamCandidateMethods > 0) yield return $"存在 {r.Stats.UpstreamCandidateMethods} 个语义上游候选：确认 Hook 上游（一次拿到完整句子）还是 Hook setter（拿最终渲染文本）";
        yield break;
    }

    private static string Summarize(TextCandidate c)
    {
        var parts = new List<string>();
        var fired = c.RuleScores.Where(kv => kv.Value != 0).Select(kv => kv.Key).ToList();
        if (fired.Count > 0) parts.Add(string.Join("+", fired));
        if (c.Framework.Length > 0) parts.Add(c.Framework);
        if (c.CjkLiteralCount > 0) parts.Add($"CJK×{c.CjkLiteralCount}");
        parts.Add($"来源 {c.ValueSource}");
        return string.Join("；", parts);
    }

    private static string Explain(string flag) => flag switch
    {
        ReviewFlags.SystemUi => "系统 UI（不是主线对话）",
        ReviewFlags.I18nRefresh => "换语言/初始化整窗刷新（运行期会成批假事件）",
        ReviewFlags.SampleCode => "框架示例代码（可能未被游戏使用）",
        ReviewFlags.FrameworkInternal => "框架内部实现，游戏侧入口在其上游",
        ReviewFlags.MultiWrite => "同一方法多次写文本（需去重）",
        ReviewFlags.HardcodedText => "硬编码字面量（不来自对话数据）",
        ReviewFlags.UpstreamOnly => "调用链上游包装方法",
        _ => flag,
    };

    private static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Size(long bytes) => bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024.0:0.#} KB";

    private static string Esc(string s) => (s ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string ShortTypeName(this TextCandidate c)
    {
        var t = c.TypeFullName;
        var i = t.LastIndexOf('.');
        if (i >= 0) t = t[(i + 1)..];
        return $"{t}.{c.MethodName}";
    }
}
