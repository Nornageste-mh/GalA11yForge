namespace UnityMonoAnalyzer.Reporting;

/// <summary>覆盖声明 + 8 项运行期确认清单。生成的报告必须说得清"能做什么/不能做什么"。</summary>
public static class Coverage
{
    public static CoverageInfo Build(ScanStats stats, bool cjkGame, int upstreamCount)
    {
        var info = new CoverageInfo
        {
            Thresholds = new Thresholds(),
            Dimensions = BuildDimensions(cjkGame),
        };

        info.Can.AddRange(new[]
        {
            "定位 `*_Data\\Managed\\Assembly-CSharp.dll`：支持多层子目录、一个安装目录内多个 Unity 程序集（启动器/多章节）并给出排名理由",
            "识别 Unity 版本（`globalgamemanagers` 优先，`UnityPlayer.dll` 版本资源兜底）并拒绝 IL2CPP 目标",
            "统计 `TMPro.TMP_Text::set_text` / `SetText` 与 `UnityEngine.UI.Text::set_text` 的调用指令、调用者方法与类型",
            "用 IL 反向栈分析判定写入实参来源（常量 / 参数 / 字段 / 局部变量 / 方法调用），作为 S7 依据",
            "已知对话框架痕迹统计：Naninovel、NodeCanvas DialogueTrees、ScenarioForge、Fungus、Yarn、Ink、PixelCrushers；以及自定义 `*Dialog*`/`*Talk*` 命名",
            "CJK 字面量分布（含平假名/片假名/汉字），只输出计数与所在方法，不导出任何字面量内容",
            "按 R4 规则打分并分层（A≥70 且 S1>0 / B≥50 / C≥30），输出可交人工确认的候选表与逐条证据",
            "识别已部署补丁痕迹（BepInEx / doorstop / nvdaControllerClient）作为环境事实，不据此推断可 Hook 性",
        });

        info.Cannot.AddRange(new[]
        {
            "看不到 `set_text` 之外的文本通道：图片字、TextMesh(3D)、自绘文本、以及游戏自定义的 \"SetString\" 式非标准 API（只有方法名里带 Text/SetText 的才会被统计）",
            "不解析 TextAsset、本地化表、字体/图集资源里的台词；只统计代码内字面量（因此文本量被低估是正常的）",
            "静态无法判断可见性：CanvasGroup.alpha、activeInHierarchy、面板是否在当前场景，菜单/设置/存档面板的文本会被当候选",
            "静态无法判断说话人（who）与正文的先后、打字机逐字多次 `set_text` 的去重、富文本标签清洗 —— 这些必须运行期确认",
            "只做一层语义上游展开：Manager→Presenter→View 这类跨 2 层以上的包装器可能漏检（可通过 `--top` 放宽后人工翻查）",
            "不分析插件 DLL（BepInEx 插件、第三方 UI 库）与 IL2CPP 目标；只测 `Assembly-CSharp.dll`",
            "没有运行期证据：全部分数都是静态启发式，A 级候选也必须经运行期回放确认后才可生成补丁",
        });

        info.RuntimeConfirmations.AddRange(new[]
        {
            "1. 目标游戏用 TMP 还是 uGUI.Text？两者是否同时存在（存在则需双重 Hook 并按 API 去重）",
            "2. 打字机效果是否对同一句反复调用 set_text（需按内容+时间窗口去重）",
            "3. 说话人（who）与正文（text）的写入先后顺序，能否在同一次 Hook 中拼出完整朗读串",
            "4. 选项列表：按钮文本来源、顺序、禁用项（不可选）如何表达",
            "5. 菜单/设置/存档面板：确认哪些写入点应被静音或排除",
            "6. 可见性：CanvasGroup.alpha / activeInHierarchy / 是否能读到当前激活面板",
            "7. 富文本清洗：`<color>`、`<size>`、`<ruby>`、`<sprite>` 等标签的剥离规则",
            "8. 事件频率与去重窗口：每秒最多朗读次数、重复句抑制",
        });

        if (!cjkGame)
        {
            info.Cannot.Add("本程序集未检出 CJK 字面量（英文游戏或文本全在资源里），S6 维度不适用，中文台词定位能力对本作无效");
        }
        if (upstreamCount == 0)
        {
            info.Cannot.Add("未发现「语义上游」候选（没有方法名带 Say/Line/Dialogue 等关键词的上层包装器），可能存在非语义命名的包装器");
        }
        if (stats.ResolveFailures.Count > 0)
        {
            info.Cannot.Add($"有 {stats.ResolveFailures.Count} 个引用程序集无法解析（{string.Join(", ", stats.ResolveFailures.Take(5))}），继承链回溯可能不完整");
        }

        return info;
    }

    private static List<DimensionInfo> BuildDimensions(bool cjkGame) => new()
    {
        new DimensionInfo { Id = "S1", Name = "已知对话框架命中（含调用图上游）", Points = 25, Applicable = true },
        new DimensionInfo { Id = "S2", Name = "方法名语义（Say/Line/Dialogue/Talk/Message/Choice/Select/ShowText）", Points = 20, Applicable = true },
        new DimensionInfo { Id = "S3", Name = "直接调用文本 set_text", Points = 20, Applicable = true },
        new DimensionInfo { Id = "S4", Name = "所属类型持有 Text/TMP 字段", Points = 10, Applicable = true },
        new DimensionInfo { Id = "S5", Name = "类名含 Window/UI/Dialog/Box/Panel", Points = 5, Applicable = true },
        new DimensionInfo
        {
            Id = "S6", Name = "方法内 CJK 字面量", Points = 10, Applicable = cjkGame,
            Reason = cjkGame ? "" : "本程序集无 CJK 字面量，维度不适用"
        },
        new DimensionInfo { Id = "S7", Name = "数据来源非硬编码（参数/字段/调用）", Points = 10, Applicable = true },
        new DimensionInfo { Id = "S8", Name = "调用者>1 或上游有 Choice/Select", Points = 5, Applicable = true },
        new DimensionInfo { Id = "N1", Name = "调试/日志/版本命名", Points = -20, Applicable = true },
        new DimensionInfo { Id = "N2", Name = "非台词文本特征（路径/URL/格式串/数字/标识符）", Points = -15, Applicable = true },
        new DimensionInfo { Id = "N3", Name = "仅被初始化期方法调用", Points = -10, Applicable = true },
        new DimensionInfo { Id = "N4", Name = "固定短串（长度<2）", Points = -10, Applicable = true },
    };
}
