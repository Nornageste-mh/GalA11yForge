using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityMonoAnalyzer.Reporting;

/// <summary>analysis.json 的唯一序列化入口 —— schema 固定，字段名 camelCase。</summary>
public static class JsonReport
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static string Serialize(AnalysisResult result) => JsonSerializer.Serialize(result, Options);

    public static void Write(AnalysisResult result, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, Serialize(result), new UTF8Encoding(false));
    }

    public static AnalysisResult Deserialize(string json) =>
        JsonSerializer.Deserialize<AnalysisResult>(json, Options) ?? new AnalysisResult();
}
