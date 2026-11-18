using GalA11yForge.Core.Models;
using GalA11yForge.Adapters.Il2Cpp.Internal;

namespace GalA11yForge.Adapters.Il2Cpp.Tests;

/// <summary>分析 → 计划 → 生成三段的可观察行为。全部用手搓元数据，不碰真实游戏。</summary>
public sealed class PlanningAndGenerationTests : IDisposable
{
    private readonly string _root;
    private readonly string _game;
    private readonly string _workspace;
    private readonly GameContext _context;
    private readonly Il2CppOptions _options;
    private readonly Il2CppAnalyzer _analyzer = new();
    private readonly Il2CppPlanner _planner = new();
    private readonly Il2CppGenerator _generator = new();

    public PlanningAndGenerationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gala11y-plan-" + Guid.NewGuid().ToString("N"));
        _game = Path.Combine(_root, "SiliconHeart");
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_game);
        Directory.CreateDirectory(_workspace);
        File.WriteAllBytes(Path.Combine(_game, "GameAssembly.dll"), [0x4D, 0x5A, 0x00, 0x00]);

        _context = Facts.Context(_game, _workspace);
        _options = Il2CppOptions.From(_context);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    private static EngineDetection Detection() => new()
    {
        Family = "unity_il2cpp",
        Version = "2021.3.15f1",
        Bitness = 64,
        Confidence = 0.95,
        Evidence = ["GameAssembly.dll"],
    };

    private AnalysisReport Analyze(Il2CppSourceKind source = Il2CppSourceKind.Interop)
    {
        var scan = Facts.NaninovelScan(source);
        return _analyzer.Analyze(_context, Detection(), _options, scan, null, CancellationToken.None);
    }

    [Fact]
    public void Analyze_RanksSafeNonAsyncChannelsFirst()
    {
        var report = Analyze();

        Assert.True(report.Candidates.Count >= 4);
        // R21 之后：async 的 PrintTextAsync 被打 -60 并压到 60 分以下，第一名应是安全的 set_Text 出口。
        Assert.Equal("Naninovel.UITextPrinter::set_Text", report.Candidates[0].Source.Split("!")[1].Split("(")[0]);
        Assert.Equal(100, report.Candidates[0].Score);

        var asyncCandidate = Assert.Single(report.Candidates, c => c.Source.Contains("PrintTextAsync", StringComparison.Ordinal));
        Assert.True(asyncCandidate.Score <= 60);
        Assert.Contains(asyncCandidate.Evidence, e => e.Contains("N9=-60", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_SkipsInterfaceProxies()
    {
        var report = Analyze();

        Assert.DoesNotContain(report.Candidates, c => c.Source.Contains("ITextPrinterManager", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_RecordsUnverifiedRuntimeAndScanFacts()
    {
        var report = Analyze();

        Assert.Contains(report.Unverified, u => u.Contains("运行时"));
        Assert.Contains(report.Coverage.Covered, c => c.Contains("interop"));
        Assert.Contains(report.Candidates[0].Evidence, e => e.StartsWith("签名：", StringComparison.Ordinal));
        Assert.Contains(report.Candidates[0].Evidence, e => e.Contains("已知通道"));
    }

    [Fact]
    public void Analyze_AppliesDumpCapAndTierDowngrade()
    {
        var report = Analyze(Il2CppSourceKind.Dump);

        Assert.NotEmpty(report.Candidates);
        Assert.All(report.Candidates, c => Assert.True(c.Score <= Il2CppScoring.DumpScoreCap));
    }

    [Fact]
    public void Plan_InteropSource_ProducesAutomaticWithConfirmationAndHooks()
    {
        var report = Analyze();
        var layout = Facts.InteropLayout(_game);

        var plan = _planner.Plan(_context, report, Detection(), _options, layout);

        Assert.Equal(PlanRoute.AutomaticWithConfirmation, plan.Route);
        Assert.NotEmpty(plan.PatchPoints);
        Assert.All(plan.PatchPoints, p => Assert.Equal(PatchPointKind.Hook, p.Kind));
        Assert.All(plan.PatchPoints, p => Assert.True(Il2CppTargets.TryParse(p.Target)?.CanGenerateHook == true));
        // R21：async 方法不许进自动补丁点，只留一条人工待办说明原因。
        Assert.DoesNotContain(plan.PatchPoints, p => p.Target.Contains("PrintTextAsync", StringComparison.Ordinal));
        Assert.Contains(plan.ManualTodo, m => m.Title.Contains("async 方法不能自动补", StringComparison.Ordinal));
        Assert.Contains(plan.ManualTodo, m => m.Title.Contains("OnPrintTextStarted"));
        Assert.Contains(plan.ManualTodo, m => m.Title.Contains("OnPrintTextStarted"));
        Assert.Contains(plan.Risks, r => r.Id == "R-301");
    }

    [Fact]
    public void Plan_DumpOnlyNeverEmitsHooks()
    {
        var report = Analyze(Il2CppSourceKind.Dump);
        var layout = Facts.DumpLayout(_game, "dump.cs");

        var plan = _planner.Plan(_context, report, Detection(), _options, layout);

        Assert.Equal(PlanRoute.SemiAutomatic, plan.Route);
        Assert.Empty(plan.PatchPoints);
        Assert.Contains(plan.ManualTodo, m => m.Blocking && m.Title.Contains("BepInEx 6"));
        Assert.Contains(plan.ManualTodo, m => m.Title.Contains("interop 后可自动接入"));
    }

    [Fact]
    public void Plan_NoCandidatesAtAll_IsHonestSemiAutomatic()
    {
        var empty = new AnalysisReport
        {
            GamePath = _game,
            Detection = Detection(),
        };

        var plan = _planner.Plan(_context, empty, Detection(), _options, Facts.InteropLayout(_game));

        Assert.Empty(plan.PatchPoints);
        Assert.NotEmpty(plan.ManualTodo);
        Assert.NotEmpty(plan.Risks);
    }

    [Fact]
    public void Generate_InteropPlan_WritesCompilableProjectWithTextParameter()
    {
        var report = Analyze();
        var plan = _planner.Plan(_context, report, Detection(), _options, Facts.InteropLayout(_game));
        var output = Path.Combine(_workspace, "out", "sh");

        var result = _generator.Generate(_context, plan, output, Facts.InteropLayout(_game), _options);

        Assert.Equal(output, result.ProjectDirectory);
        var names = result.Artifacts.Select(a => a.RelativePath).ToList();
        Assert.Contains("sh.il2cpp.a11y.csproj", names);
        Assert.Contains("Plugin.cs", names);
        Assert.Contains("A11y/A11yRuntime.cs", names);
        Assert.Contains("Hooks/Hook_001.cs", names);
        Assert.DoesNotContain(names, n => n.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(n));

        var hook = File.ReadAllText(Path.Combine(output, "Hooks", "Hook_001.cs"));
        // R21 之后 hook-001 是安全的 UITextPrinter::set_Text，不再是 async 的 PrintTextAsync。
        Assert.Contains("Naninovel.UITextPrinter", hook, StringComparison.Ordinal);
        Assert.DoesNotContain("PrintTextAsync", hook, StringComparison.Ordinal);
        Assert.Contains("A11y.A11yRuntime.OnDialogue(Channel,", hook, StringComparison.Ordinal);
        Assert.DoesNotContain("__result", hook);

        var csproj = File.ReadAllText(Path.Combine(output, "sh.il2cpp.a11y.csproj"));
        Assert.DoesNotContain("<RootNamespace>\n", csproj.Replace("\r\n", "\n"));
        Assert.DoesNotContain("<AssemblyName>\n", csproj.Replace("\r\n", "\n"));
        Assert.Contains("<TargetFramework>net6.0</TargetFramework>", csproj);
        Assert.Contains("BepInEx.Unity.IL2CPP", csproj);
        Assert.Contains("Il2CppInterop.Runtime", csproj);
        Assert.Contains("Elringus.Naninovel.Runtime", csproj);

        var plugin = File.ReadAllText(Path.Combine(output, "Plugin.cs"));
        Assert.Contains(GalA11yForge.Core.ForgeInfo.Version, plugin);
        Assert.Contains("gala11y.il2cpp.sh", plugin);
        Assert.Contains("BepInEx.Unity.IL2CPP", plugin);

        var runtime = File.ReadAllText(Path.Combine(output, "A11y", "A11yRuntime.cs"));
        Assert.Contains("IsPlaceholderText", runtime, StringComparison.Ordinal);
        Assert.Contains("占位", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_DumpPlan_WritesSkeletonWithoutHooks()
    {
        var report = Analyze(Il2CppSourceKind.Dump);
        var layout = Facts.DumpLayout(_game, "dump.cs");
        var plan = _planner.Plan(_context, report, Detection(), _options, layout);
        var output = Path.Combine(_workspace, "skeleton");

        var result = _generator.Generate(_context, plan, output, layout, _options);

        Assert.Contains("Plugin.cs", result.Artifacts.Select(a => a.RelativePath));
        Assert.DoesNotContain(result.Artifacts, a => a.RelativePath.StartsWith("Hooks/", StringComparison.Ordinal));
        var csproj = File.ReadAllText(Path.Combine(output, "sh.il2cpp.a11y.csproj"));
        Assert.Contains("只能编译骨架", csproj);
        Assert.Equal("Dump", result.Metadata["source_kind"]);
    }

    [Fact]
    public void Generate_IsIdempotentWhenDirectoryAlreadyExists()
    {
        var report = Analyze();
        var plan = _planner.Plan(_context, report, Detection(), _options, Facts.InteropLayout(_game));
        var output = Path.Combine(_workspace, "twice");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "stale.txt"), "old");

        var first = _generator.Generate(_context, plan, output, Facts.InteropLayout(_game), _options);
        var second = _generator.Generate(_context, plan, output, Facts.InteropLayout(_game), _options);

        Assert.Equal(first.Artifacts.Count, second.Artifacts.Count);
        Assert.Equal(first.Metadata["hook_count"], second.Metadata["hook_count"]);
        Assert.NotEqual("0", first.Metadata["hook_count"]);
    }

    [Fact]
    public void Analyze_MethodWithoutStringParameter_ReportsFieldFallbackEvidence()
    {
        // 0.2.12 的泛化缺口：方法没有 string 形参，但实例类型上有文本字段
        // （GalMaster / Lilith 都是这个形状）。分析报告必须把字段写进证据，供计划器接字段兜底。
        var scan = new Il2CppScanOutcome
        {
            Source = Il2CppSourceKind.Interop,
            Layout = Facts.InteropLayout(_game),
        };
        scan.Types["Game.DialogueUI"] = Facts.Type(
            "Game.DialogueUI",
            isUi: true,
            textMembers: ["_dialogueText:TextMeshProUGUI", "_speakerText:Text", "_diag:String"]);
        scan.Methods.Add(Facts.Method("Game.DialogueUI", "ShowText", Facts.Param("node", "Game.DialogueNode")));
        scan.TypeCount = scan.Types.Count;
        scan.MethodCount = scan.Methods.Count;

        var report = _analyzer.Analyze(_context, Detection(), _options, scan, null, CancellationToken.None);
        var candidate = Assert.Single(report.Candidates.Where(c => c.Source.Contains("ShowText", StringComparison.Ordinal)));

        Assert.Contains(candidate.Evidence, e =>
            e.StartsWith("类型文本字段：", StringComparison.Ordinal)
            && e.Contains("_dialogueText:TextMeshProUGUI", StringComparison.Ordinal));
        Assert.Contains(candidate.Evidence, e => e.Contains("（实例方法）", StringComparison.Ordinal));
        Assert.Contains(candidate.Evidence, e => e.Contains("可字段兜底", StringComparison.Ordinal));
        // 0.2.12 真机教训：LayeredWindowPresenter._diag 会把 “not started” 当台词读，必须从字段证据里剔掉。
        Assert.DoesNotContain(candidate.Evidence, e => e.Contains("_diag", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_DecorativeHostType_IsNotFieldFallbackCandidate()
    {
        // 0.2.12 真机：GiftCellView._text 把“一个饰品的名字”当台词读、LilithFolderView 读界面字。
        // 装饰性容器（格子/列表项/文件夹…）上的文本不应成为字段兜底入口。
        var scan = new Il2CppScanOutcome
        {
            Source = Il2CppSourceKind.Interop,
            Layout = Facts.InteropLayout(_game),
        };
        scan.Types["Game.GiftCellView"] = Facts.Type(
            "Game.GiftCellView",
            isUi: true,
            textMembers: ["_text:Text"]);
        scan.Methods.Add(Facts.Method("Game.GiftCellView", "SetRootVisible"));
        scan.TypeCount = scan.Types.Count;
        scan.MethodCount = scan.Methods.Count;

        var report = _analyzer.Analyze(_context, Detection(), _options, scan, null, CancellationToken.None);

        Assert.DoesNotContain(report.Candidates, c => c.Source.Contains("GiftCellView", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_CapsFieldFallbackHooksAtTwo()
    {
        // 0.2.15 真机：全库扫描时字段兜底候选一多，界面工具类（MemoFullView/LocalizedText）
        // 会占满 6 个名额。自动方案只放前 2 个，其余进人工待办。
        TextCandidate FieldCandidate(string id, string source) => new()
        {
            Id = id,
            Kind = TextCandidateKind.Dialogue,
            Source = source,
            Score = 52,
            Evidence =
            [
                "类型文本字段：_dialogueText:TextMeshProUGUI（实例方法）",
                "字段兜底加权：规则分补到 52 分（B 级）",
            ],
        };

        var report = new AnalysisReport
        {
            SchemaVersion = 1,
            GamePath = _game,
            Detection = Detection(),
            Candidates =
            [
                FieldCandidate("tc-001", "il2cpp:Game.dll!Game.DialogueBubbleUI::ShowNode(Game.DialogueNode|node)"),
                FieldCandidate("tc-002", "il2cpp:Game.dll!Game.LocalizedText::RefreshText()"),
                FieldCandidate("tc-003", "il2cpp:Game.dll!Game.MemoFullView::RefreshAll()"),
                FieldCandidate("tc-004", "il2cpp:Game.dll!Game.MemoFullView::RefreshChrome()"),
            ],
        };

        var plan = _planner.Plan(_context, report, Detection(), _options, Facts.InteropLayout(_game));

        var fieldPoints = plan.PatchPoints
            .Where(p => p.Description.Contains("类型文本字段：", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, fieldPoints.Count);
        Assert.Contains(plan.ManualTodo, t => t.Title.Contains("字段兜底名额已满", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_WithOnlyMenuHookPoints_RaisesR306()
    {
        // 0.2.65：只有菜单/选项类钩子时，计划要提醒「可能一句剧情都不读」。
        var report = new AnalysisReport
        {
            SchemaVersion = 1,
            GamePath = _game,
            Detection = Detection(),
            Candidates =
            [
                new TextCandidate
                {
                    Id = "tc-001",
                    Kind = TextCandidateKind.Menu,
                    Source = "il2cpp:Game.dll!Game.OptionsUI::OpenSettings(System.String|text)",
                    Score = 80,
                    Evidence = ["签名：[Game.dll] Game.OptionsUI::OpenSettings(System.String)"],
                },
            ],
        };

        var layout = Facts.InteropLayout(_game);
        var plan = _planner.Plan(_context, report, Detection(), _options, layout);

        Assert.NotEmpty(plan.PatchPoints);
        Assert.DoesNotContain(plan.PatchPoints, p => p.Description.Contains("台词", StringComparison.Ordinal));
        var risk = Assert.Single(plan.Risks, r => r.Id == "R-306");
        Assert.Equal(RiskSeverity.High, risk.Severity);
        Assert.Contains("台词类入口", risk.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_And_Generate_FieldFallbackHook_ProducesNativeFieldReader()
    {
        // 计划器看到「字段证据」就生成字段兜底补丁点；生成器据此写原生字段读取器，
        // 不写死 offset（Lilith 实测 TMP 字段 offset 返回 -1，只能按名查）。
        const string source = "il2cpp:Game.dll!Game.DialogueUI::ShowDialogue(Game.DialogueNode|node)";
        var report = new AnalysisReport
        {
            SchemaVersion = 1,
            GamePath = _game,
            Detection = Detection(),
            Candidates =
            [
                new TextCandidate
                {
                    Id = "tc-001",
                    Kind = TextCandidateKind.Dialogue,
                    Source = source,
                    Score = 80,
                    Evidence =
                    [
                        "签名：[Game.dll] Game.DialogueUI::ShowDialogue(Game.DialogueNode)",
                        "类型文本字段：_dialogueText:TextMeshProUGUI（实例方法）",
                    ],
                },
            ],
        };

        var layout = Facts.InteropLayout(_game);
        var plan = _planner.Plan(_context, report, Detection(), _options, layout);
        var point = Assert.Single(plan.PatchPoints, p => p.Description.Contains("类型文本字段：", StringComparison.Ordinal));
        Assert.Equal(PatchPointKind.Hook, point.Kind);
        Assert.Contains(plan.Artifacts, a => a.RelativePath == Path.Combine("A11y", "A11yFieldReader.cs"));

        var output = Path.Combine(_workspace, "field-hook");
        var result = _generator.Generate(_context, plan, output, layout, _options);

        var hook = File.ReadAllText(Path.Combine(output, "Hooks", "Hook_001.cs"));
        Assert.Contains("A11yFieldReader.ReadAndSpeak", hook, StringComparison.Ordinal);
        Assert.Contains("__instance", hook, StringComparison.Ordinal);
        Assert.Contains("typeof(global::TMPro.TextMeshProUGUI)", hook, StringComparison.Ordinal);

        var reader = File.ReadAllText(Path.Combine(output, "A11y", "A11yFieldReader.cs"));
        Assert.Contains("il2cpp_class_get_field_from_name", reader, StringComparison.Ordinal);
        Assert.Contains("il2cpp_field_get_value_object", reader, StringComparison.Ordinal);

        var csproj = File.ReadAllText(Path.Combine(output, "sh.il2cpp.a11y.csproj"));
        Assert.Contains("Unity.TextMeshPro", csproj, StringComparison.Ordinal);
        // 字段补丁的 __instance 可能继承 UIBehaviour（Naninovel ChatMessage 实测），必须带 UGUI 引用。
        Assert.Contains("UnityEngine.UI", csproj, StringComparison.Ordinal);
        Assert.Equal("1", result.Metadata["field_fallback_hooks"]);
    }

    [Fact]
    public void Plan_UnnamedStringParameter_DoesNotBecomeFieldHook()
    {
        // 0.2.13 真机发现的坑：il2cpp 扫描能给出「System.String|（形参无名）」，
        // 这种目标不能声明 Harmony 参数，但它本来就在写字符串，不是“字段兜底”候选。
        // 如果把它当字段补丁，会生成注定编译不过的强类型 __instance 补丁。
        const string source = "il2cpp:Game.dll!Game.DialogueUI::set_Text(System.String|)";
        var report = new AnalysisReport
        {
            SchemaVersion = 1,
            GamePath = _game,
            Detection = Detection(),
            Candidates =
            [
                new TextCandidate
                {
                    Id = "tc-001",
                    Kind = TextCandidateKind.Dialogue,
                    Source = source,
                    Score = 75,
                    Evidence =
                    [
                        "签名：[Game.dll] Game.DialogueUI::set_Text(System.String)",
                        "类型文本字段：_dialogueText:TextMeshProUGUI（实例方法）",
                    ],
                },
            ],
        };

        var plan = _planner.Plan(_context, report, Detection(), _options, Facts.InteropLayout(_game));
        Assert.DoesNotContain(plan.PatchPoints, p => p.Target.Contains("set_Text", StringComparison.Ordinal));
        Assert.Contains(plan.ManualTodo, t => t.Title.Contains("人工接入通道", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Artifacts, a => a.RelativePath == Path.Combine("A11y", "A11yFieldReader.cs"));
    }
}
