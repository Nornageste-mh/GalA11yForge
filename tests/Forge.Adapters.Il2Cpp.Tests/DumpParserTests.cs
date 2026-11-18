using GalA11yForge.Adapters.Il2Cpp.Internal;

namespace GalA11yForge.Adapters.Il2Cpp.Tests;

/// <summary>
/// dump.cs 解析器的行为测试。用一段手写的最小 dump 覆盖真实 dump.cs 的关键格式：
/// Image 注释切换程序集、Namespace 注释切换命名空间、字段注释、RVA/Slot 注释、
/// 默认参数值、泛型基类、接口。
/// </summary>
public sealed class DumpParserTests : IDisposable
{
    private readonly string _dumpPath;
    private readonly Il2CppDumpParser _parser = new();

    public DumpParserTests()
    {
        _dumpPath = Path.Combine(Path.GetTempPath(), "gala11y-dump-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(_dumpPath, """
            // Image 1: Elringus.Naninovel.Runtime.dll - 456
            // Namespace: Naninovel
            [Serializable]
            public class TextPrinterManager : OrthoActorManager<TextPrinterMetadata> // TypeDefIndex: 4533
            {
            	// Fields
            	public float BaseRevealSpeed; // 0x10
            	public string Text; // 0x18
            	// Methods
            	// RVA: 0x8BA340 Offset: 0x8BA340 VA: 0x1808BA340 Slot: 68
            	public UniTask PrintTextAsync(string printerId, string text, string authorId, float speed = 1f, AsyncToken asyncToken = default) { }
            	// RVA: 0x8BA860 Offset: 0x8BA860 VA: 0x1808BA860 Slot: 49
            	public void add_OnPrintTextStarted(Action<PrintTextArgs> value) { }
            }

            // Namespace: Naninovel.UI
            public interface IRevealableText
            {
            	// Methods
            	// RVA: 0x9B6DA0 Offset: 0x9B6DA0 VA: 0x1809B6DA0 Slot: 98
            	public abstract void set_Text(string value) { }
            }

            // Namespace: 
            public abstract class SomeBase
            {
            }
            """);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_dumpPath);
        }
        catch (IOException)
        {
            // 临时文件清理失败不影响测试结论。
        }
    }

    private Il2CppScanOutcome Parse() => _parser.Parse(Facts.DumpLayout("C:\\games\\x", _dumpPath), CancellationToken.None);

    [Fact]
    public void Parse_ReadsTypesAndNamespaces()
    {
        var outcome = Parse();

        Assert.Equal(Il2CppSourceKind.Dump, outcome.Source);
        Assert.True(outcome.TypeCount >= 3);
        Assert.True(outcome.Types.ContainsKey("Naninovel.TextPrinterManager"));
        Assert.True(outcome.Types.ContainsKey("Naninovel.UI.IRevealableText"));

        var printer = outcome.Types["Naninovel.TextPrinterManager"];
        Assert.Equal("Naninovel", printer.Namespace);
        Assert.Equal("TextPrinterManager", printer.Name);
        Assert.Equal("OrthoActorManager<TextPrinterMetadata>", printer.BaseTypeName);
        Assert.Equal("naninovel", printer.FrameworkId);
        Assert.Contains(printer.TextMemberNames, m => m.StartsWith("Text:", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_StripsDefaultValuesAndNormalizesAliases()
    {
        var outcome = Parse();
        var method = outcome.Methods.Single(m => m.Name == "PrintTextAsync");

        Assert.Equal(5, method.Parameters.Count);
        Assert.Equal(["printerId", "text", "authorId", "speed", "asyncToken"], method.Parameters.Select(p => p.Name));
        Assert.Equal("System.String", method.Parameters[1].TypeName);
        Assert.Equal("System.Single", method.Parameters[3].TypeName);
        Assert.True(method.Parameters[1].IsString);
        Assert.True(method.Parameters[3].IsNumeric);
        Assert.Equal(0x8BA340, method.Rva);
        Assert.Equal(68, method.Slot);
    }

    [Fact]
    public void Parse_FlagsInterfaces()
    {
        var outcome = Parse();

        Assert.True(outcome.Types["Naninovel.UI.IRevealableText"].IsInterface);
        Assert.True(outcome.Types["SomeBase"].IsAbstract);
        Assert.False(outcome.Types["Naninovel.TextPrinterManager"].IsInterface);
    }

    [Fact]
    public void Parse_KeepsEventAdderShape()
    {
        var outcome = Parse();
        var adder = outcome.Methods.Single(m => m.Name == "add_OnPrintTextStarted");

        Assert.Equal("OnPrintTextStarted", adder.EventName);
        Assert.True(adder.IsEventAdder);
        Assert.Equal(49, adder.Slot);
    }

    [Fact]
    public void Parse_WithoutDumpPathWarnsInsteadOfThrowing()
    {
        var outcome = _parser.Parse(new Il2CppLayout { GameDirectory = "C:\\games\\x" }, CancellationToken.None);

        Assert.Equal(Il2CppSourceKind.Dump, outcome.Source);
        Assert.Empty(outcome.Methods);
        Assert.Contains(outcome.Warnings, w => w.Contains("dump.cs"));
    }
}
