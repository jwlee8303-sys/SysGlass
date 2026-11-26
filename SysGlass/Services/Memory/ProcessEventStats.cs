using SysGlass.Models;

namespace SysGlass.Services.Memory;

/// <summary>
/// 프로세스 생성·종료 이벤트의 제한된 집계. 1분 단위 버킷 60개(1시간)와 최근 이벤트 300건만 보관한다.
/// 버킷마다 이름은 최대 500개까지 세고, 넘치면 '(기타)'로 합친다. 여러 스레드에서 호출해도 안전하다.
/// 시각은 인자로 받으므로 가상 데이터로 검사할 수 있다.
/// </summary>
public sealed class ProcessEventStats
{
    public const int Buckets = 60, MaxRecent = 300, MaxNamesPerBucket = 500;
    public const string OtherName = "(기타)";

    private sealed class Bucket
    {
        public long Minute;
        public int Starts, Stops;
        public readonly Dictionary<string, int> ByName = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly object _lock = new();
    private readonly Bucket[] _buckets = Enumerable.Range(0, Buckets).Select(_ => new Bucket { Minute = long.MinValue }).ToArray();
    private readonly Queue<ProcessEventRecord> _recent = new();
    private bool _available;
    private string? _error = "수집 전";
    private DateTimeOffset? _since;
    private long _overflowed;

    public void MarkStarted(DateTimeOffset now)
    {
        lock (_lock) { _available = true; _error = null; _since ??= now; }
    }

    public void MarkUnavailable(string error)
    {
        lock (_lock) { _available = false; _error = error; }
    }

    public void Reset()
    {
        lock (_lock)
        {
            foreach (var b in _buckets) { b.Minute = long.MinValue; b.Starts = b.Stops = 0; b.ByName.Clear(); }
            _recent.Clear();
            _available = false;
            _error = "수집 전";
            _since = null;
            _overflowed = 0;
        }
    }

    public void Record(ProcessEventRecord e)
    {
        long minute = e.Time.ToUnixTimeSeconds() / 60;
        lock (_lock)
        {
            var b = _buckets[(int)(((minute % Buckets) + Buckets) % Buckets)];
            if (b.Minute != minute) { b.Minute = minute; b.Starts = b.Stops = 0; b.ByName.Clear(); }
            if (e.Kind == ProcessEventKind.Start)
            {
                b.Starts++;
                string name = e.Name.Length == 0 ? "(이름 없음)" : e.Name;
                if (!b.ByName.ContainsKey(name) && b.ByName.Count >= MaxNamesPerBucket) { name = OtherName; _overflowed++; }
                b.ByName[name] = b.ByName.GetValueOrDefault(name) + 1;
            }
            else b.Stops++;
            _recent.Enqueue(e);
            while (_recent.Count > MaxRecent) _recent.Dequeue();
        }
    }

    public ProcessEventSummary Snapshot(DateTimeOffset now)
    {
        long minute = now.ToUnixTimeSeconds() / 60;
        lock (_lock)
        {
            int s10 = 0, t10 = 0, s60 = 0, t60 = 0;
            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in _buckets)
            {
                long age = minute - b.Minute;
                if (b.Minute == long.MinValue || age < 0 || age >= Buckets) continue;
                s60 += b.Starts; t60 += b.Stops;
                if (age < 10) { s10 += b.Starts; t10 += b.Stops; }
                foreach (var (n, c) in b.ByName) names[n] = names.GetValueOrDefault(n) + c;
            }
            var top = names.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Take(15).Select(x => (x.Key, x.Value)).ToList();
            return new ProcessEventSummary(_available, _error, _since, s10, t10, s60, t60, top, _recent.Reverse().ToList(), _overflowed);
        }
    }
}
