using GalA11yForge.Adapters.Il2Cpp;
using GalA11yForge.Adapters.Renpy;
using GalA11yForge.Adapters.UnityMono;
using GalA11yForge.Build;
using GalA11yForge.Core.Abstractions;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Cli;

/// <summary>
/// 内置适配器（随工具分发）。与 workspace/adapters/adapter.json 里的外置适配器不同，
/// 这些不需要用户放置任何文件就能用；doctor 的 adapters 检查也认它。
/// </summary>
internal sealed class BuiltInAdapter
{
    public string EngineId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public IEngineAnalyzer? Analyzer { get; init; }

    public IPlanner? Planner { get; init; }

    public ICodeGenerator? Generator { get; init; }

    public IBuilder? Builder { get; init; }

    public IDeployer? Deployer { get; init; }

    public IVerifier? Verifier { get; init; }

    /// <summary>
    /// 部署器工厂：需要工作区/部署选项才能构造的部署器（IL2CPP 的 BepInEx6 部署器）走这里。
    /// CLI 里 install/verify 共用，保证两个命令用同一套选项。
    /// </summary>
    public Func<ForgeWorkspace, DeploymentOptions, IDeployer>? DeployerFactory { get; init; }

    /// <summary>
    /// 构建前必须先在游戏目录里生成 IL2CPP interop（BepInEx 6 首次启动产出 BepInEx/interop/*.dll）。
    /// 为 true 时 CLI 在 build 前做前置检查，缺失返回 E_DEPENDENCY_MISSING 并给出可执行指引。
    /// </summary>
    public bool RequiresIl2CppInterop { get; init; }
}

internal static class BuiltInAdapters
{
    private static readonly Dictionary<string, BuiltInAdapter> ByEngine = Build();

    public static int Count => ByEngine.Count;

    public static IEnumerable<string> EngineIds => ByEngine.Keys;

    public static bool TryGet(string engineId, out BuiltInAdapter adapter)
        => ByEngine.TryGetValue(engineId, out adapter!);

    public static BuiltInAdapter? Find(string engineId)
        => ByEngine.TryGetValue(engineId, out var adapter) ? adapter : null;

    private static Dictionary<string, BuiltInAdapter> Build()
    {
        var renpy = new RenpyAdapter();
        var unityMono = new UnityMonoAdapter();
        var il2cpp = new Il2CppAdapter();
        var map = new Dictionary<string, BuiltInAdapter>(StringComparer.OrdinalIgnoreCase)
        {
            [UnityMonoAdapter.EngineId] = new BuiltInAdapter
            {
                EngineId = UnityMonoAdapter.EngineId,
                Name = "Unity Mono 适配器（Cecil 静态分析 + BepInEx 插件生成）",
                Version = UnityMonoAdapter.AdapterVersion,
                Analyzer = unityMono,
                Planner = unityMono,
                Generator = unityMono,
            },
            [Il2CppAdapter.EngineId] = new BuiltInAdapter
            {
                EngineId = Il2CppAdapter.EngineId,
                Name = "Unity IL2CPP 适配器（interop 静态分析 + BepInEx6 插件生成；安装/朗读待真机验证）",
                Version = Il2CppAdapter.AdapterVersion,
                Analyzer = il2cpp,
                Planner = il2cpp,
                Generator = il2cpp,
                RequiresIl2CppInterop = true,
                DeployerFactory = (workspace, deploymentOptions) => new Il2CppPluginDeployer(workspace, deploymentOptions),
            },
            [RenpyAdapter.EngineId] = new BuiltInAdapter
            {
                EngineId = RenpyAdapter.EngineId,
                Name = "Ren'Py 覆盖层适配器（game/ 优先加载）",
                Version = RenpyAdapter.AdapterVersion,
                Analyzer = renpy,
                Planner = renpy,
                Generator = renpy,
                Verifier = renpy,
            },
        };

        return map;
    }
}
