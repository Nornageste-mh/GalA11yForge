using GalA11yForge.Core.Models;
using GalA11yForge.Adapters.Il2Cpp.Internal;

namespace GalA11yForge.Adapters.Il2Cpp.Tests;

/// <summary>
/// 手搓扫描事实的小工具。测试**不依赖**真实游戏目录：
/// 只验证适配器在给定元数据上做出什么判断，真机测试在 RealGameTests 里单独跑。
/// </summary>
internal static class Facts
{
    public static Il2CppParameterFact Param(string name, string type) => new()
    {
        Name = name,
        TypeName = type,
        IsString = type == "System.String",
        IsNumeric = type is "System.Single" or "System.Int32" or "System.Double",
    };

    public static Il2CppMethodFact Method(
        string typeFullName,
        string name,
        params Il2CppParameterFact[] parameters) => Build(typeFullName, name, parameters, null, null);

    public static Il2CppMethodFact Setter(string typeFullName, string propertyName, string parameterType = "System.String") =>
        Build(typeFullName, "set_" + propertyName, [Param("value", parameterType)], propertyName, parameterType);

    public static Il2CppMethodFact EventAdder(string typeFullName, string eventName, string handlerType) =>
        Build(typeFullName, "add_" + eventName, [Param("value", handlerType)], null, null, eventName);

    private static Il2CppMethodFact Build(
        string typeFullName,
        string name,
        Il2CppParameterFact[] parameters,
        string? propertyName,
        string? propertyType,
        string? eventName = null)
    {
        var dot = typeFullName.LastIndexOf('.');
        return new Il2CppMethodFact
        {
            AssemblyFile = "Elringus.Naninovel.Runtime.dll",
            TypeFullName = typeFullName,
            TypeName = dot < 0 ? typeFullName : typeFullName[(dot + 1)..],
            Namespace = dot < 0 ? string.Empty : typeFullName[..dot],
            Name = name,
            FullName = typeFullName + "." + name,
            Signature = typeFullName + "::" + name + "(" + string.Join(", ", parameters.Select(p => p.TypeName)) + ")",
            Parameters = parameters.ToList(),
            TextParameterName = Il2CppPatterns.PickTextParameter(parameters),
            IsPropertyMember = propertyName is not null,
            PropertyName = propertyName,
            PropertyTypeName = propertyType,
            IsEventAdder = eventName is not null,
            EventName = eventName,
        };
    }

    public static Il2CppTypeFact Type(
        string fullName,
        string? framework = null,
        bool isUi = false,
        bool isInterface = false,
        params string[] textMembers)
    {
        var dot = fullName.LastIndexOf('.');
        var t = new Il2CppTypeFact
        {
            AssemblyFile = "Elringus.Naninovel.Runtime.dll",
            FullName = fullName,
            Name = dot < 0 ? fullName : fullName[(dot + 1)..],
            Namespace = dot < 0 ? string.Empty : fullName[..dot],
            FrameworkId = framework,
            IsUiType = isUi,
            IsInterface = isInterface,
        };
        t.TextMemberNames.AddRange(textMembers);
        return t;
    }

    public static Il2CppEventFact Event(string typeFullName, string eventName, string handlerType, params string[] arguments) => new()
    {
        AssemblyFile = "Elringus.Naninovel.Runtime.dll",
        TypeFullName = typeFullName,
        EventName = eventName,
        HandlerTypeName = handlerType,
        HandlerArgumentTypeNames = arguments.ToList(),
    };

    public static Il2CppLayout InteropLayout(string gameDirectory) => new()
    {
        GameDirectory = gameDirectory,
        GameAssemblyPath = Path.Combine(gameDirectory, "GameAssembly.dll"),
        InteropDirectory = Path.Combine(gameDirectory, "BepInEx", "interop"),
        InteropAssemblyCount = 10,
        InteropTotalBytes = 1024,
        InteropAssemblyHash = "deadbeefdeadbeef",
    };

    public static Il2CppLayout DumpLayout(string gameDirectory, string dumpPath) => new()
    {
        GameDirectory = gameDirectory,
        GameAssemblyPath = Path.Combine(gameDirectory, "GameAssembly.dll"),
        DumpPath = dumpPath,
    };

    /// <summary>模拟一次 interop 扫描：Naninovel 的已知通道 + 说话人名面板 + 事件 + 接口代理。</summary>
    public static Il2CppScanOutcome NaninovelScan(Il2CppSourceKind source = Il2CppSourceKind.Interop)
    {
        var outcome = new Il2CppScanOutcome
        {
            Source = source,
            Layout = InteropLayout("C:\\games\\siliconheart"),
            AssemblyCount = 2,
            Elapsed = TimeSpan.FromSeconds(1),
        };

        outcome.Types["Naninovel.TextPrinterManager"] = Type("Naninovel.TextPrinterManager", "naninovel", textMembers: ["Text"]);
        outcome.Types["Naninovel.UITextPrinter"] = Type("Naninovel.UITextPrinter", "naninovel", isUi: true);
        outcome.Types["Naninovel.UI.RevealableTextPrinterPanel"] = Type("Naninovel.UI.RevealableTextPrinterPanel", "naninovel", isUi: true);
        outcome.Types["Naninovel.UI.RevealableUIText"] = Type("Naninovel.UI.RevealableUIText", "naninovel", isUi: true);
        outcome.Types["Naninovel.UI.UITextPrinterPanel"] = Type("Naninovel.UI.UITextPrinterPanel", "naninovel", isUi: true);
        outcome.Types["Naninovel.UI.AuthorNamePanel"] = Type("Naninovel.UI.AuthorNamePanel", "naninovel", isUi: true);
        outcome.Types["Naninovel.ITextPrinterManager"] = Type("Naninovel.ITextPrinterManager", "naninovel", isInterface: true);

        outcome.Methods.Add(Method(
            "Naninovel.TextPrinterManager",
            "PrintTextAsync",
            Param("printerId", "System.String"),
            Param("text", "System.String"),
            Param("authorId", "System.String"),
            Param("speed", "System.Single"),
            Param("asyncToken", "AsyncToken")));
        outcome.Methods.Add(EventAdder("Naninovel.TextPrinterManager", "OnPrintTextStarted", "System.Action`1"));
        outcome.Methods.Add(Setter("Naninovel.UITextPrinter", "Text"));
        outcome.Methods.Add(Setter("Naninovel.UI.RevealableTextPrinterPanel", "PrintedText"));
        outcome.Methods.Add(Setter("Naninovel.UI.RevealableTextPrinterPanel", "AuthorNameText"));
        outcome.Methods.Add(Setter("Naninovel.UI.RevealableUIText", "Text"));
        outcome.Methods.Add(Setter("Naninovel.UI.UITextPrinterPanel", "AuthorNameText"));
        outcome.Methods.Add(Setter("Naninovel.UI.AuthorNamePanel", "Text"));
        outcome.Methods.Add(Setter("Naninovel.ITextPrinterManager", "Text"));

        outcome.Events.Add(Event("Naninovel.TextPrinterManager", "OnPrintTextStarted", "System.Action`1", "Naninovel.PrintTextArgs"));
        outcome.TypeCount = outcome.Types.Count;
        outcome.MethodCount = outcome.Methods.Count;
        return outcome;
    }

    public static GameContext Context(string gameDirectory, string workspaceRoot, string slug = "sh") =>
        GameContext.Create(gameDirectory, workspaceRoot, slug);
}
