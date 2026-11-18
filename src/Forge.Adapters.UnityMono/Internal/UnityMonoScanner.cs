using System.Text;
using GalA11yForge.Core.Results;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace GalA11yForge.Adapters.UnityMono.Internal;

/// <summary>
/// Cecil 只读扫描器。两遍 IL：
/// 第一遍收集方法事实/文本 sink/打字机 sink/类型字段；第二遍只补“程序集内调用者计数”。
/// 所有上限都是结构化的（类型/方法/指令数），不按墙钟时间截断，保证同输入同输出。
/// </summary>
internal sealed class UnityMonoScanner
{
    public const int MaxTypesPerAssembly = 30_000;
    public const int MaxMethodsPerAssembly = 250_000;
    public const int MaxInstructionsPerMethod = 100_000;

    public ForgeResult<UnityMonoScanOutcome> Scan(
        UnityMonoGameLayout layout,
        string language,
        string languageSource,
        double minConfidence,
        CancellationToken cancellationToken)
    {
        var outcome = new UnityMonoScanOutcome
        {
            Layout = layout,
            Language = language,
            LanguageSource = languageSource,
            MinConfidence = minConfidence,
            Notes = layout.Notes,
        };

        cancellationToken.ThrowIfCancellationRequested();

        var primary = ScanAssembly(layout.PrimaryAssemblyPath, isPrimary: true, cancellationToken);
        if (primary.Error is not null)
        {
            return ForgeResult<UnityMonoScanOutcome>.Failure(primary.Error);
        }

        outcome.Assemblies.Add(primary.Scan!);
        foreach (var note in primary.Scan!.Issues)
        {
            outcome.Notes.Add(note);
        }

        foreach (var path in layout.AdditionalAssemblyPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var additional = ScanAssembly(path, isPrimary: false, cancellationToken);
            if (additional.Error is not null)
            {
                outcome.Notes.Add(
                    $"附加程序集读取失败（已跳过）：{Path.GetFileName(path)} — {additional.Error.Message}");
                continue;
            }

            outcome.Assemblies.Add(additional.Scan!);
            foreach (var note in additional.Scan!.Issues)
            {
                outcome.Notes.Add(note);
            }
        }

        return ForgeResult<UnityMonoScanOutcome>.Success(outcome);
    }

    private static (UnityMonoAssemblyScan? Scan, ForgeError? Error) ScanAssembly(
        string path,
        bool isPrimary,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(path);
        AssemblyDefinition assembly;
        try
        {
            var resolver = new UnityMonoAssemblyResolver();
            var managedDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(managedDirectory))
            {
                resolver.AddSearchDirectory(managedDirectory);
                var plugins = Path.Combine(managedDirectory, "Plugins");
                if (Directory.Exists(plugins))
                {
                    resolver.AddSearchDirectory(plugins);
                }
            }

            assembly = AssemblyDefinition.ReadAssembly(
                path,
                new ReaderParameters
                {
                    InMemory = true,
                    ReadSymbols = false,
                    // 游戏程序集引用 UnityEngine.*，默认 resolver 只在进程目录找。
                    // Deferred 让 ReadAssembly 不因缺一个引用就整体失败，后续解析失败会被局部 catch。
                    ReadingMode = ReadingMode.Deferred,
                    AssemblyResolver = resolver,
                });
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or
                                       ArgumentException or NotSupportedException or InvalidOperationException or
                                       AssemblyResolutionException)
        {
            var code = ex is IOException or UnauthorizedAccessException
                ? ForgeErrorCodes.Io
                : ForgeErrorCodes.ArgumentInvalid;
            return (null, ForgeError.Create(
                code,
                $"无法读取 Unity Mono 程序集 {fileName}：{ex.Message}",
                "确认程序集未被加密/裁剪；破解或加密的程序集需要人工处理。"));
        }

