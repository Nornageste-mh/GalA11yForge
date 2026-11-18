using System.Text;
using System.Text.Json;
using GalA11yForge.Core.Models;

namespace GalA11yForge.Generators.Tests;

/// <summary>生成物只含代码/配置/接口：不复制 plan 自由文本，不含媒体，编码与命名空间改写可核。</summary>
public sealed class ContentSafetyTests : GeneratorTestBase
{
    private static readonly string[] AllowedExtensions = [".cs", ".csproj", ".json", ".ps1", ".md"];

    [Fact]
    public async Task Plan_free_text_never_leaks_into_generated_project()
    {
        var plan = Plan(Hook("a", "GameNS.A:B", description: "「快跑，别回头。」这是台词"));
        plan.ManualTodo[0].Title = "待办：朗读「不要回头」";
        plan.ManualTodo[0].Detail = "台词细节：回头就会死";
        plan.Risks[0].Description = "风险台词：快跑";
        plan.Risks[0].Mitigation = "缓解台词：别回头";

        var result = await CreateGenerator().GenerateAsync(Context(), plan, OutDir);

        Assert.True(result.Ok, result.Error?.Message);
        var tree = ReadTree(OutDir);
        Assert.NotEmpty(tree);
        foreach (var (path, text) in TextTree(tree))
        {
            Assert.DoesNotContain("快跑", text, StringComparison.Ordinal);
            Assert.DoesNotContain("别回头", text, StringComparison.Ordinal);
            Assert.DoesNotContain("不要回头", text, StringComparison.Ordinal);
            Assert.DoesNotContain("回头就会死", text, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(path));
        }
    }

    [Fact]
    public async Task Generated_files_use_allowlisted_extensions_and_contain_no_media()
    {
        var result = await CreateGenerator().GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), OutDir);

        Assert.True(result.Ok, result.Error?.Message);
        var tree = ReadTree(OutDir);
        foreach (var path in tree.Keys)
        {
            Assert.Contains(Path.GetExtension(path), AllowedExtensions, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("台词", path, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(tree.Keys, path => path.Contains("/assets/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Hook_source_does_not_copy_plan_description()
    {
        var description = "钩在 SetText 上以便测试描述泄漏";
        var plan = Plan(Hook("a", "GameNS.A:B", description: description));

        var result = await CreateGenerator().GenerateAsync(Context(), plan, OutDir);

        Assert.True(result.Ok, result.Error?.Message);
        var hook = ReadText(Path.Combine(OutDir, "Hooks", "Hook_a.cs"));
        Assert.DoesNotContain(description, hook, StringComparison.Ordinal);
        Assert.Contains("internal const string Target = \"GameNS.A:B\";", hook);
    }

    [Fact]
    public async Task Chinese_workspace_and_game_directory_are_supported()
    {
        var gamePath = Path.Combine(Root, "游戏", "某游戏 中文");
        Directory.CreateDirectory(gamePath);
        var sentinel = Path.Combine(gamePath, "读我.txt");
        File.WriteAllText(sentinel, "游戏目录哨兵");
        var workspace = Path.Combine(Root, "工作 区");
        var output = Path.Combine(workspace, "任务一", "project");
        var context = GameContext.Create(gamePath, workspace, "forge-golden");

        var result = await CreateGenerator().GenerateAsync(
            context, PlanWith(gamePath, Hook("a", "GameNS.A:B")), output);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.True(File.Exists(Path.Combine(output, "Plugin.cs")));
        var profile = JsonDocument.Parse(ReadText(Path.Combine(output, "profile.json"))).RootElement;
        Assert.Equal("某游戏 中文", profile.GetProperty("gameName").GetString());
        Assert.Contains("某游戏 中文 无障碍补丁", ReadText(Path.Combine(output, "Plugin.cs")));
        Assert.Equal(new[] { sentinel }, Directory.GetFileSystemEntries(gamePath));
    }

    [Fact]
    public async Task Encodings_are_stable()
    {
        var result = await CreateGenerator().GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), OutDir);

        Assert.True(result.Ok, result.Error?.Message);
        var tree = ReadTree(OutDir);
        Assert.True(HasUtf8Bom(tree["build.ps1"]), "build.ps1 必须带 UTF-8 BOM（Windows PowerShell 5.1 读中文需要）");
        Assert.False(HasUtf8Bom(tree["Plugin.cs"]), "C# 源文件不应带 BOM");
        Assert.False(HasUtf8Bom(tree["profile.json"]), "JSON 必须无 BOM");
        Assert.False(HasUtf8Bom(tree["Platform/Speech.cs"]), "无 BOM 的桥文件应保持无 BOM");
        Assert.Contains(
            "GalgameSample 无障碍补丁",
            ReadText(Path.Combine(OutDir, "profile.json")),
            StringComparison.Ordinal);

        foreach (var (path, text) in TextTree(tree))
        {
            Assert.False(
                text.Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n'),
                $"{path} 含裸 LF，生成文本应统一 CRLF");
        }
    }

    [Fact]
    public async Task Bridge_namespace_is_replaced_everywhere()
    {
        var result = await CreateGenerator().GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), OutDir);

        Assert.True(result.Ok, result.Error?.Message);
        var tree = ReadTree(OutDir);
        foreach (var (path, text) in TextTree(tree))
        {
            Assert.DoesNotContain("A11yFramework", text, StringComparison.Ordinal);
        }

        Assert.Contains("namespace GalA11y.ForgeGolden", ReadText(Path.Combine(OutDir, "Platform", "Speech.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_placeholder_or_route_tokens_remain()
    {
        var result = await CreateGenerator().GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), OutDir);

        Assert.True(result.Ok, result.Error?.Message);
        foreach (var (path, text) in TextTree(ReadTree(OutDir)))
        {
            Assert.DoesNotContain("{{", text, StringComparison.Ordinal);
            Assert.DoesNotContain("}}", text, StringComparison.Ordinal);
            Assert.DoesNotContain("@@ROUTE", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Csproj_does_not_reference_game_assembly()
    {
        var result = await CreateGenerator().GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), OutDir);

        Assert.True(result.Ok, result.Error?.Message);
        var csproj = ReadText(Path.Combine(OutDir, "ForgeGolden.A11y.csproj"));
        Assert.DoesNotContain("Assembly-CSharp", csproj, StringComparison.Ordinal);
        Assert.Contains("netstandard2.1", csproj, StringComparison.Ordinal);
        Assert.Contains("BepInEx", csproj, StringComparison.Ordinal);
        Assert.Contains("0Harmony", csproj, StringComparison.Ordinal);
        Assert.Contains("Unity.TextMeshPro", csproj, StringComparison.Ordinal);
        Assert.Contains("<Deterministic>true</Deterministic>", csproj, StringComparison.Ordinal);
    }

    private static IEnumerable<(string Path, string Text)> TextTree(IReadOnlyDictionary<string, byte[]> tree)
    {
        foreach (var pair in tree)
        {
            var bytes = pair.Value;
            var offset = HasUtf8Bom(bytes) ? 3 : 0;
            yield return (pair.Key, Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset));
        }
    }

    private static bool HasUtf8Bom(byte[] bytes)
        => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
}
