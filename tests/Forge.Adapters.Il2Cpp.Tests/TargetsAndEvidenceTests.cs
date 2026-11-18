using GalA11yForge.Adapters.Il2Cpp.Internal;

namespace GalA11yForge.Adapters.Il2Cpp.Tests;

/// <summary>补丁目标字符串（Source/Target）的序列化与运行时证据 CSV 的解析。</summary>
public sealed class TargetsAndEvidenceTests
{
    [Fact]
    public void BuildSource_RoundTripsParameterNamesAndTypes()
    {
        var method = Facts.Method(
            "Naninovel.TextPrinterManager",
            "PrintTextAsync",
            Facts.Param("printerId", "System.String"),
            Facts.Param("text", "System.String"),
            Facts.Param("speed", "System.Single"));

        var source = Il2CppTargets.BuildSource(method, "Elringus.Naninovel.Runtime.dll");
        var target = Il2CppTargets.TryParse(source);

        Assert.NotNull(target);
        Assert.Equal("Elringus.Naninovel.Runtime.dll", target!.AssemblyFile);
        Assert.Equal("Naninovel.TextPrinterManager", target.TypeFullName);
        Assert.Equal("PrintTextAsync", target.MethodName);
        Assert.Equal(["System.String", "System.String", "System.Single"], target.ParameterTypes);
        Assert.Equal(["printerId", "text", "speed"], target.ParameterNames);
        Assert.Equal("Naninovel.TextPrinterManager", target.CSharpTypeName);
        Assert.StartsWith("il2cpp:", source);
        Assert.DoesNotContain("|", target.Target[..target.Target.IndexOf('(')]);
    }

    [Fact]
    public void TryParse_RejectsMalformedTargets()
    {
        Assert.Null(Il2CppTargets.TryParse(null));
        Assert.Null(Il2CppTargets.TryParse(""));
        Assert.Null(Il2CppTargets.TryParse("Naninovel.UITextPrinter::set_Text(System.String|value)"));
        Assert.Null(Il2CppTargets.TryParse("il2cpp:NoBang::set_Text(System.String|value)"));
        Assert.Null(Il2CppTargets.TryParse(new string('x', Il2CppTargets.MaxTargetLength + 1)));
    }

    [Fact]
    public void CanGenerateHook_RejectsGenericAndNestedDeclaringTypes()
    {
        var generic = Il2CppTargets.TryParse(
            "il2cpp:Assembly-CSharp.dll!Foo.Bar<T>::Run(System.String|text)")!;
        var nested = Il2CppTargets.TryParse(
            "il2cpp:Assembly-CSharp.dll!Foo/Inner::Run(System.String|text)")!;
        var noString = Il2CppTargets.TryParse(
            "il2cpp:Assembly-CSharp.dll!Foo.Bar::Run(System.Int32|count)")!;
        var unnamedString = Il2CppTargets.TryParse(
            "il2cpp:Assembly-CSharp.dll!Foo.Bar::Run(System.String)")!;
        var good = Il2CppTargets.TryParse(
            "il2cpp:Assembly-CSharp.dll!Foo.Bar::Run(System.String|text)")!;

        Assert.False(generic.CanGenerateHook);
        Assert.False(nested.CanGenerateHook);
        Assert.False(noString.CanGenerateHook);
        Assert.False(unnamedString.CanGenerateHook);
        Assert.True(good.CanGenerateHook);
    }

    [Fact]
    public void IsSimpleType_OnlyAllowsPrimitives()
    {
        Assert.True(Il2CppTargets.IsSimpleType("System.String"));
        Assert.True(Il2CppTargets.IsSimpleType("System.Single"));
        Assert.False(Il2CppTargets.IsSimpleType("Naninovel.AsyncToken"));
        Assert.False(Il2CppTargets.IsSimpleType("System.Action`1"));
    }

    [Fact]
    public void NormalizeSlug_FallsBackWhenCandidateIsInvalid()
    {
        Assert.Equal("my-game", Il2CppTargets.NormalizeSlug("my-game", "C:\\games\\x"));
        Assert.Equal("siliconheart", Il2CppTargets.NormalizeSlug("BAD SLUG!", "C:\\games\\SiliconHeart"));
    }

    [Fact]
    public void MethodNameOf_HandlesSignatures()
    {
        Assert.Equal("PrintTextAsync", Il2CppRuntimeEvidence.MethodNameOf("TextPrinterManager.PrintTextAsync(string printerId, string text)"));
        Assert.Equal("set_Text", Il2CppRuntimeEvidence.MethodNameOf("set_Text"));
        Assert.Equal(string.Empty, Il2CppRuntimeEvidence.MethodNameOf("   "));
    }

    [Fact]
    public void RuntimeEvidence_LoadsRowsAndFindsByTypeAndMethod()
    {
        var path = Path.Combine(Path.GetTempPath(), "gala11y-evidence-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, """
            target,type,runtime_calls,runtime_nonempty,runtime_verdict,confidence
            "UITextPrinter.set_Text(string)","Naninovel.UITextPrinter",6,1,"有台词",高
            RevealableTextPrinterPanel.set_PrintedText(string),Naninovel.UI.RevealableTextPrinterPanel,14,1,有台词,高
            RevealableTMProText.set_Text(string),Naninovel.UI.RevealableTMProText,0,0,运行时不触发,低
            """);

        try
        {
            var evidence = Il2CppRuntimeEvidence.TryLoad(path);

            Assert.NotNull(evidence);
            Assert.Equal(3, evidence!.RowCount);
            Assert.Empty(evidence.Warnings);

            var ui = evidence.Find("Naninovel.UITextPrinter", "set_Text");
            Assert.NotNull(ui);
            Assert.Equal(6, ui!.Calls);
            Assert.Equal(1, ui.NonEmptyHits);
            Assert.Equal("高", ui.Confidence);
            Assert.True(ui.Tested);

            var missed = evidence.Find("Naninovel.UI.RevealableTMProText", "set_Text");
            Assert.NotNull(missed);
            Assert.Equal(0, missed!.Calls);
            Assert.False(missed.NonEmptyHits > 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RuntimeEvidence_RejectsCsvWithoutRequiredColumns()
    {
        var path = Path.Combine(Path.GetTempPath(), "gala11y-evidence-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, "foo,bar\n1,2\n");

        try
        {
            var evidence = Il2CppRuntimeEvidence.TryLoad(path);

            Assert.NotNull(evidence);
            Assert.True(evidence!.IsEmpty);
            Assert.Contains(evidence.Warnings, w => w.Contains("target"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RuntimeEvidence_MissingFileReturnsNull()
    {
        Assert.Null(Il2CppRuntimeEvidence.TryLoad(null));
        Assert.Null(Il2CppRuntimeEvidence.TryLoad(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv")));
    }
}
