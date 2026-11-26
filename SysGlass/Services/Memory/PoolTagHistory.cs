using SysGlass.Models;

namespace SysGlass.Services.Memory;

/// <summary>
/// Pool Tag 사용량의 제한된 이력. 태그를 인덱스로 바꿔 사용량만 배열로 저장해 메모리를 아낀다.
/// 최근 15분은 모든 스냅샷, 25시간은 10분 간격 스냅샷만 보관한다. 시간축은 절전 시간을 제외한 활성 시간이며,
/// 활성 시간이 뒤로 가면(재부팅) 이력을 비운다. 한 스레드에서만 호출해야 한다.
/// </summary>
public sealed class PoolTagHistory
{
    private readonly record struct Snapshot(long Active100ns, long[] Used);

    private static readonly long FineKeep = TimeSpan.FromMinutes(15).Ticks;
    private static readonly long CoarseKeep = TimeSpan.FromHours(25).Ticks;
    private static readonly long CoarseStep = TimeSpan.FromMinutes(10).Ticks;
    private const int MaxTags = 20_000, FineMax = 120, CoarseMax = 160;
    private const double MaxSpanRatio = 1.5;

    private readonly Dictionary<uint, int> _index = new();
    private readonly Queue<Snapshot> _fine = new();
    private readonly Queue<Snapshot> _coarse = new();
    private Snapshot? _last, _lastCoarse;

    public int SnapshotCount => _fine.Count + _coarse.Count;
    public int TagCount => _index.Count;

    public void Clear()
    {
        _index.Clear();
        _fine.Clear();
        _coarse.Clear();
        _last = _lastCoarse = null;
    }

    public void Add(long active100ns, IReadOnlyList<PoolTagEntry> tags)
    {
        if (_last is { } last && active100ns <= last.Active100ns) Clear();
        foreach (var t in tags)
            if (!_index.ContainsKey(t.Key) && _index.Count < MaxTags) _index[t.Key] = _index.Count;

        var used = new long[_index.Count];
        foreach (var t in tags)
            if (_index.TryGetValue(t.Key, out int i)) used[i] = (long)Math.Min(t.TotalUsed, long.MaxValue);
        var snap = new Snapshot(active100ns, used);

        _fine.Enqueue(snap);
        while (_fine.Count > FineMax || (_fine.Count > 1 && active100ns - _fine.Peek().Active100ns > FineKeep)) _fine.Dequeue();
        if (_lastCoarse is not { } lc || active100ns - lc.Active100ns >= CoarseStep)
        {
            _coarse.Enqueue(snap);
            _lastCoarse = snap;
            while (_coarse.Count > CoarseMax || (_coarse.Count > 1 && active100ns - _coarse.Peek().Active100ns > CoarseKeep)) _coarse.Dequeue();
        }
        _last = snap;
    }

    /// <summary>
    /// window 이전 스냅샷 대비 태그별 증가량. 관측이 부족하면 null(미측정).
    /// 기준 스냅샷 이후 처음 나타난 태그는 기준값을 0으로 본다(그 전에는 할당이 없었던 태그).
    /// </summary>
    public Dictionary<uint, long>? Growth(TimeSpan window)
    {
        if (_last is not { } last) return null;
        long target = last.Active100ns - window.Ticks;
        Snapshot? best = null;
        foreach (var q in new[] { _fine, _coarse })
            foreach (var s in q)
            {
                if (s.Active100ns > target) break;
                if (best is null || s.Active100ns > best.Value.Active100ns) best = s;
            }
        if (best is not { } b) return null;
        long span = last.Active100ns - b.Active100ns;
        if (span <= 0 || span > window.Ticks * MaxSpanRatio) return null;

        var result = new Dictionary<uint, long>(_index.Count);
        foreach (var (key, i) in _index)
        {
            long now = i < last.Used.Length ? last.Used[i] : 0;
            long before = i < b.Used.Length ? b.Used[i] : 0;
            if (now != before) result[key] = now - before;
        }
        return result;
    }
}
