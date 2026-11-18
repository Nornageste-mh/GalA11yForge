using System.Diagnostics;
using GalA11yForge.Core.Models;
using GalA11yForge.Detection.Fingerprints;
using GalA11yForge.Detection.Internal;

namespace GalA11yForge.Detection;

/// <summary>
/// 引擎识别器：把一个游戏安装目录变成 <see cref="DetectionResult"/>。
///
/// 流程（可追到 research/16-engines-more/evidence/{engine-fingerprints.json,detect.py}）：
/// 1) 递归索引文件名/目录名（只读，跳过 reparse point）；
/// 2) 逐条规则求值（all 全真 / any 一真，命中即停止求值该规则剩余条件）；
/// 3) 命中的候选按 score 降序（稳定排序，同分保持规则顺序）；
/// 4) 应用 5 组消歧：when 中引擎全部命中时，除 resolve 外的引擎标记 suppressed；
/// 5) 置信度 = score / 100；score &lt; 50 只算「疑似」；
/// 6) 对主结论探版本与位数（读不到写 null / 0 + unverified 证据）。
///
/// 不启动游戏、不运行可执行文件、不写文件；目录不存在直接抛
/// <see cref="DirectoryNotFoundException"/>（CLI 侧应映射为参数错误）。
/// </summary>
public sealed class EngineDetector
{
    private readonly FingerprintDatabase _database;

    /// <summary>使用随程序集发布的默认指纹库（Fingerprints/engine-fingerprints.json）。</summary>
    public EngineDetector()
        : this(FingerprintLoader.LoadDefault())
    {
    }

    /// <summary>使用调用方提供的指纹库（测试或自定义库）。</summary>
    public EngineDetector(FingerprintDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <summary>当前使用的指纹库。</summary>
    public FingerprintDatabase Database => _database;

    /// <summary>检测指定游戏目录。</summary>
    public DetectionResult Detect(string gameDirectory) => Detect(gameDirectory, CancellationToken.None);

    /// <summary>检测指定游戏目录（支持取消）。</summary>
    public DetectionResult Detect(string gameDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            throw new ArgumentException("游戏目录不能为空", nameof(gameDirectory));
        }

        string rootPath;
        try
        {
            rootPath = Path.GetFullPath(gameDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DirectoryNotFoundException($"游戏目录无效：{gameDirectory}（{ex.Message}）", ex);
        }

        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"游戏目录不存在：{rootPath}");
        }

        var stopwatch = Stopwatch.StartNew();
        var index = GameDirectoryIndex.Scan(rootPath, cancellationToken);
        var evaluator = new ConditionEvaluator(index);

        var hits = new List<DetectionCandidate>();
        var evaluatedRules = 0;
        foreach (var rule in _database.Rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            evaluatedRules++;

            if (!TryMatch(rule, evaluator, out var evidence))
            {
                continue;
            }

            var engine = _database.FindEngine(rule.Engine);
            hits.Add(new DetectionCandidate
            {
                Engine = rule.Engine,
                EngineName = engine?.Name ?? rule.Engine,
                EngineFamily = engine?.Family ?? "unknown",
                Difficulty = engine?.Difficulty ?? string.Empty,
                RuleId = rule.Id,
                Score = rule.Score,
                Evidence = evidence,
            });
        }

        var ordered = hits.OrderByDescending(candidate => candidate.Score).ToList();
        ApplyDisambiguation(ordered);

        var primaryCandidate = ordered.FirstOrDefault(candidate => !candidate.IsSuppressed);
        var primary = primaryCandidate is null
            ? BuildUnknown(index, evaluatedRules)
            : BuildPrimary(index, primaryCandidate, ordered, evaluatedRules, cancellationToken);

        PlaceholderBinaryCheck.Apply(index.RootPath, primary);

