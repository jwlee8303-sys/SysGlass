using SysGlass.Models;
using SysGlass.Services.Memory;
using SysGlass.Utilities;

/// <summary>메모리 감시의 순수 로직 검사. 실제 시스템 메모리를 소모하지 않고 가상 표본만 사용한다.</summary>
internal static class MemoryChecks
{
    private const ulong MB = 1024 * 1024;
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    private static KernelMemorySample Sample(TimeSpan active, ulong pagedMb, ulong nonPagedMb) => new()
    {
        Time = T0 + active,
        ActiveTime100ns = TimeSpan.FromHours(100).Ticks + active.Ticks,
        PagedPool = pagedMb * MB,
        NonPagedPool = nonPagedMb * MB,
    };

    public static void Run(Action<bool, string> check)
    {
        // 관측 부족은 0이 아니라 미측정
        var h = new MemoryHistory();
        check(!h.Growth(TimeSpan.FromMinutes(10)).Measured, "empty history is unmeasured");
        h.Add(Sample(TimeSpan.Zero, 300, 100));
        h.Add(Sample(TimeSpan.FromMinutes(5), 310, 100));
        check(!h.Growth(TimeSpan.FromMinutes(10)).Measured, "short observation is unmeasured");

        // 5초 간격 25시간 표본: 시간당 100MB 증가
        h.Clear();
        var step = TimeSpan.FromSeconds(5);
        for (var t = TimeSpan.Zero; t <= TimeSpan.FromHours(25); t += step)
            h.Add(Sample(t, 300 + (ulong)(t.TotalHours * 100), 100));
        var g10 = h.Growth(TimeSpan.FromMinutes(10));
        var g1 = h.Growth(TimeSpan.FromHours(1));
        var g24 = h.Growth(TimeSpan.FromHours(24));
        check(g10.Measured && Math.Abs(g10.TotalDelta!.Value / (double)MB - 16.7) < 1.5, "10 minute growth");
        check(g1.Measured && Math.Abs(g1.TotalDelta!.Value / (double)MB - 100) < 2, "1 hour growth");
        check(g24.Measured && Math.Abs(g24.TotalDelta!.Value / (double)MB - 2400) < 5, "24 hour growth from coarse history");
        check(g1.TotalPerHour is { } ph && Math.Abs(ph / MB - 100) < 2, "growth per hour");
        check(h.Count <= 4000 + 1600, "history stays bounded");

        // 활성 시간이 뒤로 가면(재부팅·카운터 초기화) 이력을 새로 시작해 큰 감소·증가로 오판하지 않는다.
        bool reset = h.Add(new KernelMemorySample { Time = T0, ActiveTime100ns = 1, PagedPool = 263 * MB, NonPagedPool = 90 * MB });
        check(reset && !h.Growth(TimeSpan.FromMinutes(10)).Measured, "counter reset restarts history");

        // 감시 중단 구간처럼 기준 표본이 너무 오래되면 비교하지 않는다.
        h.Clear();
        h.Add(Sample(TimeSpan.Zero, 300, 100));
        h.Add(Sample(TimeSpan.FromMinutes(40), 900, 100));
        check(!h.Growth(TimeSpan.FromMinutes(10)).Measured, "stale baseline is not compared");

        // 풀 값이 없는 표본은 무시
        check(!h.Add(new KernelMemorySample { ActiveTime100ns = long.MaxValue }), "sample without pool ignored");

        // 감소도 부호와 함께 표시
        check(Format.SignedBytes(-3.0 * MB) == "-3 MB" && Format.SignedBytes(120.0 * MB) == "+120 MB", "signed byte format");
        check(Format.Bytes(15.22 * 1024 * MB) == "15.22 GB" && Format.Bytes(null) == Format.NA, "byte format");
        check(Format.Duration(TimeSpan.FromDays(24.2)) == "24일 4시간" && Format.Duration(TimeSpan.FromMinutes(12)) == "12분", "duration format");

        DetectorChecks(check);
        SettingsChecks(check);
    }

    private static readonly MetricThreshold PoolThreshold = new(2048, 4096);

