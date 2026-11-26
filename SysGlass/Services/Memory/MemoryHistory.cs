using SysGlass.Models;

namespace SysGlass.Services.Memory;

/// <summary>
/// 커널 풀 표본의 제한된 이력. 최근 1시간은 모든 표본을, 25시간은 1분 간격 표본만 보관한다.
/// 시간축은 절전 시간을 제외한 활성 시간이므로 절전 복귀 직후에도 증가율이 부풀려지지 않는다.
/// 활성 시간이 뒤로 가면(재부팅 등 카운터 초기화) 이력을 비우고 새로 시작한다. 한 스레드에서만 호출해야 한다.
/// </summary>
public sealed class MemoryHistory
{
    public readonly record struct Point(long Active100ns, DateTimeOffset Time, ulong Paged, ulong NonPaged);

    private static readonly long FineKeep = TimeSpan.FromMinutes(65).Ticks;
    private static readonly long CoarseKeep = TimeSpan.FromHours(25).Ticks;
    private static readonly long CoarseStep = TimeSpan.FromMinutes(1).Ticks;
    private const int FineMax = 4000, CoarseMax = 1600;
    /// <summary>비교 기준 표본이 구간보다 이만큼 이상 오래되면 비교하지 않는다(감시 중단 구간 등).</summary>
    private const double MaxSpanRatio = 1.5;

    private readonly Queue<Point> _fine = new();
    private readonly Queue<Point> _coarse = new();
    private Point? _last, _lastCoarse;

    public int Count => _fine.Count + _coarse.Count;

    public IReadOnlyCollection<Point> Fine => _fine;
    public IReadOnlyCollection<Point> Coarse => _coarse;

    public TimeSpan Observed
    {
        get
        {
            if (_last is not { } last) return TimeSpan.Zero;
            long first = Math.Min(_fine.Count > 0 ? _fine.Peek().Active100ns : long.MaxValue,
                                  _coarse.Count > 0 ? _coarse.Peek().Active100ns : long.MaxValue);
            return first == long.MaxValue ? TimeSpan.Zero : TimeSpan.FromTicks(last.Active100ns - first);
        }
    }

    public void Clear()
    {
        _fine.Clear();
        _coarse.Clear();
        _last = _lastCoarse = null;
    }

    /// <summary>표본을 추가한다. 풀 값이 없는 표본은 무시한다. 초기화가 일어났으면 true.</summary>
    public bool Add(KernelMemorySample s)
    {
        if (s.PagedPool is not { } paged || s.NonPagedPool is not { } nonPaged) return false;
        var p = new Point(s.ActiveTime100ns, s.Time, paged, nonPaged);
        bool reset = false;
        if (_last is { } last && p.Active100ns <= last.Active100ns)
        {
            Clear();
            reset = true;
        }

        _fine.Enqueue(p);
        while (_fine.Count > FineMax || (_fine.Count > 1 && p.Active100ns - _fine.Peek().Active100ns > FineKeep)) _fine.Dequeue();

        if (_lastCoarse is not { } lc || p.Active100ns - lc.Active100ns >= CoarseStep)
        {
            _coarse.Enqueue(p);
            _lastCoarse = p;
            while (_coarse.Count > CoarseMax || (_coarse.Count > 1 && p.Active100ns - _coarse.Peek().Active100ns > CoarseKeep)) _coarse.Dequeue();
        }
        _last = p;
        return reset;
    }

    /// <summary>최근 표본과 window 이전 표본의 차이. 관측이 부족하면 미측정.</summary>
    public PoolGrowth Growth(TimeSpan window)
    {
        if (_last is not { } last) return PoolGrowth.Unmeasured(window);
        long target = last.Active100ns - window.Ticks;
        Point? best = null;
        foreach (var q in new[] { _fine, _coarse })
            foreach (var p in q)
            {
                if (p.Active100ns > target) break; // 큐는 시간순
                if (best is null || p.Active100ns > best.Value.Active100ns) best = p;
            }
        if (best is not { } b) return PoolGrowth.Unmeasured(window);
        long span = last.Active100ns - b.Active100ns;
        if (span <= 0 || span > window.Ticks * MaxSpanRatio) return PoolGrowth.Unmeasured(window);
        return new PoolGrowth(window, (long)last.Paged - (long)b.Paged, (long)last.NonPaged - (long)b.NonPaged, TimeSpan.FromTicks(span));
    }
}
