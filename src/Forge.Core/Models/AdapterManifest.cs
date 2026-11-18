namespace GalA11yForge.Core.Models;

/// <summary>
/// 适配器注册项（adapters/adapter.json 的 adapters[] 元素）。
/// Priority 越小越先被选中；Capabilities 声明支持的流水线步骤。
/// </summary>
public sealed class AdapterManifest
{
    /// <summary>适配器唯一名（slug 风格），例如 unity-mono。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>引擎 id，例如 unity_mono；同一引擎可以有多个适配器（按版本/优先级）。</summary>
    public string Engine { get; set; } = string.Empty;

    public int Priority { get; set; } = 100;

    /// <summary>相对适配器目录的程序集文件名，例如 Forge.Adapters.UnityMono.dll。</summary>
    public string Assembly { get; set; } = string.Empty;

    /// <summary>实现类型全名，例如 GalA11yForge.Adapters.UnityMono.UnityMonoAdapter。</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>已知值：detect、analyze、plan、generate、build、deploy、verify、universal、analyzer_backend。</summary>
    public List<string> Capabilities { get; set; } = new();
}

/// <summary>adapter.json 的根对象。也兼容顶层直接是数组的写法。</summary>
public sealed class AdapterRegistryDocument
{
    public int SchemaVersion { get; set; } = 1;

    public List<AdapterManifest> Adapters { get; set; } = new();
}