    private static MetricReading Pool(double? mb) =>
        new("paged", "페이징 풀", mb, PoolThreshold, mb is { } v ? $"{v} MB" : "N/A", "주의 2 GB · 위험 4 GB");

    private static void DetectorChecks(Action<bool, string> check)
    {
        // 기준값 판정 (가용 RAM처럼 낮을수록 나쁜 항목 포함)
        var avail = new MetricThreshold(15, 5, LowerIsWorse: true);
        check(avail.Evaluate(50) == MemoryLevel.Normal && avail.Evaluate(15) == MemoryLevel.Warn && avail.Evaluate(4) == MemoryLevel.High, "lower-is-worse threshold");
        check(PoolThreshold.Evaluate(null) == MemoryLevel.Unknown && PoolThreshold.Evaluate(double.NaN) == MemoryLevel.Unknown, "missing value is unknown, not normal");

        var d = new MemoryAnomalyDetector();
        DateTimeOffset At(double minutes) => T0.AddMinutes(minutes);
        IReadOnlyList<MemoryAlert> Run(double minutes, double? mb) => d.Evaluate(At(minutes), [Pool(mb)], true).Alerts;

        // 정상 → 주의: 지속 시간(5분) 전에는 알리지 않는다
        check(Run(0, 300).Count == 0, "normal has no alert");
        for (int m = 1; m <= 4; m++) check(Run(m, 2500).Count == 0, $"warn pending {m}m");
        var pending = d.Evaluate(At(4.5), [Pool(2500)], true).Assessment;
        check(pending.Overall == MemoryLevel.Warn && !pending.Metrics[0].Sustained, "status shows warn before alert");
        var first = Run(6, 2500);
        check(first.Count == 1 && first[0].Level == MemoryLevel.Warn && !first[0].Escalated && !first[0].Repeated, "first alert after sustain");

        // 같은 상태는 재발송 간격(30분)까지 다시 알리지 않는다
        bool dup = false;
        for (int m = 7; m < 36; m++) dup |= Run(m, 2600).Count > 0;
        check(!dup, "no duplicate alert inside repeat interval");
        var repeat = Run(36, 2600);
        check(repeat.Count == 1 && repeat[0].Repeated, "repeat alert after interval");

        // 악화되면 즉시 추가 알림
        var worse = Run(37, 4500);
        check(worse.Count == 1 && worse[0].Escalated && worse[0].Level == MemoryLevel.High, "escalation alerts immediately");
        check(Run(38, 4600).Count == 0, "no repeat right after escalation");

        // 알림 후에는 정상이 1분 유지되어야 초기화(경계값 흔들림 대비)
        check(Run(38.5, 1000).Count == 0 && Run(39, 2500).Count == 0, "brief recovery keeps episode");
        Run(40, 1000);
        Run(41.5, 1000); // 1분 이상 정상 → 초기화
        for (double m = 42; m < 47; m++) check(Run(m, 2500).Count == 0, $"after recovery sustain restarts {m}");
        check(Run(47.5, 2500).Count == 1, "alert again after new sustained episode");

        // 알림 전 대기 중에 정상으로 돌아오면 즉시 초기화
        d.Reset();
        Run(0, 2500); Run(1, 2500); Run(2, 2500);
        Run(3, 1000);
        bool early = false;
        for (double m = 4; m <= 8.5; m++) early |= Run(m, 2500).Count > 0;
        check(!early && Run(9, 2500).Count == 1, "pending episode resets on recovery");

        // 측정 불가는 진행도 초기화도 하지 않는다
        d.Reset();
        Run(0, 2500); Run(1, 2500); Run(2, null); Run(3, null); Run(4, 2500);
        check(Run(5, 2500).Count == 1, "unknown does not reset pending episode");

        // 절전 복귀·긴 공백: 대기 중인 지속 시간을 다시 센다
        d.Reset();
        Run(0, 2500); Run(1, 2500); Run(2, 2500);
        bool afterGap = false;
        for (int m = 180; m <= 184; m++) afterGap |= Run(m, 2500).Count > 0;
        check(!afterGap, "long gap restarts sustain");
        check(Run(185, 2500).Count == 1, "alert after sustain following gap");
        d.Reset();
        Run(0, 2500); Run(1, 2500); Run(2, 2500); Run(3, 2500);
        d.MarkDiscontinuity();
        bool afterResume = false;
        for (int m = 4; m <= 8; m++) afterResume |= Run(m, 2500).Count > 0;
        check(!afterResume && Run(9, 2500).Count == 1, "resume marks discontinuity");
        d.Reset();
        Run(10, 2500);
        check(Run(5, 2500).Count == 0, "clock moving backwards is a discontinuity");

        // 커널 값을 읽지 못하면 전체 상태는 측정 불가
        d.Reset();
        check(d.Evaluate(At(0), [Pool(null)], false).Assessment.Overall == MemoryLevel.Unknown, "unavailable is unknown");
        check(d.Evaluate(At(1), [Pool(300)], true).Assessment.Summary == "정상", "normal summary");

        // 증가 추세 지속 → 누수 의심(원인 미확정), 여러 항목 동시 알림은 한 번에 묶는다
        d.Reset();
        var trend = new MetricReading("growth", "시간당 풀 증가량", 600, new MetricThreshold(200, 500), "+600 MB/h", "주의 200 MB/h · 위험 500 MB/h", IsTrend: true);
        for (int m = 0; m < 5; m++) d.Evaluate(At(m), [Pool(2500), trend], true);
        var (leak, both) = d.Evaluate(At(5), [Pool(2500), trend], true);
        check(leak.LeakSuspected && leak.Overall == MemoryLevel.High && leak.Summary.Contains("누수 의심"), "sustained trend is leak suspicion");
        check(both.Count == 2, "multiple metrics alert together");
        var (title, body) = MemoryAlertText.Compose(both, TimeSpan.FromMinutes(5));
        check(title.Contains("위험") && body.Contains("의심") && body.Contains("확정되지 않았습니다"), "alert text separates suspicion from cause");
    }