        stopwatch.Stop();
        return new DetectionResult
        {
            Primary = primary,
            Candidates = ordered,
            RootPath = index.RootPath,
            FilesScanned = index.Files.Count,
            DirectoriesScanned = index.DirectoriesScanned,
            Truncated = index.Truncated,
            Elapsed = stopwatch.Elapsed,
        };
    }

    /// <summary>规则求值：all 需全部命中，any 命中第一个即返回；证据只收集命中项。</summary>
    private static bool TryMatch(DetectionRule rule, ConditionEvaluator evaluator, out List<string> evidence)
    {
        evidence = new List<string>();
        var requireAll = rule.MatchesAll;
        var conditions = requireAll ? rule.All : rule.Any;
        if (conditions.Count == 0)
        {
            return false;
        }

        foreach (var condition in conditions)
        {
            var outcome = evaluator.Evaluate(condition);
            if (outcome.Hit)
            {
                evidence.Add(outcome.Evidence);
                if (!requireAll)
                {
                    return true;
                }

                continue;
            }

            if (requireAll)
            {
                evidence.Clear();
                return false;
            }
        }

        return requireAll;
    }

    /// <summary>
    /// 5 组消歧：when 内引擎全部出现在命中的引擎集合里时，
    /// 把 when 中除 resolve 之外的候选标记为 suppressed（理由写进候选与主证据）。
    /// </summary>
    private void ApplyDisambiguation(List<DetectionCandidate> candidates)
    {
        foreach (var group in _database.Disambiguation)
        {
            if (group.When.Count == 0)
            {
                continue;
            }

            var allPresent = group.When.All(engineId =>
                candidates.Any(candidate => string.Equals(candidate.Engine, engineId, StringComparison.OrdinalIgnoreCase)));
            if (!allPresent)
            {
                continue;
            }

            var reason =
                $"disambiguation({string.Join("+", group.When)})→{group.Resolve}：{group.Why}";

            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate.Engine, group.Resolve, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (group.When.Any(engineId => string.Equals(engineId, candidate.Engine, StringComparison.OrdinalIgnoreCase)))
                {
                    candidate.SuppressedBy ??= reason;
                }
            }
        }
    }

    private EngineDetection BuildPrimary(
        GameDirectoryIndex index,
        DetectionCandidate candidate,
        List<DetectionCandidate> ordered,
        int evaluatedRules,
        CancellationToken cancellationToken)
    {
        var probe = ArtifactProbe.Probe(index, candidate.Engine, cancellationToken);

        var evidence = new List<string>(candidate.Evidence)
        {
            $"rule:{candidate.RuleId} engine={candidate.Engine} score={candidate.Score}",
        };
        evidence.AddRange(probe.Evidence);

        if (probe.Version is null)
        {
            evidence.Add(UnverifiedVersionLine(candidate.Engine));
        }

        foreach (var suppressed in ordered.Where(item => item.IsSuppressed))
        {
            evidence.Add(
                $"suppressed:{suppressed.Engine}（rule={suppressed.RuleId} score={suppressed.Score}）← {suppressed.SuppressedBy}");
        }

        foreach (var other in ordered.Where(item => !ReferenceEquals(item, candidate) && !item.IsSuppressed))
        {
            evidence.Add(
                $"candidate:{other.Engine} score={other.Score} confidence={other.Confidence:0.00} rule={other.RuleId}");
        }

        evidence.Add(ScanLine(index, evaluatedRules, ordered.Count));
        if (index.Truncated)
        {
            evidence.Add("warn:扫描达到本层上限，结果可能漏检");
        }

        return new EngineDetection
        {
            SchemaVersion = 1,
            Family = candidate.Engine,
            Version = probe.Version,
            Bitness = probe.Bitness,
            Confidence = candidate.Confidence,
            Evidence = evidence,
        };
    }

    private static EngineDetection BuildUnknown(GameDirectoryIndex index, int evaluatedRules)
    {
        var evidence = new List<string>
        {
            $"scan:root={index.RootPath}",
            ScanLine(index, evaluatedRules, 0),
            index.RootEntries.Count == 0
                ? "scan:top_level=（空目录）"
                : "scan:top_level=" + string.Join(", ", index.RootEntries.Take(12)),
            "engine:none（空目录或不是已知 Galgame 引擎布局；未启动游戏、未运行可执行文件、未写入任何文件）",
        };

        if (index.Truncated)
        {
            evidence.Add("warn:扫描达到本层上限，结果可能漏检");
        }

        return new EngineDetection
        {
            SchemaVersion = 1,
            Family = "unknown",
            Version = null,
            Bitness = 0,
            Confidence = 0,
            Evidence = evidence,
        };
    }

    private static string ScanLine(GameDirectoryIndex index, int evaluatedRules, int hits) =>
        $"scan:files={index.Files.Count} dirs={index.DirectoriesScanned} "
        + $"reparse_skipped={index.ReparsePointsSkipped} truncated={index.Truncated.ToString().ToLowerInvariant()} "
        + $"rules_evaluated={evaluatedRules} hits={hits}";

    private static string UnverifiedVersionLine(string engineId) => engineId.ToLowerInvariant() switch
    {
        "unity_mono" or "unity_il2cpp" or "unity_custom_draw" =>
            "unverified:version 未从 *_Data/globalgamemanagers / UnityPlayer.dll 读到 Unity 版本（version=null，不编造）",
        "renpy" =>
            "unverified:version 未从 renpy/__init__.py 读到 version_tuple（version=null，不编造）",
        _ =>
            "unverified:version 该引擎没有静态版本来源（version=null，不编造）",
    };
}
