namespace UnityMonoAnalyzer.Analysis;

/// <summary>
/// 人工复核标记（不是打分规则）。
/// R4 §11 的 12 条规则只回答"像不像台词写入点"，回答不了"这条是不是主对话"；
/// 这里把静态可判的噪声特征挂在候选上，供报告生成"建议优先复核"短名单。
/// 引入依据（[实测] 本轮 4 款游戏）：
///  * 我与你共度的七日：Top1 是 NodeCanvas 自带的 Examples/DialogueUGUI（示例代码，可能未被游戏使用）；
///  * NEEDY GIRL OVERDOSE：Top5 全是 `*Dialog::OnLanguageChanged`（换语言时的整窗刷新，不是每句台词）；
///  * To the Moon：Top1 `SetTextFromGameVariable::SetText` 是框架内部取值器，游戏侧入口在其上游。
/// </summary>
public static class ReviewFlags
{
    /// <summary>系统/菜单类 UI（确认框、设置、存档、暂停、退出…），不是主对话。</summary>
    public const string SystemUi = "system-ui";
    /// <summary>本地化刷新方法：一次换语言把整个窗口重写一遍，运行期会造成成批假事件。</summary>
    public const string I18nRefresh = "i18n-refresh";
    /// <summary>位于框架代发的 Examples/Samples/Demo 目录，可能只是示例代码。</summary>
    public const string SampleCode = "sample-code";
    /// <summary>方法本身声明在已知对话框架的命名空间里（框架内部实现，游戏侧入口通常在其上游）。</summary>
    public const string FrameworkInternal = "framework-internal";
    /// <summary>同一方法里有多处文本写入（打字机/逐字/重排），运行期需去重。</summary>
    public const string MultiWrite = "multi-write";
    /// <summary>全部写入都是硬编码字面量（不是从对话数据读出来的）。</summary>
    public const string HardcodedText = "hardcoded-text";
    /// <summary>语义上游候选：本身不写文本，只在调用链上游包了一层。</summary>
    public const string UpstreamOnly = "upstream-only";

    private static readonly string[] SystemUiWords =
    {
        "MessageBox", "Confirm", "Alert", "Notice", "Warning", "Error", "Reboot", "Shutdown", "ShutDown",
        "TimePass", "YesNo", "Quit", "ExitPanel", "ExitButton", "SavePanel", "LoadPanel", "SaveLoad",
        "Settings", "Config", "Option", "Pause", "Title", "Credit", "Achievement", "Tutorial",
        "Launcher", "Login", "Updater", "Download", "Patch", "DialogResult", "Backlog", "Gallery",
        "Tooltip", "Hint", "Notice", "Toast", "Splash",
    };

    private static readonly string[] I18nWords =
    {
        "Language", "Localiz", "Localis", "ApplyLang", "SetLang", "ChangeLang", "RefreshText",
        "RefreshLocalized", "UpdateText", "ReloadText", "OnEnable", "Awake", "Start",
    };

    private static readonly string[] SampleSegments = { "Examples", "Example", "Samples", "Sample", "Demo", "Tests" };

    public static List<string> Build(MethodRecord rec, CandidateFeatures f)
    {
        var flags = new List<string>();

        if (SystemUiWords.Any(w => rec.TypeFullName.Contains(w, StringComparison.OrdinalIgnoreCase)
                                || rec.MethodName.Contains(w, StringComparison.OrdinalIgnoreCase)))
            flags.Add(SystemUi);

        if (I18nWords.Any(w => rec.MethodName.Contains(w, StringComparison.OrdinalIgnoreCase)))
            flags.Add(I18nRefresh);

        if (SampleSegments.Any(s => rec.TypeFullName.Contains(s, StringComparison.OrdinalIgnoreCase)))
            flags.Add(SampleCode);

        if (f.OnKnownFrameworkType) flags.Add(FrameworkInternal);

        if (rec.SinkSites.Count > 1) flags.Add(MultiWrite);

        if (f.ConstantSinkCalls > 0 && f.DynamicSinkCalls == 0) flags.Add(HardcodedText);

        if (!rec.DiscoveredBySink) flags.Add(UpstreamOnly);

        return flags;
    }

    /// <summary>会让"建议优先复核"短名单降权的标记。</summary>
    public static bool IsNoise(string flag) => flag is SystemUi or SampleCode or I18nRefresh;

    public static bool HasNoise(IEnumerable<string> flags) => flags.Any(IsNoise);
}
