using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Adapters.Renpy.Internal;

/// <summary>步骤三：根据分析报告生成“零归档覆盖层”补丁方案。</summary>
internal sealed class RenpyPlanner
{
    public Task<ForgeResult<PatchPlan>> PlanAsync(
        GameContext context,
        AnalysisReport analysis,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (analysis is null)
        {
            return Task.FromResult(ForgeResult<PatchPlan>.Failure(
                ForgeErrorCodes.ArgumentInvalid, "分析报告为空，无法生成方案。", "先运行 analyze。"));
        }

        if (analysis.Detection is null)
        {
            return Task.FromResult(ForgeResult<PatchPlan>.Failure(
                ForgeErrorCodes.ArgumentInvalid, "分析报告缺少引擎检测结果。", "重新运行 detect/analyze。"));
        }

        if (!string.Equals(analysis.Detection.Family, RenpyAdapter.EngineId, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ForgeResult<PatchPlan>.Failure(
                ForgeErrorCodes.EngineUnsupported,
                $"该报告不是 Ren'Py（engine={analysis.Detection.Family}），本适配器不处理。",
                "使用与引擎匹配的适配器。"));
        }

        var gameDirectory = string.IsNullOrWhiteSpace(analysis.GamePath)
            ? context.GameDirectory
            : analysis.GamePath;

        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            return Task.FromResult(ForgeResult<PatchPlan>.Failure(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不存在：{gameDirectory}",
                "确认 --game 指向仍安装的游戏根目录。"));
        }

        var facts = RenpyInspector.Inspect(gameDirectory);
        if (!facts.IsRenpy)
        {
            return Task.FromResult(ForgeResult<PatchPlan>.Failure(
                ForgeErrorCodes.EngineUnsupported,
                "游戏目录缺少 renpy/ 或 game/，无法制定覆盖层方案。",
                "重新运行 detect 确认引擎。"));
        }

        var confidence = 0.85;
        if (facts.HasExistingOverlay)
        {
            confidence -= 0.10;
        }

        if (analysis.Detection.Bitness == 0)
        {
            confidence -= 0.05;
        }

        var plan = new PatchPlan
        {
            SchemaVersion = 1,
            GameSlug = RenpyPaths.ResolveSlug(context),
            Route = PlanRoute.Automatic,
            Target = new PlanTarget
            {
                GamePath = Path.GetFullPath(gameDirectory),
                Engine = RenpyAdapter.EngineId,
                EngineVersion = analysis.Detection.Version,
                Bitness = analysis.Detection.Bitness,
            },
            Confidence = Math.Round(Math.Clamp(confidence, 0, 1), 2),
            PatchPoints = BuildPatchPoints(facts),
            Artifacts = RenpyOverlayCatalog.CreateArtifacts().ToList(),
            Risks = analysis.Risks.ToList(),
            ManualTodo = BuildManualTodo(facts, analysis),
        };

        plan.Risks.Add(new RiskItem
        {
            Id = "r-101",
            Severity = facts.HasExistingOverlay ? RiskSeverity.High : RiskSeverity.Medium,
            Description = "覆盖层是新增松散 .rpy：若游戏目录已有同名文件或其他 a11y 补丁，可能出现加载顺序冲突或双重朗读。",
            Mitigation = "生成器只新增文件、不覆盖同名文件；安装前确认 game/a11y_platform 不存在或已卸载。",
        });