    private static void SettingsChecks(Action<bool, string> check)
    {
        var s = new SysGlass.Settings.AppSettings
        {
            MemPagedWarnMb = 5000, MemPagedHighMb = 100,      // 순서 뒤바뀜 → 기본값
            MemAvailWarnPercent = 3, MemAvailHighPercent = 10, // 낮을수록 나쁜 항목의 순서 뒤바뀜 → 기본값
            MemKernelIntervalSec = 0, MemAlertRepeatMin = 99999, MemColorScheme = 7,
        };
        s.Normalize();
        check(s.MemPagedWarnMb == 2048 && s.MemPagedHighMb == 4096, "invalid pool pair restored");
        check(s.MemAvailWarnPercent == 15 && s.MemAvailHighPercent == 5, "invalid lower-is-worse pair restored");
        check(s.MemKernelIntervalSec == 1 && s.MemAlertRepeatMin == 1440 && s.MemColorScheme == 1, "memory settings clamped");

        // 수집 상태 → 판정 항목: 1시간 관측 전 증가량은 측정 불가이고 전체는 정상
        var defaults = new SysGlass.Settings.AppSettings();
        var state = new MemoryWatchState
        {
            Enabled = true,
            Kernel = new KernelMemorySample { PagedPool = 300 * MB, NonPagedPool = 200 * MB, CommitTotal = 10, CommitLimit = 100, PhysicalAvailable = 50, PhysicalTotal = 100 },
        };
        var readings = MemoryReadings.Build(state, defaults);
        var (a, _) = new MemoryAnomalyDetector().Evaluate(T0, readings, true);
        check(a.Overall == MemoryLevel.Normal && a.Metrics.Single(m => m.Key == "growth").Level == MemoryLevel.Unknown, "unmeasured growth does not mark normal state abnormal");

        // 실제 사례 수준(페이징 풀 15.22GB)은 위험
        var incident = state with { Kernel = state.Kernel! with { PagedPool = (ulong)(15.22 * 1024) * MB } };
        var (b, _) = new MemoryAnomalyDetector().Evaluate(T0, MemoryReadings.Build(incident, defaults), true);
        check(b.Overall == MemoryLevel.High && b.Summary.StartsWith("위험 · 페이징 풀"), "incident level paged pool is high");
    }
}
