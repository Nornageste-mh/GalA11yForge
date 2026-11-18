namespace GalA11yForge.Adapters.Il2Cpp.Internal;

/// <summary>方法参数事实。只保留类型与名字，不保留任何实参内容。</summary>
internal sealed class Il2CppParameterFact
{
    public string Name { get; init; } = string.Empty;

    /// <summary>参数类型显示名，例如 <c>System.String</c>、<c>System.Single</c>。</summary>
    public string TypeName { get; init; } = string.Empty;

    public bool IsString { get; init; }

    public bool IsNumeric { get; init; }
}

/// <summary>A 单条方法事实。IL2CPP 的 interop 程序集里方法体是调用桩，所以这里**不记录 IL**。</summary>
internal sealed class Il2CppMethodFact
{
    public string AssemblyFile { get; init; } = string.Empty;

    public string TypeFullName { get; init; } = string.Empty;

    public string TypeName { get; init; } = string.Empty;

    public string Namespace { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string FullName { get; init; } = string.Empty;

    /// <summary>人读签名，例如 <c>Naninovel.UITextPrinter::set_Text(System.String)</c>。</summary>
    public string Signature { get; init; } = string.Empty;

    public bool IsConstructor { get; init; }

    public bool IsStatic { get; init; }

    public bool IsVirtual { get; init; }

    public bool IsPropertyMember { get; init; }

    public string? PropertyName { get; init; }

    /// <summary>属性类型名（setter 的参数类型 / getter 的返回类型）。</summary>
    public string? PropertyTypeName { get; init; }

    public string? EventName { get; init; }

    public bool IsEventAdder { get; init; }

    public List<Il2CppParameterFact> Parameters { get; init; } = [];

    public bool HasStringParameter => Parameters.Any(p => p.IsString);

    public bool HasNumericParameter => Parameters.Any(p => p.IsNumeric);

    /// <summary>第一个“看起来像台词”的 string 参数名；没有则为 null。</summary>
    public string? TextParameterName { get; init; }

    /// <summary>dump.cs 路线才有：RVA / 虚表槽位。interop 路线为 0。</summary>
    public long Rva { get; init; }

    public int Slot { get; init; }
}

/// <summary>类型事实。文本类字段/属性只记名字与类型名，不记录任何内容。</summary>
internal sealed class Il2CppTypeFact
{
    public string AssemblyFile { get; init; } = string.Empty;

    public string FullName { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Namespace { get; init; } = string.Empty;

    public string? BaseTypeName { get; init; }

    public int MethodCount { get; set; }

    public List<string> TextMemberNames { get; } = [];

    public List<string> Attributes { get; } = [];

    /// <summary>已知框架 id（如 <c>naninovel</c>）；空表示未识别。</summary>
    public string? FrameworkId { get; set; }

    public bool IsUiType { get; set; }

    /// <summary>接口类型：Harmony 不能直接给接口方法打补丁，候选必须跳过。</summary>
    public bool IsInterface { get; init; }

    public bool IsAbstract { get; init; }
}

/// <summary>事件事实。IL2CPP interop 不生成 EventDefinition，只有 add_/remove_ 方法，故单独记。</summary>
internal sealed class Il2CppEventFact
{
    public string AssemblyFile { get; init; } = string.Empty;

    public string TypeFullName { get; init; } = string.Empty;

    public string EventName { get; init; } = string.Empty;

    public string HandlerTypeName { get; init; } = string.Empty;

    public List<string> HandlerArgumentTypeNames { get; init; } = [];

    /// <summary>处理器委托只带一个 <c>string</c> 参数（这种才可能用通用代码订阅）。</summary>
    public bool IsStringEvent { get; init; }

    public string Signature => TypeFullName + "::" + EventName + "(" + string.Join(",", HandlerArgumentTypeNames) + ")";
}

/// <summary>一次静态扫描的完整产出。</summary>
internal sealed class Il2CppScanOutcome
{
    public Il2CppSourceKind Source { get; init; }

    public Il2CppLayout Layout { get; init; } = new();

    public Dictionary<string, Il2CppTypeFact> Types { get; } = new(StringComparer.Ordinal);

    public List<Il2CppMethodFact> Methods { get; } = [];

    public List<Il2CppEventFact> Events { get; } = [];

    public List<string> Warnings { get; } = [];

    public int AssemblyCount { get; set; }

    public int TypeCount { get; set; }

    public int MethodCount { get; set; }

    public TimeSpan Elapsed { get; set; }
}