        using (assembly)
        {
            var scan = new UnityMonoAssemblyScan
            {
                FilePath = path,
                FileName = fileName,
                AssemblyName = assembly.Name.Name,
                IsPrimary = isPrimary,
            };
            scan.AssemblyVersion = assembly.Name.Version?.ToString();

            var allMethods = new List<MethodDefinition>(1024);
            var flattened = new List<TypeDefinition>(1024);
            try
            {
                FlattenTypes(assembly.MainModule, flattened, scan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
            {
                return (null, ForgeError.Create(
                    ForgeErrorCodes.Io,
                    $"程序集 {fileName} 的元数据损坏：{ex.Message}",
                    "该程序集可能被混淆或截断；需要人工确认。"));
            }

            foreach (var type in flattened)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scan.TypesScanned++;

                var typeFacts = BuildTypeFacts(type);
                scan.Types[typeFacts.FullName] = typeFacts;

                if (typeFacts.TextFieldCount > 0)
                {
                    // 可能有对话管线；先记下，等所有方法扫完再决定是否给 S1。
                }

                foreach (var method in type.Methods)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (scan.MethodsScanned >= MaxMethodsPerAssembly)
                    {
                        scan.Truncated = true;
                        break;
                    }

                    scan.MethodsScanned++;
                    if (!method.HasBody)
                    {
                        continue;
                    }

                    allMethods.Add(method);
                    scan.MethodsWithBody++;

                    var facts = ScanMethod(scan, typeFacts, method, cancellationToken);
                    if (facts is null)
                    {
                        continue;
                    }

                    if (ShouldKeep(facts))
                    {
                        scan.Methods.Add(facts);
                    }
                }

                if (scan.MethodsScanned >= MaxMethodsPerAssembly)
                {
                    scan.Truncated = true;
                    break;
                }
            }

            // 同一类型：TMP 字段 + 直接文本写入 + string 参数打字机入口 → 类型级对话管线。
            foreach (var facts in scan.Methods)
            {
                // DOText/SetText 这类入口不一定有 string 参数或 sink；类型已确认是对话管线时，
                // 方法名含 text token 也视为管线内入口（R17：DOText 是每句入口，不能被 S3 漏掉）。
                if ((facts.IsTextRelevant || MonoTextPatterns.HasTextToken(facts.MethodName)) &&
                    scan.Types.TryGetValue(facts.TypeFullName, out var typeFacts) &&
                    typeFacts.IsDialoguePipeline)
                {
                    facts.TypeLevelPipeline = true;
                }
            }

            CountInAssemblyCallers(scan, allMethods, cancellationToken);
            return (scan, null);
        }
    }

    private static void FlattenTypes(
        ModuleDefinition module,
        List<TypeDefinition> output,
        UnityMonoAssemblyScan scan,
        CancellationToken cancellationToken)
    {
        var stack = new Stack<TypeDefinition>();
        for (var i = module.Types.Count - 1; i >= 0; i--)
        {
            stack.Push(module.Types[i]);
        }

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output.Count >= MaxTypesPerAssembly)
            {
                scan.Truncated = true;
                return;
            }

            var type = stack.Pop();
            output.Add(type);
            for (var i = type.NestedTypes.Count - 1; i >= 0; i--)
            {
                stack.Push(type.NestedTypes[i]);
            }
        }
    }

    private static UnityMonoTypeFacts BuildTypeFacts(TypeDefinition type)
    {
        var textFields = new List<string>();
        foreach (var field in type.Fields)
        {
            try
            {
                if (IsTextComponent(field.FieldType))
                {
                    textFields.Add(field.Name);
                }
            }
            catch (Exception ex) when (ex is AssemblyResolutionException or InvalidOperationException or NotSupportedException)
            {
                // 解析不了的字段类型不参与 S4，避免把坏引用当证据。
            }
        }

        return new UnityMonoTypeFacts
        {
            FullName = type.FullName,
            Name = type.Name,
            Namespace = type.Namespace ?? string.Empty,
            TextFieldCount = textFields.Count,
            TextFieldNames = textFields,
            FrameworkId = MatchFramework(type),
            IsCustomDialogueType = MonoTextPatterns.HasCustomDialogueTrace(type.FullName),
            MethodCount = type.Methods.Count,
        };
    }

    private static string? MatchFramework(TypeDefinition type)
    {
        var direct = MonoTextPatterns.MatchKnownFramework(type.FullName);
        if (direct is not null)
        {
            return direct;
        }

        try
        {
            var baseType = type.BaseType;
            for (var depth = 0; baseType is not null && depth < 6; depth++)
            {
                var matched = MonoTextPatterns.MatchKnownFramework(baseType.FullName);
                if (matched is not null)
                {
                    return matched;
                }

                var resolved = baseType.Resolve();
                baseType = resolved?.BaseType;
            }
        }
        catch (Exception ex) when (ex is AssemblyResolutionException or InvalidOperationException or NotSupportedException)
        {
            // 基类解析失败不阻断扫描。
        }

        return null;
    }

    private static UnityMonoMethodFacts? ScanMethod(
        UnityMonoAssemblyScan scan,
        UnityMonoTypeFacts typeFacts,
        MethodDefinition method,
        CancellationToken cancellationToken)
    {
        var parameters = new List<UnityMonoParameterFact>(method.Parameters.Count);
        var hasString = false;
        var hasNum = false;
        foreach (var parameter in method.Parameters)
        {
            var typeName = parameter.ParameterType.FullName;
            var isString = typeName is "System.String" or "string";
            var isNum = typeName is "System.Int32" or "System.UInt32" or "int" &&
                        string.Equals(parameter.Name, "num", StringComparison.OrdinalIgnoreCase);
            hasString |= isString;
            hasNum |= isNum;
            parameters.Add(new UnityMonoParameterFact
            {
                Name = parameter.Name ?? string.Empty,
                TypeFullName = typeName,
                IsString = isString,
                IsInt32NamedNum = isNum,
            });
        }

        var sinkCalls = new List<UnityMonoSinkHit>();
        var typewriterCalls = new List<UnityMonoSinkHit>();
        var ldstr = 0;
        var cjk = 0;
        var nonDialogue = 0;
        var maxLiteral = 0;
        var instructions = 0;
        var truncated = false;

        try
        {
            foreach (var instruction in method.Body.Instructions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++instructions > MaxInstructionsPerMethod)
                {
                    truncated = true;
                    break;
                }

                if (instruction.OpCode.Code == Code.Ldstr && instruction.Operand is string literal)
                {
                    ldstr++;
                    maxLiteral = Math.Max(maxLiteral, literal.Length);
                    if (MonoTextPatterns.ContainsCjk(literal))
                    {
                        cjk++;
                    }

                    if (MonoTextPatterns.IsNonDialogueLiteral(literal))
                    {
                        nonDialogue++;
                    }
                }

                if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj) ||
                    instruction.Operand is not MethodReference callee)
                {
                    continue;
                }

                var isSink = IsTextSink(callee);
                var isTypewriter = IsTypewriterSink(callee);
                if (!isSink && !isTypewriter)
                {
                    continue;
                }

                var api = callee.DeclaringType.FullName + "::" + callee.Name;
                var index = FindStringArgumentIndex(callee);
                var (source, constantLength) = index > 0
                    ? UnityMonoIlStack.ClassifyArgument(instruction, index)
                    : ("unknown", 0);

                var hit = new UnityMonoSinkHit
                {
                    Api = api,
                    Offset = instruction.Offset,
                    ArgumentSource = source,
                    ConstantLength = constantLength,
                };

                if (isSink)
                {
                    sinkCalls.Add(hit);
                    typeFacts.HasDirectTextWriter = true;
                    scan.SinkApiCounts[api] = scan.SinkApiCounts.TryGetValue(api, out var count) ? count + 1 : 1;
                }

                if (isTypewriter)
                {
                    typewriterCalls.Add(hit);
                    if (hasString)
                    {
                        typeFacts.HasStringTypewriterEntry = true;
                    }

                    scan.TypewriterApiCounts[api] =
                        scan.TypewriterApiCounts.TryGetValue(api, out var count) ? count + 1 : 1;
                }
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            scan.Issues.Add($"IL 扫描失败（已跳过）：{method.FullName} — {ex.Message}");
            return null;
        }

        if (truncated)
        {
            scan.Truncated = true;
            scan.Issues.Add($"方法 IL 超限（已截断到 {MaxInstructionsPerMethod} 条）：{method.FullName}");
        }

        scan.LdstrTotal += ldstr;
        if (cjk > 0)
        {
            scan.CjkLiteralMethods++;
        }

        return new UnityMonoMethodFacts
        {
            AssemblyFile = scan.FileName,
            AssemblyName = scan.AssemblyName,
            TypeFullName = method.DeclaringType.FullName,
            TypeName = method.DeclaringType.Name,
            Namespace = method.DeclaringType.Namespace ?? string.Empty,
            MethodName = method.Name,
            FullName = method.FullName,
            Signature = BuildSignature(method),
            Parameters = parameters,
            HasStringParameter = hasString,
            HasInt32NumParameter = hasNum,
            IsConstructor = method.IsConstructor,
            IsGenericMethod = method.HasGenericParameters,
            TextFieldCount = typeFacts.TextFieldCount,
            TextFieldNames = typeFacts.TextFieldNames,
            LdstrCount = ldstr,
            CjkLiteralCount = cjk,
            NonDialogueLiteralCount = nonDialogue,
            MaxLiteralLength = maxLiteral,
            TextSinkCalls = sinkCalls,
            TypewriterSinkCalls = typewriterCalls,
            IsSemanticName = MonoTextPatterns.HasSemanticName(method.Name),
            IsUiName = MonoTextPatterns.HasUiName(method.DeclaringType.Name),
            IsDebugName = MonoTextPatterns.HasDebugName(method.Name) ||
                          MonoTextPatterns.HasDebugName(method.DeclaringType.Name),
            IsChoiceLike = MonoTextPatterns.IsChoiceName(method.Name) ||
                           MonoTextPatterns.IsChoiceName(method.DeclaringType.Name),
            IsMenuLike = MonoTextPatterns.IsMenuName(method.Name) ||
                        MonoTextPatterns.IsMenuName(method.DeclaringType.Name),
            FrameworkId = typeFacts.FrameworkId,
            IsCustomDialogueType = typeFacts.IsCustomDialogueType,
            InstructionCount = instructions,
        };
    }

    private static bool ShouldKeep(UnityMonoMethodFacts facts)
    {
        if (facts.IsConstructor || facts.IsGenericMethod)
        {
            return false;
        }

        if (facts.HasStringParameter || facts.HasDirectTextSink || facts.HasTypewriterSink ||
            facts.IsSemanticName || facts.IsChoiceLike)
        {
            return true;
        }

        // 0.2.48：Utage（宴）正文管线靠属性链传文本（AdvMessageWindow.PageTextChange），
        // 没有 string 形参也不是 Unity 文本 sink，必须按类型白名单保留，否则分析器看不到每句入口。
        if (MonoTextPatterns.IsUtageDialogueType(facts.TypeFullName))
        {
            return true;
        }

        // 有文本字段的类型（如 UIText）保留全部方法，让类型级 S1 有完整上下文；
        // 其它类型只保留有文本形状的方法，避免把整个 UI 库塞进候选。
        return facts.TextFieldCount > 0 && (facts.IsUiName || facts.FrameworkId is not null || facts.IsCustomDialogueType);
    }

    private static void CountInAssemblyCallers(
        UnityMonoAssemblyScan scan,
        List<MethodDefinition> allMethods,
        CancellationToken cancellationToken)
    {
        var byFullName = new Dictionary<string, UnityMonoMethodFacts>(StringComparer.Ordinal);
        foreach (var facts in scan.Methods)
        {
            byFullName.TryAdd(facts.FullName, facts);
        }

        if (byFullName.Count == 0)
        {
            return;
        }

        foreach (var method in allMethods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!method.HasBody)
            {
                continue;
            }

            try
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj) ||
                        instruction.Operand is not MethodReference callee ||
                        !byFullName.TryGetValue(callee.FullName, out var facts))
                    {
                        continue;
                    }

                    facts.CallerCount++;
                    facts.AllCallersInitLike &= IsInitLike(method);
                    facts.AnyCallerChoiceLike |= MonoTextPatterns.IsChoiceName(method.Name) ||
                                                 MonoTextPatterns.IsChoiceName(method.DeclaringType.Name);
                    if (facts.CallerSamples.Count < 5)
                    {
                        var sample = method.DeclaringType.Name + "::" + method.Name;
                        if (!facts.CallerSamples.Contains(sample, StringComparer.Ordinal))
                        {
                            facts.CallerSamples.Add(sample);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
            {
                // 单个方法的调用边失败不影响其它统计。
            }
        }
    }

    private static bool IsInitLike(MethodDefinition method)
    {
        var name = method.Name;
        if (method.IsConstructor)
        {
            return true;
        }

        return name is "Awake" or "Start" or "OnEnable" or "Setup" ||
               name.StartsWith("Init", StringComparison.Ordinal) ||
               name.Contains("Initialize", StringComparison.Ordinal);
    }

    private static string BuildSignature(MethodDefinition method)
    {
        var builder = new StringBuilder();
        builder.Append(method.ReturnType.FullName).Append(' ')
            .Append(method.DeclaringType.FullName).Append("::").Append(method.Name).Append('(');
        for (var i = 0; i < method.Parameters.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            var parameter = method.Parameters[i];
            builder.Append(parameter.ParameterType.FullName);
            if (!string.IsNullOrEmpty(parameter.Name))
            {
                builder.Append(' ').Append(parameter.Name);
            }
        }

        builder.Append(')');
        return builder.ToString();
    }

    private static bool IsTextSink(MethodReference method)
    {
        if (method.Name is not ("set_text" or "SetText"))
        {
            return false;
        }

        return IsTextComponent(method.DeclaringType);
    }

    private static bool IsTypewriterSink(MethodReference method)
    {
        if (method.Name.Equals("DOText", StringComparison.Ordinal))
        {
            var declaring = method.DeclaringType?.FullName ?? string.Empty;
            return declaring.Contains("Tween", StringComparison.OrdinalIgnoreCase) ||
                   declaring.Contains("DOTween", StringComparison.OrdinalIgnoreCase) ||
                   declaring.Contains("ShortcutExtensions", StringComparison.OrdinalIgnoreCase) ||
                   declaring.Contains("TMPro", StringComparison.OrdinalIgnoreCase) ||
                   declaring.Contains("TextMeshPro", StringComparison.OrdinalIgnoreCase);
        }

        return method.Name is "TypeText" or "Typewriter" or "PlayTypewriter" or "StartTypewriter";
    }

    private static int FindStringArgumentIndex(MethodReference method)
    {
        for (var i = 0; i < method.Parameters.Count; i++)
        {
            if (method.Parameters[i].ParameterType.FullName is "System.String" or "string")
            {
                return i + 1;
            }
        }

        return method.Parameters.Count == 1 ? 1 : 0;
    }

    private static bool IsTextComponent(TypeReference type, int depth = 0)
    {
        var fullName = type.FullName;
        if (fullName is "TMPro.TMP_Text" or "TMPro.TextMeshProUGUI" or "UnityEngine.UI.Text" or
            "UnityEngine.TextMesh" or "TMPro.TMP_SubMeshUI")
        {
            return true;
        }

        if (fullName.Contains("TMPro.TextMeshPro", StringComparison.Ordinal) ||
            fullName.Contains("TMPro.TMP_Text", StringComparison.Ordinal) ||
            fullName.Contains("UnityEngine.UI.Text", StringComparison.Ordinal))
        {
            return true;
        }

        if (depth >= 6)
        {
            return false;
        }

        try
        {
            var definition = type.Resolve();
            if (definition?.BaseType is not null && definition.BaseType.FullName != fullName)
            {
                return IsTextComponent(definition.BaseType, depth + 1);
            }
        }
        catch (Exception ex) when (ex is AssemblyResolutionException or InvalidOperationException or NotSupportedException)
        {
            // 外部 Unity/TMP 程序集不在扫描目录时解析会失败，按非文本组件处理。
        }

        return false;
    }
}

/// <summary>
/// 从游戏 Managed 目录解析 Unity/第三方引用；解析失败不缓存、不中断扫描。
/// 来源：实验原型 src/experiments/unity-mono-analyzer/src/Analysis/AssemblyScanner.cs（RecordingResolver），
/// 适配器只保留“记录 + 上抛给局部 catch”的最小行为。
/// </summary>
internal sealed class UnityMonoAssemblyResolver : DefaultAssemblyResolver
{
    public List<string> Failures { get; } = new();

    public override AssemblyDefinition Resolve(AssemblyNameReference name)
    {
        try
        {
            return base.Resolve(name);
        }
        catch (AssemblyResolutionException)
        {
            Record(name);
            throw;
        }
    }

    public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        try
        {
            return base.Resolve(name, parameters);
        }
        catch (AssemblyResolutionException)
        {
            Record(name);
            throw;
        }
    }

    private void Record(AssemblyNameReference name)
    {
        if (!Failures.Contains(name.Name))
        {
            Failures.Add(name.Name);
        }
    }
}
