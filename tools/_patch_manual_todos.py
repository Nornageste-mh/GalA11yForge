import pathlib

# --- fix outdated CLI flag text ---
p = pathlib.Path(r'D:\DSHWorkSpace\src\Forge.Build\PluginDeployer.cs')
s = p.read_text(encoding='utf-8')
old = '或用 --bepinex-package 指定：'
new = '或用 --bepinex <zip> 指定：'
assert old in s
s = s.replace(old, new, 1)
p.write_text(s, encoding='utf-8')
print('PluginDeployer flag text fixed')

# --- dynamic manual todos / risks in UnityMonoPlanner ---
p = pathlib.Path(r'D:\DSHWorkSpace\src\Forge.Adapters.UnityMono\Internal\UnityMonoPlanner.cs')
s = p.read_text(encoding='utf-8')
orig = s

s = s.replace('            Risks = BuildRisks(analysis),\n            ManualTodo = BuildManualTodo(analysis, parseIssues),',
              '            Risks = BuildRisks(analysis, selected),\n            ManualTodo = BuildManualTodo(analysis, parseIssues, selected),', 1)

old_risk = '''    private static List<RiskItem> BuildRisks(AnalysisReport analysis)
    {
        var risks = new List<RiskItem>
        {
            new()
            {
                Id = "R-101",
                Severity = RiskSeverity.High,
                Description = "静态计划未验证运行期调用顺序/参数非空；DOText→HandleMessageText→AnimateText 需在 manual 模式复核。",
                Mitigation = "首轮只开 manual 模式，确认每个 Hook 命中后再开自动朗读。",
            },'''
new_risk = '''    private static List<RiskItem> BuildRisks(AnalysisReport analysis, IReadOnlyList<UnityMonoCandidateView> selected)
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
            },'''
assert old_risk in s, 'risk block not found'
s = s.replace(old_risk, new_risk, 1)

old_todo = '''    private static List<ManualTodoItem> BuildManualTodo(AnalysisReport analysis, List<string> parseIssues)
    {
        var todo = new List<ManualTodoItem>
        {
            new()
            {
                Id = "M-001",
                Title = "运行期 manual 模式复核 Hook 命中",
                Detail = "确认 DOText（每句入口）、HandleMessageText（消息框）、AnimateText（主通道）、Choice（选项）均被调用，且文本参数非空。",
                Blocking = true,
            },
            new()
            {
                Id = "M-002",
                Title = "确认选项回显与去重窗口",
                Detail = "Choice 的选项文本/回显延迟需实测；按 (num,hash) 设短窗去重，避免重复朗读。",
                Blocking = true,
            },
            new()
            {
                Id = "M-003",
                Title = "确认 language 层与文本来源",
                Detail = "明确 AnimateText（简中）与 DOText/原稿层（日文）的配对策略；在 profile 显式写 sc|tc|ja|en。",
                Blocking = false,
            },
            new()
            {
                Id = "M-004",
                Title = "核对生成器的 Hook 目标解析",
                Detail = "生成器只接受基础类型参数；若运行期报同名重载歧义，按完整签名人工指定。",
                Blocking = false,
            },
        };'''
new_todo = '''    private static List<ManualTodoItem> BuildManualTodo(
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
                Detail = $"确认本计划入口按预期被调用且能拿到文本：{targets}。只报命中不朗读时，检查入口有无字符串参数或实例文本字段。",
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
        });'''
assert old_todo in s, 'todo block not found'
s = s.replace(old_todo, new_todo, 1)

# add TargetLabel helper before CompareViews
anchor = '    private static int CompareViews(UnityMonoCandidateView left, UnityMonoCandidateView right)'
helper = '''    private static string TargetLabel(UnityMonoCandidateView view)
    {
        var type = view.TypeFullName;
        var dot = type.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < type.Length)
        {
            type = type[(dot + 1)..];
        }

        return type + "::" + view.MethodName;
    }

'''
assert anchor in s
s = s.replace(anchor, helper + anchor, 1)

p.write_text(s, encoding='utf-8')
print('UnityMonoPlanner patched:', len(orig), '->', len(s))
