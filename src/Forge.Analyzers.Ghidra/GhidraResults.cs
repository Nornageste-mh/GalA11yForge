using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GalA11yForge.Analyzers.Ghidra;

/// <summary>CandidateHookDump.java 的 JSON 模型（schema gala11y.ghidra.candidates/1）。</summary>
public sealed class GhidraCandidatesDocument
{
    public string Schema { get; set; } = "";
    public string GeneratedAt { get; set; } = "";
    public GhidraToolInfo Tool { get; set; } = new();
    public GhidraBinaryInfo Binary { get; set; } = new();
    public GhidraTextApiSurface TextApiSurface { get; set; } = new();
    public List<GhidraCandidate> Candidates { get; set; } = new();
    public GhidraStringTable StringTable { get; set; } = new();
    public List<GhidraEngineHint> EngineHints { get; set; } = new();
    public GhidraStats Stats { get; set; } = new();
}

public sealed class GhidraToolInfo
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Script { get; set; } = "";
}

public sealed class GhidraBinaryInfo
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public string ExecutableFormat { get; set; } = "";
    public string Language { get; set; } = "";
    public string Compiler { get; set; } = "";
    public int Bits { get; set; }
    public string ImageBase { get; set; } = "";
    public string EntryPoint { get; set; } = "";
    public bool IsDotnet { get; set; }
    public bool DynamicApiResolutionSuspected { get; set; }
    public bool PackedSuspected { get; set; }
    public List<GhidraSection> Sections { get; set; } = new();
    public List<string> Dlls { get; set; } = new();
}

public sealed class GhidraSection
{
    public string Name { get; set; } = "";
    public string Start { get; set; } = "";
    public long Size { get; set; }
    public bool Execute { get; set; }
    public bool Write { get; set; }
    public double Entropy { get; set; }
}

public sealed class GhidraTextApiSurface
{
    public List<string> Present { get; set; } = new();
    public List<string> Missing { get; set; } = new();
    public List<GhidraImportSlot> ImportSlots { get; set; } = new();
}

public sealed class GhidraImportSlot
{
    public string Api { get; set; } = "";
    public string Dll { get; set; } = "";
    public string Category { get; set; } = "";
    public string Slot { get; set; } = "";
    public int RefSites { get; set; }
}

public sealed class GhidraCandidate
{
    public int Rank { get; set; }
    public string Function { get; set; } = "";
    public string Address { get; set; } = "";
    public double Confidence { get; set; }
    public string Level { get; set; } = "";
    public List<string> Apis { get; set; } = new();
    public GhidraCandidateEvidence Evidence { get; set; } = new();
    public GhidraHook Hook { get; set; } = new();
}

public sealed class GhidraCandidateEvidence
{
    public int CallerCount { get; set; }
    public List<string> Callers { get; set; } = new();
    public int CjkStringRefs { get; set; }
    public bool NameHint { get; set; }
    public bool DebugHint { get; set; }
    public List<GhidraStringHit> Strings { get; set; } = new();
}

public sealed class GhidraHook
{
    public string Slot { get; set; } = "";
    public string Strategy { get; set; } = "";
}

public sealed class GhidraStringHit
{
    public string Address { get; set; } = "";
    public string Text { get; set; } = "";
    public int CjkChars { get; set; }
}

public sealed class GhidraStringTable
{
    public string Source { get; set; } = "";
    public int DefinedStringsTotal { get; set; }
    public int DefinedCjkStrings { get; set; }
    public int Utf16ScanCjkStrings { get; set; }
    public int WithCodeXref { get; set; }
    public List<GhidraStringAnchor> TopAnchors { get; set; } = new();
}

public sealed class GhidraStringAnchor
{
    public string Address { get; set; } = "";
    public string Text { get; set; } = "";
    public int CjkChars { get; set; }
    public int Xrefs { get; set; }
    public List<string> Functions { get; set; } = new();
}

public sealed class GhidraEngineHint
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class GhidraStats
{
    public long Functions { get; set; }
    public long InstructionsInCandidateBodies { get; set; }
    public long DefinedStrings { get; set; }
    public long DefinedCjkStrings { get; set; }
    public long Utf16ScanCjkStrings { get; set; }
    public long CjkStrings { get; set; }
    public long GlobalScanCandidates { get; set; }
    public long CandidateCount { get; set; }
    public long ScriptMs { get; set; }
}

/// <summary>candidates.json 与 stdout 里 WROTE 行的解析器；不做任何「成功」的推断。</summary>
public static class GhidraResultParser
{
    public const string SchemaPrefix = "gala11y.ghidra.candidates/";

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        // 脚本 JSON 是 snake_case（size_bytes / packed_suspected / text_api_surface ...），
        // 不能只靠 Web 默认的 camelCase：多词字段会静默读成默认值。
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        AllowTrailingCommas = true,
    };

    private static readonly Regex WrotePattern = new(
        @"^WROTE\s+(?<path>.+?)\s+bytes=(?<bytes>\d+)\s+candidates=(?<candidates>\d+)\s+cjk_strings=(?<cjk>\d+)\s+packed=(?<packed>true|false)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static GhidraCandidatesDocument Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        GhidraCandidatesDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<GhidraCandidatesDocument>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new JsonException($"Ghidra 脚本输出不是合法 JSON：{ex.Message}", ex);
        }

        if (document is null)
        {
            throw new JsonException("Ghidra 脚本输出为空。");
        }

        if (document.Schema is null || !document.Schema.StartsWith(SchemaPrefix, StringComparison.Ordinal))
        {
            throw new JsonException(
                $"Ghidra 脚本输出的 schema 不受支持：'{document.Schema}'（期望 {SchemaPrefix}1）。");
        }

        return document;
    }

    public static GhidraCandidatesDocument ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path));
    }

    public static bool TryParse(string json, out GhidraCandidatesDocument? document, out string? error)
    {
        try
        {
            document = Parse(json);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            document = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>解析脚本 stdout 里 `WROTE &lt;path&gt; bytes=... candidates=... cjk_strings=... packed=...`。</summary>
    public static bool TryParseWroteLine(
        string standardOutput,
        out string? path,
        out int candidates,
        out int cjkStrings,
        out bool packed)
    {
        path = null;
        candidates = 0;
        cjkStrings = 0;
        packed = false;
        if (string.IsNullOrEmpty(standardOutput))
        {
            return false;
        }

        var match = WrotePattern.Match(standardOutput);
        if (!match.Success)
        {
            return false;
        }

        path = match.Groups["path"].Value.Trim();
        candidates = int.Parse(match.Groups["candidates"].Value, CultureInfo.InvariantCulture);
        cjkStrings = int.Parse(match.Groups["cjk"].Value, CultureInfo.InvariantCulture);
        packed = string.Equals(match.Groups["packed"].Value, "true", StringComparison.OrdinalIgnoreCase);
        return true;
    }
}
