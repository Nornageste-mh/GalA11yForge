using System.Diagnostics;
using Mono.Cecil;

namespace GalA11yForge.Adapters.Il2Cpp.Internal;

/// <summary>
/// 只读扫描 <c>BepInEx/interop/*.dll</c>。
///
/// <para>
/// 关键事实（R18 实测）：interop 程序集里的方法体是 <c>IL2CPP.il2cpp_runtime_invoke</c> 调用桩，
/// 不含原来的调用图，字符串字面量也被搬到 global-metadata。所以这里**只读元数据**：
/// 类型、基类、方法名、参数名/类型、属性、事件（以 add_/remove_ 方法形式出现）。
/// 任何声称“从 IL 里看出调用了 set_Text”的做法在 IL2CPP 上都是假的。
/// </para>
/// </summary>
internal sealed class Il2CppInteropScanner
{
    internal const int MaxAssemblies = 32;
    internal const long MaxAssemblyBytes = 32L * 1024 * 1024;
    internal const long MaxTotalBytes = 96L * 1024 * 1024;
    internal const int MaxTypesPerAssembly = 4000;
    internal const int MaxMethodsPerType = 800;

    private static readonly string[] NumericTypeNames =
    [
        "Single", "Int32", "Int64", "UInt32", "UInt64", "Double", "Int16", "UInt16", "Byte", "SByte",
    ];

    private static readonly string[] StringTypeNames = ["String", "Il2CppSystem.String"];

    public Il2CppScanOutcome Scan(Il2CppLayout layout, CancellationToken cancellationToken)
    {
        var outcome = new Il2CppScanOutcome { Source = Il2CppSourceKind.Interop, Layout = layout };
        var stopwatch = Stopwatch.StartNew();

        if (layout.InteropDirectory is null)
        {
            outcome.Warnings.Add("没有 BepInEx/interop 目录：IL2CPP 静态分析无法进行（需要 BepInEx 6 先成功启动一次）。");
            outcome.Elapsed = stopwatch.Elapsed;
            return outcome;
        }

        var files = new DirectoryInfo(layout.InteropDirectory)
            .EnumerateFiles("*.dll", SearchOption.TopDirectoryOnly)
            .Where(f => !Il2CppLayoutProbe.IsSkippedInteropAssembly(f.Name) && f.Length <= MaxAssemblyBytes)
            .OrderByDescending(f => string.Equals(f.Name, "Assembly-CSharp.dll", StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var totalBytes = 0L;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (outcome.AssemblyCount >= MaxAssemblies)
            {
                outcome.Warnings.Add("interop 程序集超过上限 " + MaxAssemblies + " 个，其余未扫描：" + file.Name);
                break;
            }

            if (totalBytes + file.Length > MaxTotalBytes)
            {
                outcome.Warnings.Add("interop 扫描总字节超过上限，停止于：" + file.Name);
                break;
            }

            totalBytes += file.Length;
            try
            {
                ScanAssembly(file, outcome, cancellationToken);
                outcome.AssemblyCount++;
            }
            catch (BadImageFormatException)
            {
                outcome.Warnings.Add("warn: 不是有效托管程序集，跳过：" + file.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outcome.Warnings.Add("warn: 读取失败，跳过 " + file.Name + "：" + ex.Message);
            }
        }

        outcome.TypeCount = outcome.Types.Count;
        outcome.MethodCount = outcome.Methods.Count;
        outcome.Elapsed = stopwatch.Elapsed;
        return outcome;
    }

    private static void ScanAssembly(FileInfo file, Il2CppScanOutcome outcome, CancellationToken cancellationToken)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(
            file.FullName,
            new ReaderParameters { ReadingMode = ReadingMode.Deferred });

        var assemblyName = assembly.Name.Name;
        var typesSeen = 0;

        foreach (var type in assembly.MainModule.Types)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (typesSeen >= MaxTypesPerAssembly)
            {
                outcome.Warnings.Add("warn: " + file.Name + " 类型数超过上限 " + MaxTypesPerAssembly + "，其余未扫描");
                break;
            }

            if (!IsInterestingType(type))
            {
                continue;
            }

            typesSeen++;
            var facts = BuildTypeFacts(file.Name, assemblyName, type, outcome);
            outcome.Types[facts.FullName] = facts;
            outcome.TypeCount++;
        }
    }

