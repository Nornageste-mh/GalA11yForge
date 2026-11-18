namespace GalA11yForge.ExternalReader;

public sealed record SpeechItem(string Source, string NodeId, string Text, long FirstSeenMs, long SettledMs);

public sealed class GateStats
{
    public long Observed, SuppressedRepeat, SuppressedShort, SuppressedFilter, SuppressedSameAsPending, SuppressedUnchanged, Emitted;
}

/// <summary>
/// “文本变化 → 稳定 → 朗读”的闸门。
///
/// 规则（对应 R9 §7.2 的 LineAssembler 建议）：
///   1. 同一 NodeId 的文本变了：挂起，等它稳定 settleMs 再念（挡打字机逐字刷新）；
///   2. 归一化后 <c>minLen</c> 或命中 <c>--filter</c> 之外：丢弃；
///   3. 与上一句完全相同且距今 &lt; repeatWindowMs：丢弃（3 秒内同文本只念一次）；
///   4. 多个节点同时变化：按“最后变化时间”升序念，避免并发抢话。
/// </summary>
public sealed class SpeechGate
{
    private sealed class Pending
    {
        public required string Source;
        public required string NodeId;
        public required string Text;
        public long FirstSeenMs;
        public long LastChangeMs;
    }

    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// 每个节点“上一次念过的文本”。没有这张表时，静态控件名（例如 WinForms 的
    /// AccessibleName、菜单项）会在每次 _lastSpoken 被别的句子顶掉后重新入队，
    /// 造成“同一句每两三秒念一次”的刷屏（本原型第一版实测 45 句里有 20 句是这种回声）。
    /// </summary>
    private readonly Dictionary<string, string> _spokenByNode = new(StringComparer.Ordinal);
    private readonly Options _o;
    private readonly GateStats _stats;

    private string _lastSpoken = string.Empty;
    private long _lastSpokenMs = -1_000_000;

    public SpeechGate(Options options, GateStats stats)
    {
        _o = options;
        _stats = stats;
    }

    public string LastSpoken => _lastSpoken;

    public void Observe(IReadOnlyList<TextObservation> observations, long nowMs)
    {
        foreach (var obs in observations)
        {
            var key = obs.Source + "|" + obs.NodeId;
            var text = TextNormalizer.Normalize(obs.Text, _o.MaxLen);
            if (text.Length == 0)
            {
                _pending.Remove(key);
                continue;
            }

            _stats.Observed++;

            if (text.Length < _o.MinLen)
            {
                _stats.SuppressedShort++;
                _pending.Remove(key);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(_o.Filter) && !text.Contains(_o.Filter, StringComparison.OrdinalIgnoreCase))
            {
                _stats.SuppressedFilter++;
                _pending.Remove(key);
                continue;
            }
            if (text == _lastSpoken && nowMs - _lastSpokenMs < _o.RepeatWindowMs)
            {
                _stats.SuppressedRepeat++;
                _pending.Remove(key);
                continue;
            }
            if (_spokenByNode.TryGetValue(key, out var spoken) && spoken == text)
            {
                // 这个节点没有变化：不重复念（静态标签最主要的噪声源）
                _stats.SuppressedUnchanged++;
                _pending.Remove(key);
                continue;
            }

            if (_pending.TryGetValue(key, out var p))
            {
                if (p.Text == text) continue;      // 没变，继续等稳定
                _stats.SuppressedSameAsPending++;
                p.Text = text;
                p.LastChangeMs = nowMs;
            }
            else
            {
                _pending[key] = new Pending
                {
                    Source = obs.Source,
                    NodeId = obs.NodeId,
                    Text = text,
                    FirstSeenMs = nowMs,
                    LastChangeMs = nowMs,
                };
            }
        }
    }

    /// <summary>把已经稳定 settleMs 的挂起项交出去（并做全局重复检查）。</summary>
    public List<SpeechItem> Flush(long nowMs)
    {
        var due = _pending.Values
            .Where(p => nowMs - p.LastChangeMs >= _o.SettleMs)
            .OrderBy(p => p.LastChangeMs)
            .ToList();

        var result = new List<SpeechItem>(due.Count);
        foreach (var p in due)
        {
            var key = p.Source + "|" + p.NodeId;
            _pending.Remove(key);
            if (p.Text == _lastSpoken && nowMs - _lastSpokenMs < _o.RepeatWindowMs)
            {
                _stats.SuppressedRepeat++;
                continue;
            }
            if (_spokenByNode.TryGetValue(key, out var spoken) && spoken == p.Text)
            {
                _stats.SuppressedUnchanged++;
                continue;
            }
            _lastSpoken = p.Text;
            _lastSpokenMs = nowMs;
            _spokenByNode[key] = p.Text;
            if (_spokenByNode.Count > 5000) _spokenByNode.Clear();
            _stats.Emitted++;
            result.Add(new SpeechItem(p.Source, p.NodeId, p.Text, p.FirstSeenMs, nowMs));
        }
        return result;
    }

    public void Forget()
    {
        _pending.Clear();
        _spokenByNode.Clear();
        _lastSpoken = string.Empty;
        _lastSpokenMs = -1_000_000;
    }
}