        return Task.FromResult(ForgeResult<PatchPlan>.Success(plan));
    }

    private static List<PatchPoint> BuildPatchPoints(RenpyGameFacts facts)
    {
        var points = new List<PatchPoint>
        {
            new()
            {
                Id = "pp-001",
                Kind = PatchPointKind.Script,
                Target = "game/a11y_platform/00_contract.rpy",
                CandidateId = "tc-001",
                Confidence = 0.92,
                Description = "公共契约层：常量、状态、日志、屏幕取词与 _tts_all 双签名回退；必须最先加载（init -1850）。",
            },
            new()
            {
                Id = "pp-002",
                Kind = PatchPointKind.Script,
                Target = "game/a11y_platform/01_speech.rpy",
                CandidateId = "tc-004",
                Confidence = 0.90,
                Description = "语音后端链（NVDA DLL → SAPI/VBS → 日志）与去重；接管 config.tts_function 只为阻止游戏自带语音重复。",
            },
            new()
            {
                Id = "pp-003",
                Kind = PatchPointKind.Script,
                Target = "game/a11y_platform/02_dialogue.rpy",
                CandidateId = "tc-001",
                Confidence = 0.93,
                Description = "链式挂载 config.say_menu_text_filter：调用游戏原有 filter 后原样返回，只旁路记录待播文本。",
            },
            new()
            {
                Id = "pp-004",
                Kind = PatchPointKind.Script,
                Target = "game/a11y_platform/03_menu.rpy",
                CandidateId = "tc-002",
                Confidence = 0.86,
                Description = "选项/主菜单整体播报：读取焦点列表文本，避免只读当前项导致上下文缺失。",
            },
            new()
            {
                Id = "pp-005",
                Kind = PatchPointKind.Script,
                Target = "game/a11y_platform/04_focus.rpy",
                CandidateId = "tc-003",
                Confidence = 0.82,
                Description = "焦点与屏幕轮询：periodic_callbacks 心跳，检测焦点变化后朗读控件文本。",
            },
            new()
            {
                Id = "pp-006",
                Kind = PatchPointKind.Script,
                Target = "game/a11y_platform/05_keys.rpy",
                CandidateId = null,
                Confidence = 0.90,
                Description = "键位：必须在 init 1200 整体追加 config.underlay，用 compile_event 校验 F5/F8/F9/F10/F11。",
            },
            new()
            {
                Id = "pp-007",
                Kind = PatchPointKind.Asset,
                Target = "a11y_say.vbs",
                CandidateId = null,
                Confidence = 0.85,
                Description = "SAPI 兜底朗读脚本：纯 ASCII，由 wscript 调起；只在 NVDA 后端不可用时使用。",
            },
            new()
            {
                Id = "pp-008",
                Kind = PatchPointKind.Config,
                Target = "game/a11y_platform + a11y_manifest.json + deploy_plan.json",
                CandidateId = null,
                Confidence = 0.95,
                Description = "安装策略：只新增 9 个内容文件，不覆盖同名文件；纯新增因此无需备份；清单记录 SHA256 供卸载核对。",
            },
        };

        if (facts.HasExistingOverlay)
        {
            points.Add(new PatchPoint
            {
                Id = "pp-009",
                Kind = PatchPointKind.Other,
                Target = string.Join(", ", facts.OverlayDirectories),
                CandidateId = null,
                Confidence = 1.0,
                Description = "检测到已安装 a11y_* 覆盖层：安装新补丁前必须先按旧清单卸载，否则两套钩子会同时生效。",
            });
        }

        return points;
    }

    private static List<ManualTodoItem> BuildManualTodo(RenpyGameFacts facts, AnalysisReport analysis)
    {
        var todos = new List<ManualTodoItem>();
        if (facts.HasExistingOverlay)
        {
            todos.Add(new ManualTodoItem
            {
                Id = "mt-001",
                Title = "先卸载已有的 a11y_* 补丁",
                Detail = $"检测到 {string.Join("、", facts.OverlayDirectories)}。请先运行其 uninstall.ps1（或按旧清单手工删除），"
                         + "确认 game 下不再有 a11y_ 开头的覆盖层，再安装本补丁。",
                Blocking = true,
            });
        }

        todos.Add(new ManualTodoItem
        {
            Id = facts.HasExistingOverlay ? "mt-002" : "mt-001",
            Title = "真机验收（本工具无法代替）",
            Detail = "把生成目录内容复制到游戏根目录后启动游戏：确认 a11y_speech.log 出现加载记录，"
                     + "并用 NVDA 检查对话、旁白、选项、主菜单、存读档与 F5/F8/F9/F10/F11。",
            Blocking = true,
        });

        if (facts.IsSteam)
        {
            todos.Add(new ManualTodoItem
            {
                Id = "mt-003",
                Title = "Steam 目录只读：手工复制安装",
                Detail = "游戏位于 steamapps 下。本工具不写游戏目录；请关闭 Steam 云同步后，把 9 个内容文件复制到游戏根目录，"
                         + "或先复制整个游戏到可写目录再安装。",
                Blocking = false,
            });
        }

        if (facts.PythonMajor == 2)
        {
            todos.Add(new ManualTodoItem
            {
                Id = "mt-004",
                Title = "Ren'Py 6.99 / Python 2 兼容性需实测",
                Detail = "覆盖层包含 6.99 的 get_screen 回退与 _tts_all(self) 回退，但静态检查不能证明运行时行为。",
                Blocking = false,
            });
        }

        if (facts.NvdaClients.Count == 0)
        {
            todos.Add(new ManualTodoItem
            {
                Id = "mt-005",
                Title = "未发现 nvdaControllerClient DLL",
                Detail = "游戏目录没有 nvdaControllerClient32/64.dll，补丁将回退到 SAPI/VBS 朗读；"
                         + "若要 NVDA 直连，需要用户自行提供合法授权的 DLL。",
                Blocking = false,
            });
        }

        if (facts.Bitness == 0)
        {
            todos.Add(new ManualTodoItem
            {
                Id = "mt-006",
                Title = "未识别 Python 位数",
                Detail = "lib/ 下没有可识别的 windows 运行库目录，NVDA DLL 位数选择可能不正确；真机验收时重点确认后端。",
                Blocking = false,
            });
        }

        if (analysis.Unverified.Count > 0)
        {
            todos.Add(new ManualTodoItem
            {
                Id = "mt-007",
                Title = "阅读报告中的“未验证”清单",
                Detail = string.Join("；", analysis.Unverified),
                Blocking = false,
            });
        }

        return todos;
    }
}