    /// <summary>
    /// Il2CppInterop 把 IL2CPP 接口生成为<b>普通类</b>（<c>IsInterface=false</c>，直接继承
    /// <c>Il2CppObjectBase</c>，方法全 virtual 且带调用桩）。实测 <c>Naninovel.ITextPrinterManager</c>
    /// 就是这种形状；按 <c>I</c>+大写开头且基类恰为 <c>Il2CppObjectBase</c> 识别，
    /// 避免把接口当成可 patch 的补丁目标（运行期调用不会经过这个代理类）。
    /// </summary>
    private static bool IsInteropInterfaceProxy(TypeDefinition type)
    {
        const string interopBase = "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase";

        if (!string.Equals(type.BaseType?.FullName, interopBase, StringComparison.Ordinal))
        {
            return false;
        }

        var name = type.Name;
        return name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]);
    }

    private static bool IsInterestingType(TypeDefinition type)    {
        if (type.Name.StartsWith('<') || type.FullName.Contains('/'))
        {
            return false;
        }

        return type.IsClass || type.IsInterface || type.IsValueType;
    }

    private static Il2CppTypeFact BuildTypeFacts(
        string assemblyFile,
        string assemblyName,
        TypeDefinition type,
        Il2CppScanOutcome outcome)
    {
        var ns = type.Namespace ?? string.Empty;
        var facts = new Il2CppTypeFact
        {
            AssemblyFile = assemblyFile,
            FullName = type.FullName,
            Name = type.Name,
            Namespace = ns,
            BaseTypeName = type.BaseType?.FullName,
            IsInterface = type.IsInterface || IsInteropInterfaceProxy(type),
            IsAbstract = type.IsAbstract,
        };

        facts.FrameworkId = Il2CppPatterns.DetectFramework(assemblyName, type.FullName);
        facts.IsUiType = ns.StartsWith("UnityEngine", StringComparison.Ordinal)
                         || ns.StartsWith("TMPro", StringComparison.Ordinal)
                         || ns.StartsWith("Naninovel.UI", StringComparison.Ordinal)
                         || type.Name.Contains("UI", StringComparison.Ordinal)
                         || type.Name.Contains("Panel", StringComparison.Ordinal)
                         || type.Name.Contains("Widget", StringComparison.Ordinal);

        foreach (var attribute in SafeAttributes(type))
        {
            facts.Attributes.Add(attribute.AttributeType.Name);
        }

        // 属性 setter/getter 索引：IL2CPP interop 保留属性，可以直接按名对上。
        var propertyByAccessor = new Dictionary<string, (string Name, string TypeName)>(StringComparer.Ordinal);
        foreach (var property in type.Properties)
        {
            var propertyTypeName = property.PropertyType.Name;
            if (property.SetMethod is not null)
            {
                propertyByAccessor[property.SetMethod.Name] = (property.Name, propertyTypeName);
            }

            if (property.GetMethod is not null)
            {
                propertyByAccessor[property.GetMethod.Name] = (property.Name, propertyTypeName);
            }

            if (Il2CppPatterns.LooksLikeTextMember(property.Name) || Il2CppPatterns.IsTextLikeType(propertyTypeName))
            {
                facts.TextMemberNames.Add(property.Name + ":" + propertyTypeName);
            }
        }

        foreach (var field in type.Fields)
        {
            if (Il2CppPatterns.LooksLikeTextMember(field.Name) && Il2CppPatterns.IsTextLikeType(field.FieldType.Name))
            {
                facts.TextMemberNames.Add(field.Name + ":" + field.FieldType.Name);
            }
        }

        var methods = 0;
        foreach (var method in type.Methods)
        {
            if (methods >= MaxMethodsPerType)
            {
                outcome.Warnings.Add("warn: " + type.FullName + " 方法数超过上限 " + MaxMethodsPerType);
                break;
            }

            if (method.Name.StartsWith('<'))
            {
                continue;
            }

            methods++;
            var fact = BuildMethodFact(assemblyFile, type, method, propertyByAccessor, outcome);
            outcome.Methods.Add(fact);
            facts.MethodCount++;
        }

        return facts;
    }

    private static Il2CppMethodFact BuildMethodFact(
        string assemblyFile,
        TypeDefinition type,
        MethodDefinition method,
        Dictionary<string, (string Name, string TypeName)> propertyByAccessor,
        Il2CppScanOutcome outcome)
    {
        var parameters = new List<Il2CppParameterFact>(method.Parameters.Count);
        foreach (var parameter in method.Parameters)
        {
            var typeName = parameter.ParameterType.FullName;
            var simpleName = parameter.ParameterType.Name;
            parameters.Add(new Il2CppParameterFact
            {
                Name = parameter.Name,
                TypeName = typeName,
                IsString = StringTypeNames.Contains(simpleName, StringComparer.Ordinal),
                IsNumeric = NumericTypeNames.Contains(simpleName, StringComparer.Ordinal),
            });
        }

        propertyByAccessor.TryGetValue(method.Name, out var property);
        var isEventAccessor = (method.Name.StartsWith("add_", StringComparison.Ordinal) ||
                               method.Name.StartsWith("remove_", StringComparison.Ordinal))
                              && method.Parameters.Count == 1;

        if (method.Name.StartsWith("add_", StringComparison.Ordinal) && isEventAccessor)
        {
            var handlerType = method.Parameters[0].ParameterType;
            var argumentTypes = new List<string>();
            var handlerSimpleName = handlerType.Name;
            if (handlerType is GenericInstanceType generic)
            {
                foreach (var argument in generic.GenericArguments)
                {
                    argumentTypes.Add(argument.Name);
                }
            }
            else if (method.Parameters[0].ParameterType is { } plain)
            {
                argumentTypes.Add(plain.Name);
            }

            var isStringEvent = argumentTypes.Count == 1 && StringTypeNames.Contains(argumentTypes[0], StringComparer.Ordinal);
            outcome.Events.Add(new Il2CppEventFact
            {
                AssemblyFile = assemblyFile,
                TypeFullName = type.FullName,
                EventName = method.Name.Substring("add_".Length),
                HandlerTypeName = handlerSimpleName,
                HandlerArgumentTypeNames = argumentTypes,
                IsStringEvent = isStringEvent,
            });
        }

        var signature = type.FullName + "::" + method.Name + "(" +
                        string.Join(", ", parameters.Select(p => p.TypeName)) + ")";

        return new Il2CppMethodFact
        {
            AssemblyFile = assemblyFile,
            TypeFullName = type.FullName,
            TypeName = type.Name,
            Namespace = type.Namespace ?? string.Empty,
            Name = method.Name,
            FullName = method.FullName,
            Signature = signature,
            IsConstructor = method.IsConstructor,
            IsStatic = method.IsStatic,
            IsVirtual = method.IsVirtual,
            IsPropertyMember = property.Name is not null,
            PropertyName = property.Name,
            PropertyTypeName = property.TypeName,
            EventName = method.Name.StartsWith("add_", StringComparison.Ordinal) ? method.Name.Substring("add_".Length) : null,
            IsEventAdder = method.Name.StartsWith("add_", StringComparison.Ordinal) && isEventAccessor,
            Parameters = parameters,
            TextParameterName = Il2CppPatterns.PickTextParameter(parameters),
        };
    }

    private static IEnumerable<CustomAttribute> SafeAttributes(TypeDefinition type)
    {
        try
        {
            return type.CustomAttributes.ToArray();
        }
        catch (Exception)
        {
            // 属性表损坏不影响主体扫描。
            return [];
        }
    }
}
