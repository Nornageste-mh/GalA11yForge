using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify.Internal;

/// <summary>
/// 一次验证过程中的累加器：统一添加检查、指标、未验证项与证据，避免各处重复样板。
/// </summary>
internal sealed class VerificationRun
{
    public VerificationRun(VerifyReport report)
    {
        Report = report;
    }

    public VerifyReport Report { get; }

    public void Pass(
        string id,
        string name,
        string priority,
        string detail,
        IEnumerable<string>? evidence = null,
        IReadOnlyDictionary<string, double>? metrics = null)
        => Add(id, name, priority, VerifyCheckStatus.Pass, detail, evidence, metrics);

    public void Fail(
        string id,
        string name,
        string priority,
        string detail,
        IEnumerable<string>? evidence = null,
        IReadOnlyDictionary<string, double>? metrics = null)
        => Add(id, name, priority, VerifyCheckStatus.Fail, detail, evidence, metrics);

    public void Skip(
        string id,
        string name,
        string priority,
        string detail,
        IEnumerable<string>? evidence = null,
        IReadOnlyDictionary<string, double>? metrics = null)
        => Add(id, name, priority, VerifyCheckStatus.Skip, detail, evidence, metrics);

    public void Add(
        string id,
        string name,
        string priority,
        VerifyCheckStatus status,
        string detail,
        IEnumerable<string>? evidence = null,
        IReadOnlyDictionary<string, double>? metrics = null)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("检查 id 不能为空。", nameof(id));
        }

        var check = new VerifyCheck
        {
            Id = id,
            Name = name,
            Priority = VerifyPriority.IsKnown(priority) ? priority.ToUpperInvariant() : VerifyPriority.P1,
            Status = status,
            Detail = detail ?? string.Empty,
        };

        if (evidence is not null)
        {
            foreach (var item in evidence)
            {
                if (!string.IsNullOrWhiteSpace(item) && !check.Evidence.Contains(item))
                {
                    check.Evidence.Add(item);
                }
            }
        }

        if (metrics is not null)
        {
            foreach (var pair in metrics)
            {
                check.Metrics[pair.Key] = pair.Value;
            }
        }

        Report.Checks.Add(check);
    }

    public void Metric(string key, double value) => Report.Metrics[key] = value;

    public void Bump(string key, double delta = 1)
    {
        Report.Metrics.TryGetValue(key, out var current);
        Report.Metrics[key] = current + delta;
    }

    public void Unverified(string text)
    {
        if (!string.IsNullOrWhiteSpace(text) && !Report.Unverified.Contains(text))
        {
            Report.Unverified.Add(text);
        }
    }

    public void Evidence(string text)
    {
        if (!string.IsNullOrWhiteSpace(text) && !Report.Evidence.Contains(text))
        {
            Report.Evidence.Add(text);
        }
    }
}
