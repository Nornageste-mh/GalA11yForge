using System.Text.Json;
using Xunit.Sdk;

namespace Forge.E2E;

/// <summary>
/// detect 结果的兼容视图。docs/06 是 {family, confidence, evidence}，docs/09 是
/// {candidates:[{engine, confidence, signals}], evidence}；这里两种都认，避免测试绑死某一版 schema。
/// </summary>
internal sealed record DetectView
{
    public required string Family { get; init; }
    public required double Confidence { get; init; }
    public required int EvidenceCount { get; init; }
    public required string Source { get; init; }

    public static DetectView Resolve(CommandResult result, IEnumerable<string> roots)
    {
        var rootList = roots.ToList();
        var detectPath = ArtifactLocator.FindNewest(rootList, "detect.json")
            ?? ArtifactLocator.FindFilesContaining(rootList, "detect", ".json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

        if (detectPath is not null)
        {
            var fromFile = TryFromFile(detectPath);
            if (fromFile is not null)
            {
                return fromFile;
            }

            if (result.Envelope?.Data is JsonElement data && TryFromJson(data, "CLI stdout data") is { } fromEnvelope)
            {
                return fromEnvelope;
            }

            throw new XunitException(
                $"detect.json 存在，但解析不出 family/engine 与 confidence：{detectPath}{Environment.NewLine}"
                + JsonHelpers.ReadObjectText(detectPath));
        }

        if (result.Envelope?.Data is JsonElement envelopeData && TryFromJson(envelopeData, "CLI stdout data") is { } view)
        {
            return view;
        }

        throw new XunitException(
            "detect 返回成功，但既没有 detect.json，也没有可解析的 CLI data。"
            + $"{Environment.NewLine}目录快照：{Environment.NewLine}{ArtifactLocator.Tree(rootList)}"
            + $"{Environment.NewLine}{result.Describe()}");
    }

    public static DetectView? TryFromFile(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return TryFromJson(document.RootElement, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static DetectView? TryFromJson(JsonElement root, string source)
    {
        var rootEvidence = CountEvidence(root);
        if (TryParse(root, source, depth: 0, out var view))
        {
            if (view.EvidenceCount == 0 && rootEvidence > 0)
            {
                view = view with { EvidenceCount = rootEvidence };
            }

            return view;
        }

        return null;
    }

    private static bool TryParse(JsonElement element, string source, int depth, out DetectView view)
    {
        view = null!;
        if (depth > 4)
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (JsonHelpers.TryGetString(element, out var family, "family", "engine")
                    && JsonHelpers.TryGetDouble(element, out var confidence, "confidence"))
                {
                    view = new DetectView
                    {
                        Family = family,
                        Confidence = Normalize(confidence),
                        EvidenceCount = CountEvidence(element),
                        Source = source,
                    };
                    return true;
                }

                if (JsonHelpers.TryGetProperty(element, "detection", out var detection)
                    && TryParse(detection, source, depth + 1, out view))
                {
                    return true;
                }

                if (JsonHelpers.TryGetProperty(element, "candidates", out var candidates)
                    && candidates.ValueKind == JsonValueKind.Array
                    && candidates.GetArrayLength() > 0)
                {
                    var first = candidates[0];
                    if (JsonHelpers.TryGetString(first, out family, "engine", "family")
                        && JsonHelpers.TryGetDouble(first, out confidence, "confidence"))
                    {
                        view = new DetectView
                        {
                            Family = family,
                            Confidence = Normalize(confidence),
                            EvidenceCount = CountEvidence(first),
                            Source = source,
                        };
                        return true;
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                        && TryParse(property.Value, source, depth + 1, out view))
                    {
                        return true;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                        && TryParse(item, source, depth + 1, out view))
                    {
                        return true;
                    }
                }

                break;
        }

        return false;
    }

    private static int CountEvidence(JsonElement element)
    {
        var count = 0;
        if (JsonHelpers.TryGetProperty(element, "evidence", out var evidence))
        {
            count += evidence.ValueKind switch
            {
                JsonValueKind.Array => evidence.GetArrayLength(),
                JsonValueKind.String or JsonValueKind.Object => 1,
                _ => 0,
            };
        }

        if (JsonHelpers.TryGetProperty(element, "signals", out var signals) && signals.ValueKind == JsonValueKind.Array)
        {
            count += signals.GetArrayLength();
        }

        return count;
    }

    private static double Normalize(double confidence)
    {
        if (double.IsNaN(confidence) || double.IsInfinity(confidence))
        {
            return 0;
        }

        if (confidence > 1 && confidence <= 100)
        {
            confidence /= 100;
        }

        return Math.Clamp(confidence, 0, 1);
    }
}
