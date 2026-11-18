using System.Text;
using Xunit.Sdk;

namespace Forge.E2E;

public sealed class GenerateTests
{
    private const string SentinelAscii = "FORGE_FIXTURE_LINE_DO_NOT_SHIP_5OMEDAY";
    private const string SentinelChinese = "【合成夹具】这句测试台词不得进入补丁工程。";

    [E2EFact]
    public void Generate_FakeGame_WritesPatchScaffold()
    {
        var sandbox = E2ESandbox.Create("generate");
        var game = E2ESandbox.FixtureRoot;
        Pipeline.Detect(sandbox, game);
        Pipeline.Analyze(sandbox, game);
        Pipeline.Plan(sandbox, game);

        var result = ForgeCli.Run("generate", game, sandbox.Workspace, sandbox.OutRoot);
        E2EAssert.Succeeded(result, "generate", allowPartial: true, hint: $"sandbox：{sandbox.Root}");

        var roots = new[] { sandbox.Workspace, sandbox.OutRoot };
        var generatedRoot = ArtifactLocator.RequireGeneratedRoot(roots);
        var problems = new List<string>();

        var hooks = Directory.EnumerateDirectories(generatedRoot, "Hooks", SearchOption.AllDirectories).FirstOrDefault();
        if (hooks is null || !Directory.EnumerateFiles(hooks, "*.cs", SearchOption.AllDirectories).Any())
        {
            problems.Add("缺少 Hooks/ 目录或 Hooks/*.cs（A5）");
        }

        var speechDirectory = Directory.EnumerateDirectories(generatedRoot, "Speech", SearchOption.AllDirectories).FirstOrDefault();
        var speechOk = (speechDirectory is not null
                        && Directory.EnumerateFiles(speechDirectory, "*", SearchOption.AllDirectories).Any())
                       || Directory.EnumerateFiles(generatedRoot, "*", SearchOption.AllDirectories).Any(file =>
                           Path.GetFileName(file).Contains("Speech", StringComparison.OrdinalIgnoreCase)
                           && IsTextArtifact(file));
        if (!speechOk)
        {
            problems.Add("缺少 Speech/ 目录（含文件）或等价的 Speech 相关源文件（A5）");
        }

        if (!Directory.GetFiles(generatedRoot, "README*", SearchOption.TopDirectoryOnly).Any())
        {
            problems.Add("生成根目录缺少 README*（A5 要求 README-A11Y.txt）");
        }

        if (!Directory.EnumerateFiles(generatedRoot, "uninstall*", SearchOption.AllDirectories).Any())
        {
            problems.Add("缺少 uninstall*（A5 要求 uninstall.ps1）");
        }

        if (!Directory.EnumerateFiles(generatedRoot, "profile.json", SearchOption.AllDirectories).Any())
        {
            problems.Add("缺少 profile.json（A5）");
        }

        if (problems.Count > 0)
        {
            throw new XunitException(
                $"A5：生成目录结构不完整：{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", problems)}"
                + $"{Environment.NewLine}生成根：{generatedRoot}"
                + $"{Environment.NewLine}目录快照：{Environment.NewLine}{ArtifactLocator.Tree(new[] { generatedRoot })}");
        }

        var offenders = FindSentinelFiles(generatedRoot);
        Assert.True(
            offenders.Count == 0,
            $"A5/版权红线：生成物包含假游戏的合成台词原文（哨兵），不得随补丁工程分发："
            + $"{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", offenders)}"
            + $"{Environment.NewLine}生成根：{generatedRoot}");
    }

    private static bool IsTextArtifact(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".md", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> FindSentinelFiles(string root)
    {
        var sentinels = new[]
        {
            Encoding.UTF8.GetBytes(SentinelAscii),
            Encoding.UTF8.GetBytes(SentinelChinese),
            Encoding.Unicode.GetBytes(SentinelAscii),
            Encoding.Unicode.GetBytes(SentinelChinese),
        };

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                offenders.Add($"{Path.GetRelativePath(root, file)}（无法读取：{exception.Message}）");
                continue;
            }

            if (sentinels.Any(sentinel => bytes.AsSpan().IndexOf(sentinel) >= 0))
            {
                offenders.Add(Path.GetRelativePath(root, file));
            }
        }

        return offenders;
    }
}
