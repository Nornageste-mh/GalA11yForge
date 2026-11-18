using System.Security.Cryptography;
using System.Text;

namespace GalA11yForge.Generators;

/// <summary>
/// 从仓库 <c>Framework/</c> 读取语音桥：<c>platform/</c> 下 l1-files.txt 列出的全部 L1 文件，
/// 加上 <c>contract/A11yHost.cs</c>（L2 契约层）。
///
/// <para>
/// 刻意把 l1-files.txt 当作唯一真源（与 <c>tools/compile_check.ps1</c> 同一约定）：
/// 清单与 <c>platform/*.cs</c> 不一致时直接拒绝生成，而不是静默多拷或漏拷。
/// </para>
/// </summary>
public sealed class FrameworkVoiceBridgeSource : IVoiceBridgeSource
{
    private const string ContractRelativePath = "contract/A11yHost.cs";
    private const string ContractOutputPath = "Platform/A11yHost.cs";

    /// <summary>
    /// 按作定制的 L1 文件：它们和具体游戏的控件命名/别名表/场景结构绑定，不能直接复用给新作
    /// （R1 研究结论：UiNav 新作不得接）。生成时跳过，但仍然参与清单一致性校验。
    /// </summary>
    private static readonly HashSet<string> NotReusableForNewGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "UiNav.cs",
    };

    private readonly string _frameworkDirectory;

    public FrameworkVoiceBridgeSource(string frameworkDirectory)
    {
        if (string.IsNullOrWhiteSpace(frameworkDirectory))
        {
            throw new ArgumentException("必须提供 Framework 目录。", nameof(frameworkDirectory));
        }

        _frameworkDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(frameworkDirectory));
    }

    public string Id => "framework";

    /// <summary>实际使用的 Framework 绝对路径（不写进生成物，只用于日志/诊断）。</summary>
    public string FrameworkDirectory => _frameworkDirectory;

    public IReadOnlyList<VoiceBridgeFile> ReadFiles()
    {
        if (!Directory.Exists(_frameworkDirectory))
        {
            throw new DirectoryNotFoundException("Framework 目录不存在：" + _frameworkDirectory);
        }

        var listPath = Path.Combine(_frameworkDirectory, "tools", "l1-files.txt");
        if (!File.Exists(listPath))
        {
            throw new FileNotFoundException(
                "Framework 里缺少 tools/l1-files.txt（L1 文件清单，唯一真源）。", listPath);
        }

        var names = new List<string>();
        foreach (var raw in File.ReadAllLines(listPath, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (!line.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "tools/l1-files.txt 只应列 .cs 文件，实际读到：" + line);
            }

            names.Add(line);
        }

        if (names.Count == 0)
        {
            throw new InvalidDataException("tools/l1-files.txt 里没有任何 L1 文件。");
        }

        if (names.Any(n => string.Equals(n, "A11yHost.cs", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "tools/l1-files.txt 不应包含 A11yHost.cs（契约层单独加入）；清单与契约层职责混淆了。");
        }

        VerifyPlatformSet(names);

        var reusable = names.Where(n => !NotReusableForNewGames.Contains(n)).ToList();
        if (reusable.Count == 0)
        {
            throw new InvalidDataException("排除按作定制文件后，l1-files.txt 里没有可复用的 L1 文件了。");
        }

        var files = new List<VoiceBridgeFile>(reusable.Count + 1);
        foreach (var name in reusable)
        {
            files.Add(ReadOne(
                Path.Combine(_frameworkDirectory, "platform", name),
                "Platform/" + name,
                "Framework/platform/" + name));
        }

        files.Add(ReadOne(
            Path.Combine(_frameworkDirectory, "contract", "A11yHost.cs"),
            ContractOutputPath,
            "Framework/" + ContractRelativePath));

        return files;
    }

    private void VerifyPlatformSet(IReadOnlyCollection<string> listed)
    {
        var platformDir = Path.Combine(_frameworkDirectory, "platform");
        if (!Directory.Exists(platformDir))
        {
            throw new DirectoryNotFoundException("Framework 里缺少 platform/ 目录：" + platformDir);
        }

        var actual = Directory.GetFiles(platformDir, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();

        var listedSet = new HashSet<string>(listed, StringComparer.OrdinalIgnoreCase);
        var actualSet = new HashSet<string>(actual, StringComparer.OrdinalIgnoreCase);

        var missing = listed.Where(n => !actualSet.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var extra = actual.Where(n => !listedSet.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (missing.Count > 0 || extra.Count > 0)
        {
            var detail = new StringBuilder("platform/*.cs 与 tools/l1-files.txt 不一致：");
            if (missing.Count > 0)
            {
                detail.Append("缺 ").Append(string.Join(", ", missing)).Append("；");
            }

            if (extra.Count > 0)
            {
                detail.Append("多 ").Append(string.Join(", ", extra)).Append('；');
            }

            detail.Append("先同步清单，再生成（与 compile_check.ps1 的约定相同）。");
            throw new InvalidDataException(detail.ToString());
        }
    }

    private static VoiceBridgeFile ReadOne(string path, string relativePath, string sourceId)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("缺少语音桥源文件：" + sourceId, path);
        }

        var bytes = File.ReadAllBytes(path);
        var bom = bytes.Length >= 3
                  && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var offset = bom ? 3 : 0;

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("语音桥源文件不是合法 UTF-8：" + sourceId, ex);
        }

        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new VoiceBridgeFile(relativePath, text, bom, sourceId, sha);
    }
}
