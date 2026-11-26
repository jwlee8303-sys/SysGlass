using SysGlass.Models;

namespace SysGlass.Services.Memory;

/// <summary>
/// 항목별 이상 상태를 판정하고 알림 시점을 결정한다. 시각을 인자로 받으므로 가상 데이터로 검사할 수 있다.
/// <list type="bullet">
/// <item>이상 상태(주의 이상)가 지속 시간 이상 끊기지 않고 이어지면 첫 알림</item>
/// <item>알림 이후 상태가 악화되면 즉시 추가 알림, 같은 상태면 재발송 간격마다 알림</item>
/// <item>알림 전 정상으로 돌아오면 즉시 초기화, 알림 후에는 정상이 RecoveryHold 동안 유지되어야 초기화(경계값 흔들림 대비)</item>
/// <item>측정 불가는 상태를 진행시키지도 초기화하지도 않는다</item>
/// <item>평가 간격이 MaxGap을 넘거나 절전 복귀 등 불연속이 표시되면, 알림 전 대기 중인 항목의 지속 시간을 다시 센다</item>
/// </list>
/// 한 스레드에서만 호출해야 한다.
/// </summary>
public sealed class MemoryAnomalyDetector
{
    public static readonly TimeSpan RecoveryHold = TimeSpan.FromMinutes(1);

    private sealed class Episode
    {
        public DateTimeOffset Since;
        public DateTimeOffset? LastNotified;
        public MemoryLevel NotifiedLevel;
        public DateTimeOffset? NormalSince;
    }

    private readonly Dictionary<string, Episode> _episodes = new();
    private DateTimeOffset? _lastEval;
    private bool _discontinuity;

    public TimeSpan Sustain { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan Repeat { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan MaxGap { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>절전 복귀·감시 재개처럼 관측이 끊겼음을 알린다.</summary>
    public void MarkDiscontinuity() => _discontinuity = true;

    public void Reset()
    {
        _episodes.Clear();
        _lastEval = null;
        _discontinuity = false;
    }

    public (MemoryAssessment Assessment, IReadOnlyList<MemoryAlert> Alerts) Evaluate(
        DateTimeOffset now, IReadOnlyList<MetricReading> readings, bool available)
    {
        bool gap = _discontinuity || (_lastEval is { } last && (now < last || now - last > MaxGap));
        _discontinuity = false;
        _lastEval = now;
        if (gap)
            foreach (var ep in _episodes.Values)
                if (ep.LastNotified is null) ep.Since = now;

        var alerts = new List<MemoryAlert>();
        var statuses = new List<MetricStatus>(readings.Count);
        foreach (var r in readings)
        {
            var level = r.Threshold.Evaluate(r.Value);
            _episodes.TryGetValue(r.Key, out var ep);
            bool sustained = false;

            if (level >= MemoryLevel.Warn)
            {
                if (ep is null) _episodes[r.Key] = ep = new Episode { Since = now };
                ep.NormalSince = null;
                sustained = now - ep.Since >= Sustain;
                if (sustained)
                {
                    bool first = ep.LastNotified is null;
                    bool escalated = !first && level > ep.NotifiedLevel;
                    bool repeated = !first && !escalated && now - ep.LastNotified!.Value >= Repeat;
                    if (first || escalated || repeated)
                    {
                        alerts.Add(new MemoryAlert(now, level, r.Key, r.Label, r.ValueText, r.ThresholdText, escalated, repeated, r.IsTrend));
                        ep.LastNotified = now;
                        ep.NotifiedLevel = level;
                    }
                }
            }
            else if (level == MemoryLevel.Normal && ep is not null)
            {
                if (ep.LastNotified is null) _episodes.Remove(r.Key);
                else
                {
                    ep.NormalSince ??= now;
                    if (now - ep.NormalSince.Value >= RecoveryHold) _episodes.Remove(r.Key);
                }
            }

            statuses.Add(new MetricStatus(r.Key, r.Label, level, r.ValueText, r.ThresholdText, sustained, r.IsTrend));
        }

        // 더 이상 보고되지 않는 항목의 상태는 버린다.
        foreach (var key in _episodes.Keys.Where(k => readings.All(r => r.Key != k)).ToList()) _episodes.Remove(key);

        MemoryLevel overall = MemoryLevel.Unknown;
        if (available)
            foreach (var s in statuses) if (s.Level > overall) overall = s.Level;
        bool leak = available && statuses.Any(s => s.IsTrend && s.Sustained && s.Level >= MemoryLevel.Warn);
        return (new MemoryAssessment(overall, statuses, leak), alerts);
    }
}
