using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Adapters.Renpy.Internal;

/// <summary>
/// 步骤二：只读分析文本管线。核心结论是“Ren'Py 有一个引擎级统一钩子
/// config.say_menu_text_filter”，因此不需要解析 .rpa，也不需要改归档。
/// </summary>
internal sealed class RenpyAnalyzer
{
    public Task<ForgeResult<AnalysisReport>> AnalyzeAsync(
        GameContext context,
        EngineDetection detection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.GameDirectory))
        {
            return Task.FromResult(ForgeResult<AnalysisReport>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                "游戏目录为空，无法分析 Ren'Py 文本管线。",
                "用 --game 指定游戏根目录。"));
        }

        if (detection is null || string.IsNullOrWhiteSpace(detection.Family))
        {
            return Task.FromResult(ForgeResult<AnalysisReport>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                "缺少引擎识别结果，无法分析。",
                "先运行 detect，再把结果传给 analyze。"));
        }

        if (!string.Equals(detection.Family, RenpyAdapter.EngineId, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ForgeResult<AnalysisReport>.Failure(
                ForgeErrorCodes.EngineUnsupported,
                $"分析器只处理 Ren'Py，收到引擎 {detection.Family}。",
                "使用与引擎匹配的适配器。"));
        }

        if (!Directory.Exists(context.GameDirectory))
        {
            return Task.FromResult(ForgeResult<AnalysisReport>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不存在：{context.GameDirectory}",
                "确认游戏仍安装在原路径。"));
        }

        var facts = RenpyInspector.Inspect(context.GameDirectory);
        if (!facts.IsRenpy)
        {
            return Task.FromResult(ForgeResult<AnalysisReport>.Failure(
                ForgeErrorCodes.EngineUnsupported,
                "目录缺少 renpy/ 或 game/，无法按 Ren'Py 分析。",
                "确认 --game 指向游戏根目录。"));
        }

        var report = new AnalysisReport
        {
            SchemaVersion = 1,
            GamePath = facts.Root,
            Detection = detection,
            Candidates = BuildCandidates(facts),
            Coverage = BuildCoverage(facts),
            Risks = BuildRisks(facts),
            Unverified = BuildUnverified(facts),
        };

        return Task.FromResult(ForgeResult<AnalysisReport>.Success(report));
    }

    private static List<TextCandidate> BuildCandidates(RenpyGameFacts facts)
    {
        var astLines = facts.Hooks.AstSayFilterLines;
        var astEvidence = astLines.Count > 0
            ? $"file:renpy/ast.py:{string.Join("/", astLines)}（say 与 menu 文本都经过 config.say_menu_text_filter）"
            : "unverified:当前游戏的 renpy/ast.py 未找到 say_menu_text_filter；可能被裁剪或版本改名，需人工确认";
        var configEvidence = facts.Hooks.ConfigSayFilterLine is not null
            ? $"file:renpy/config.py:{facts.Hooks.ConfigSayFilterLine}（say_menu_text_filter 配置项定义）"
            : "unverified:renpy/config.py 未找到 say_menu_text_filter 定义";

        var focusEvidence = facts.Hooks.FocusListLine is not null
            ? $"file:renpy/display/focus.py:{facts.Hooks.FocusListLine}（focus_list 每轮界面重建，只能轮询）"
            : "unverified:renpy/display/focus.py 未找到 focus_list；菜单焦点覆盖需人工确认";
        var ttsEvidence = facts.Hooks.ConfigTtsFunctionLine is not null
            ? $"file:renpy/config.py:{facts.Hooks.ConfigTtsFunctionLine}（tts_function 定义，用于接管引擎自语音）"
            : "unverified:renpy/config.py 未找到 tts_function 定义";

        // 只读盘点：把 game/ 下的现实情况写进证据，供 CLI/报告直接展示（不解析 .rpa 内容）。
        var accessibility = facts.AccessibilityScript is null
            ? "未发现 00accessibility.rpy"
            : $"{facts.AccessibilityScript}（{facts.AccessibilityBytes} B）";
        var sayVbs = facts.HasRootSayVbs ? "根目录已有 a11y_say.vbs" : "根目录无 a11y_say.vbs";
        var nvda = facts.NvdaClients.Count == 0
            ? "未发现 nvdaControllerClient*.dll（NVDA 后端不可用，运行时回退 SAPI）"
            : string.Join("、", facts.NvdaClients.Select(c => $"{c.RelativePath}（{c.Bitness} 位，{c.Bytes} B）"));
        var inventoryEvidence = $"measured:game/ 下松散 .rpy {facts.LooseRpy.Count} 个、.rpyc {facts.LooseRpycCount} 个；"
                                + $"{accessibility}；{sayVbs}；{nvda}";

        return new List<TextCandidate>
        {
            new()
            {
                Id = "tc-001",
                Kind = TextCandidateKind.Dialogue,
                Source = "engine:hook:renpy.config.say_menu_text_filter",
                Score = 92,
                Evidence =
                {
                    astEvidence,
                    configEvidence,
                    inventoryEvidence,
                    "measured:研究原型用该钩子覆盖对话与旁白，原文原样返回，不改写游戏文本",
                    "unverified:未真机启动，朗读时机与去重未实测",
                },
            },
            new()
            {
                Id = "tc-002",
                Kind = TextCandidateKind.Choice,
                Source = "engine:hook:renpy.config.say_menu_text_filter#menu-choices",
                Score = 88,
                Evidence =
                {
                    astEvidence,
                    "measured:菜单项文本同样走 say_menu_text_filter；覆盖层另用焦点列表兜底整体播报",
                    "unverified:选项被点击后的确认播报未真机验证",
                },
            },
            new()
            {
                Id = "tc-003",
                Kind = TextCandidateKind.Menu,
                Source = "engine:hook:renpy.display.focus.focus_list",
                Score = 70,
                Evidence =
                {
                    focusEvidence,
                    "measured:覆盖层按 50ms 心跳轮询焦点，变化时才播报，避免刷屏",
                    "unverified:自绘 UI / 无焦点控件不一定出现在 focus_list",
                },
            },
            new()
            {
                Id = "tc-004",
                Kind = TextCandidateKind.Dialogue,
                Source = "engine:hook:renpy.config.tts_function",
                Score = 64,
                Evidence =
                {
                    ttsEvidence,
                    "measured:接管 tts_function 并强制 self_voicing=False，防止引擎自语音与覆盖层双朗读",
                    "unverified:游戏自带 tts_function 的链式保留未真机验证",
                },
            },
        };
    }

    private static CoverageReport BuildCoverage(RenpyGameFacts facts)
    {
        var covered = new List<string>
        {
            "对话与旁白：config.say_menu_text_filter（引擎级统一钩子，返回原字符串，不改写剧情文本）",
            "选项与菜单标签：同一 filter 的 menu 分支，加 focus_list 轮询整体播报",
            "界面焦点：renpy.display.focus.get_focused / focus_list，只播报文本变化",
            "语音后端链：NVDA Controller Client → SAPI（a11y_say.vbs）→ 日志，可 F10 轮换",
            "读屏开关：F8 即时静音/恢复；a11y_off.flag 支持外部开关",
            "键位：F5 重读、F9 整屏、F11 状态快照；init 1200 追加 config.underlay",
            "运行日志：<游戏根>/a11y_speech.log 记录挂载、后端与错误，便于人工验收",
        };

        if (facts.PythonMajor == 2)
        {
            covered.Add("Ren'Py 6.99 / Python 2.7：renpy.display.screen.get_screen 回退与 _tts_all(raw) TypeError 回退");
        }
        else if (facts.PythonMajor == 3)
        {
            covered.Add("Ren'Py 8.x / Python 3：使用 renpy.get_screen 与 _tts_all(raw=True) 主路径");
        }

        var notCovered = new List<string>
        {
            ".rpa 归档内的文本不改写、不解析：本方案只新增松散 .rpy 覆盖层",
            "图片 / CG / 立绘没有可读替代文本；只覆盖引擎交给文本控件的字符串",
            "纯语音、音效、视频内容不在覆盖范围",
            "存档 / 读档 / 历史 / 设置界面只靠焦点列表，覆盖不保证完整",
            "说话人名字依赖下一拍读取 store._last_say_who，个别写法可能缺失",
            "数字键 1–9 直选选项未实现；目前用方向键 + 回车",
        };

        if (facts.AccessibilityScript is not null)
        {
            notCovered.Add($"引擎自带 {facts.AccessibilityScript} 的菜单/自语音行为未接管，覆盖层保持 self_voicing=False");
        }

        var manualTodo = new List<string>
        {
            "启动游戏并在 NVDA 下逐项验收：对话、旁白、选项、主菜单、存读档、F5/F8/F9/F10/F11",
            "确认游戏能加载 game/a11y_platform/ 下的松散脚本（看 a11y_speech.log 是否出现「覆盖层已加载」）",
        };

        if (facts.HasExistingOverlay)
        {
            manualTodo.Add("先卸载 game/ 下已有的 a11y_* 补丁，再部署本次生成物；否则同名文件会拒绝覆盖");
        }

        if (facts.IsSteam)
        {
            manualTodo.Add("Steam 目录只读：由用户手动复制生成物到游戏目录，或先复制整份游戏到可写目录演练");
        }

        if (facts.PythonMajor == 2)
        {
            manualTodo.Add("Python 2.7 / 6.99 路径只通过静态检查，需真机确认 NVDA DLL 位数与 wscript 中文参数");
        }

        if (facts.NvdaClients.Count == 0)
        {
            manualTodo.Add("游戏目录未发现 nvdaControllerClient*.dll；NVDA 后端需放入与 Python 位数匹配的 DLL，否则回退 SAPI");
        }

        if (facts.Bitness == 0)
        {
            manualTodo.Add("未能确定 Python 位数，需人工确认 NVDA Controller Client 的 32/64 位");
        }

        return new CoverageReport
        {
            Covered = covered,
            NotCovered = notCovered,
            ManualTodo = manualTodo,
        };
    }

    private static List<RiskItem> BuildRisks(RenpyGameFacts facts)
    {
        var risks = new List<RiskItem>
        {
            new()
            {
                Id = "r-001",
                Severity = RiskSeverity.High,
                Description = "所有朗读、焦点、按键行为均未真机启动验证，只通过离线静态检查。",
                Mitigation = "按 README 的验收清单在目标机器上跑通；未通过前不得对外宣称可用。",
            },
            new()
            {
                Id = "r-002",
                Severity = RiskSeverity.High,
                Description = "Steam 游戏目录按只读处理；生成器不会写游戏目录，安装需用户手动复制。",
                Mitigation = "先在非 Steam 副本演练；复制前用 a11y_manifest.json 的 SHA256 核对文件。",
            },
            new()
            {
                Id = "r-003",
                Severity = RiskSeverity.Medium,
                Description = "若游戏自定义了 say_menu_text_filter 或 tts_function，链式接管失败可能导致双朗读或漏读。",
                Mitigation = "覆盖层保存原 filter 并链式调用；日志记录接管结果，真机逐项确认。",
            },
            new()
            {
                Id = "r-004",
                Severity = RiskSeverity.Medium,
                Description = "不同 Ren'Py 版本 API 有差异（renpy.get_screen、_tts_all 签名、键位 init 顺序）。",
                Mitigation = "覆盖层内置 6.99 回退；在 6.99 与 8.x 各选一作真机验证。",
            },
            new()
            {
                Id = "r-005",
                Severity = RiskSeverity.Low,
                Description = "SAPI 后端每句启动 wscript，长文本或低配机器可能有延迟。",
                Mitigation = "优先 NVDA Controller Client；必要时降低 SAPI 速率或改用日志后端排查。",
            },
            new()
            {
                Id = "r-006",
                Severity = RiskSeverity.Medium,
                Description = "焦点轮询依赖 focus_list 与 periodic_callbacks，自绘 UI 或无焦点控件可能不播报。",
                Mitigation = "把未覆盖界面写入报告；真机验收时记录具体界面与按键路径。",
            },
        };

        if (facts.HasExistingOverlay)
        {
            risks.Add(new RiskItem
            {
                Id = "r-007",
                Severity = RiskSeverity.High,
                Description = "检测到已有 a11y_* 补丁；本次生成物不覆盖同名文件，直接安装会失败或混用两套补丁。",
                Mitigation = "先运行已有补丁的 uninstall.ps1 并核对哈希，再部署本次生成物。",
            });
        }

        return risks;
    }

    private static List<string> BuildUnverified(RenpyGameFacts facts)
    {
        var unverified = new List<string>
        {
            "未启动游戏：所有朗读、焦点、按键、菜单行为均未经真机验证。",
            "未验证 NVDA Controller Client 与游戏 Python 位数是否匹配。",
            "未验证 SAPI 路径在中文、长文本与多屏场景下的断句和延迟。",
            "未验证游戏自定义 say_menu_text_filter / tts_function 被链式保留后的实际效果。",
            "未解析 .rpa 内容：归档内文本不会被改写，本分析只覆盖松散脚本与引擎级钩子。",
            "未验证 6.99 与 8.x 各版本的真机差异（_tts_all 签名、get_screen、键位顺序）。",
        };

        if (facts.HasExistingOverlay)
        {
            unverified.Add("已装 a11y_* 补丁与本次生成物的交互未验证；需先卸载再部署。");
        }

        if (facts.Hooks.AstSayFilterLines.Count == 0)
        {
            unverified.Add("当前游戏的 renpy/ast.py 未找到 say_menu_text_filter，钩子是否可用需人工确认。");
        }

        return unverified;
    }
}
