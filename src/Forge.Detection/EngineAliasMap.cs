namespace GalA11yForge.Detection;

/// <summary>
/// 引擎 id 别名助手（临时兼容层）。
///
/// 背景：研究层指纹库用 research 原始 id（kirikiri_z、rm_mv_mz），
/// 而 docs/06-接口契约-v1.md §1 的示例写的是 kirikiriz、rpgmaker_mv_mz。
/// 目前 Forge.Core 里没有引擎 id 白名单（grep 只找到注释举例），所以本层**原样输出 research id**，
/// 是否切换由 Lead/适配器注册表决定；需要契约写法时调用 <see cref="ToContractId"/>。
///
/// 未列出的 id 视为同名（identity）——这是「默认不动」，不是确认过的映射。
/// </summary>
public static class EngineAliasMap
{
    private static readonly IReadOnlyDictionary<string, string> ResearchToContract =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["kirikiri_z"] = "kirikiriz",
            ["rm_mv_mz"] = "rpgmaker_mv_mz",
        };

    private static readonly IReadOnlyDictionary<string, string> ContractToResearch =
        ResearchToContract.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>research id → 契约示例 id；未登记的 id 原样返回。</summary>
    public static string ToContractId(string researchId) =>
        ResearchToContract.TryGetValue(researchId, out var contractId) ? contractId : researchId;

    /// <summary>契约 id → research id；未登记的 id 原样返回。</summary>
    public static string ToResearchId(string contractId) =>
        ContractToResearch.TryGetValue(contractId, out var researchId) ? researchId : contractId;

    /// <summary>全部已知映射（只读），供 CLI/文档打印。</summary>
    public static IReadOnlyDictionary<string, string> Mappings => ResearchToContract;
}
