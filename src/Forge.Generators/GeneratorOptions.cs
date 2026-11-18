namespace GalA11yForge.Generators;

/// <summary>
/// 生成器可选项。所有默认值都与输入（plan / context）有关，不读环境状态；
/// 同一个输入加同一组选项，输出必须逐字节相同。
/// </summary>
public sealed class GeneratorOptions
{
    /// <summary>
    /// Framework 仓库目录（内含 <c>platform/</c>、<c>contract/</c>、<c>tools/l1-files.txt</c>）。
    /// 为 null 时按「环境变量 GALA11Y_FRAMEWORK_DIR → 从程序/当前目录向上找 Framework/platform/Speech.cs」定位。
    /// </summary>
    public string? FrameworkDirectory { get; set; }

    /// <summary>插件命名空间。默认由游戏 slug 派生，例如 <c>GalA11y.ForgeGolden</c>。</summary>
    public string? Namespace { get; set; }

    /// <summary>展示名（BepInEx 插件名 / 文档标题）。默认「&lt;游戏目录名&gt; 无障碍补丁」。</summary>
    public string? Title { get; set; }

    /// <summary>补丁版本，纯数字 2–4 段，默认 <c>0.1.0.0</c>。</summary>
    public string? Version { get; set; }

    /// <summary>BepInEx 插件 GUID。默认由 slug + 命名空间确定性派生，不是随机值。</summary>
    public string? PluginGuid { get; set; }
}
