namespace GalA11yForge.Adapters.UnityMono.Internal;

internal sealed class UnityMonoParameterFact
{
    public required string Name { get; init; }

    public required string TypeFullName { get; init; }

    public bool IsString { get; init; }

    public bool IsInt32NamedNum { get; init; }
}

internal sealed class UnityMonoSinkHit
{
    public required string Api { get; init; }

    public int Offset { get; init; }

    /// <summary>constant / parameter / field / local / call / other / unknown；不保存字面量内容。</summary>
    public required string ArgumentSource { get; init; }

    public int ConstantLength { get; init; }

    public bool IsDynamic => !string.Equals(ArgumentSource, "constant", StringComparison.Ordinal);
}

internal sealed class UnityMonoMethodFacts
{
    public required string AssemblyFile { get; init; }

    public required string AssemblyName { get; init; }

    public required string TypeFullName { get; init; }

    public required string TypeName { get; init; }

    public required string Namespace { get; init; }

    public required string MethodName { get; init; }

    /// <summary>Cecil 的完整方法名（含返回类型与参数类型），用于精确匹配调用边。</summary>
    public required string FullName { get; init; }

    /// <summary>带参数名的可读签名：<c>System.Void UIText::AnimateText(TMPro.TextMeshProUGUI targetText, ...)</c>。</summary>
    public required string Signature { get; init; }

    public List<UnityMonoParameterFact> Parameters { get; init; } = new();

    public bool HasStringParameter { get; init; }

    public bool HasInt32NumParameter { get; init; }

    public bool IsConstructor { get; init; }

    public bool IsGenericMethod { get; init; }

    public int TextFieldCount { get; init; }

    public List<string> TextFieldNames { get; init; } = new();

    public int LdstrCount { get; init; }

    public int CjkLiteralCount { get; init; }

    public int NonDialogueLiteralCount { get; init; }

    public int MaxLiteralLength { get; init; }

    public List<UnityMonoSinkHit> TextSinkCalls { get; init; } = new();

    public List<UnityMonoSinkHit> TypewriterSinkCalls { get; init; } = new();

    public bool HasDirectTextSink => TextSinkCalls.Count > 0;

    public bool HasDynamicTextSink => TextSinkCalls.Any(h => h.IsDynamic);

    public bool HasTypewriterSink => TypewriterSinkCalls.Count > 0;

    public bool HasDynamicTypewriterSink => TypewriterSinkCalls.Any(h => h.IsDynamic);

    public bool IsSemanticName { get; init; }

    public bool IsUiName { get; init; }

    public bool IsDebugName { get; init; }

    public bool IsChoiceLike { get; init; }

    public bool IsMenuLike { get; init; }

    public string? FrameworkId { get; init; }

    public bool IsCustomDialogueType { get; init; }

    /// <summary>同一类型内检测到 TMP 字段 + 直接文本写入 + string 参数的打字机方法。</summary>
    public bool TypeLevelPipeline { get; set; }

    public bool IsTextRelevant =>
        HasStringParameter || HasDirectTextSink || HasTypewriterSink || IsSemanticName || IsChoiceLike;

    public int CallerCount { get; set; }

    public List<string> CallerSamples { get; } = new();

    public bool AllCallersInitLike { get; set; } = true;

    public bool AnyCallerChoiceLike { get; set; }

    public int InstructionCount { get; init; }
}

internal sealed class UnityMonoTypeFacts
{
    public required string FullName { get; init; }

    public required string Name { get; init; }

    public required string Namespace { get; init; }

    public int TextFieldCount { get; init; }

    public List<string> TextFieldNames { get; init; } = new();

    public string? FrameworkId { get; init; }

    public bool IsCustomDialogueType { get; init; }

    public bool HasDirectTextWriter { get; set; }

    public bool HasStringTypewriterEntry { get; set; }

    public bool IsDialoguePipeline =>
        TextFieldCount > 0 && HasDirectTextWriter && HasStringTypewriterEntry;

    public int MethodCount { get; init; }
}

internal sealed class UnityMonoAssemblyScan
{
    public required string FilePath { get; init; }

    public required string FileName { get; init; }

    public required string AssemblyName { get; init; }

    public string? AssemblyVersion { get; set; }

    public bool IsPrimary { get; init; }

    public int TypesScanned { get; set; }

    public int MethodsScanned { get; set; }

    public int MethodsWithBody { get; set; }

    public int LdstrTotal { get; set; }

    public int CjkLiteralMethods { get; set; }

    public bool Truncated { get; set; }

    public List<string> Issues { get; } = new();

    public List<UnityMonoMethodFacts> Methods { get; } = new();

    public Dictionary<string, UnityMonoTypeFacts> Types { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, int> SinkApiCounts { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, int> TypewriterApiCounts { get; } = new(StringComparer.Ordinal);
}

internal sealed class UnityMonoScanOutcome
{
    public required UnityMonoGameLayout Layout { get; init; }

    public required string Language { get; init; }

    public required string LanguageSource { get; init; }

    public double MinConfidence { get; init; }

    public List<string> Notes { get; init; } = new();

    public List<UnityMonoAssemblyScan> Assemblies { get; init; } = new();
}
