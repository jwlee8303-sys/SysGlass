namespace SysGlass.Models;

/// <summary>커널·시스템 메모리 표본. 측정하지 못한 값은 null이며 0으로 대신하지 않는다.</summary>
public sealed record KernelMemorySample
{
    public DateTimeOffset Time { get; init; }
    /// <summary>절전 시간을 제외한 부팅 후 활성 시간(100ns). 추세 계산의 시간축으로 쓴다.</summary>
    public long ActiveTime100ns { get; init; }

    public ulong? PhysicalTotal { get; init; }
    public ulong? PhysicalAvailable { get; init; }
    public ulong? CommitTotal { get; init; }
    public ulong? CommitLimit { get; init; }
    public ulong? PagedPool { get; init; }
    public ulong? NonPagedPool { get; init; }
    public uint? HandleCount { get; init; }
    public uint? ProcessCount { get; init; }
    public uint? ThreadCount { get; init; }

    public double? CommitPercent =>
        CommitTotal is { } c && CommitLimit is { } l && l > 0 ? c * 100.0 / l : null;

    public double? AvailablePercent =>
        PhysicalAvailable is { } a && PhysicalTotal is { } t && t > 0 ? a * 100.0 / t : null;
}

/// <summary>
/// 지정 구간의 풀 증가량. 관측 시간이 구간보다 짧으면 Measured가 false이고 값은 null(미측정)이다.
/// Span은 실제로 비교한 두 표본의 활성 시간 차이다.
/// </summary>
public sealed record PoolGrowth(TimeSpan Window, long? PagedDelta, long? NonPagedDelta, TimeSpan Span)
{
    public bool Measured => PagedDelta is not null && NonPagedDelta is not null;
    public long? TotalDelta => Measured ? PagedDelta + NonPagedDelta : null;

    /// <summary>시간당 증가량(바이트). 미측정이면 null.</summary>
    public double? TotalPerHour => TotalDelta is { } d && Span.TotalHours > 0 ? d / Span.TotalHours : null;

    public static PoolGrowth Unmeasured(TimeSpan window) => new(window, null, null, TimeSpan.Zero);
}

/// <summary>메모리 감시 서비스가 화면·기록에 넘기는 한 시점의 상태. 불변이므로 스레드 간에 그대로 전달한다.</summary>
public sealed record MemoryWatchState
{
    public bool Enabled { get; init; }
    public KernelMemorySample? Kernel { get; init; }
    public string? KernelError { get; init; }

    /// <summary>페이징 풀 상주량. 30초 주기로 갱신되므로 시각을 함께 둔다.</summary>
    public ulong? PagedResident { get; init; }
    public DateTimeOffset? PagedResidentTime { get; init; }
    public string? PagedResidentError { get; init; }

    /// <summary>시스템 가동 시간(절전 포함, 작업 관리자 기준과 같음).</summary>
    public TimeSpan? Uptime { get; init; }
    public DateTimeOffset? BootTime { get; init; }

    public PoolGrowth Growth10m { get; init; } = PoolGrowth.Unmeasured(TimeSpan.FromMinutes(10));
    public PoolGrowth Growth1h { get; init; } = PoolGrowth.Unmeasured(TimeSpan.FromHours(1));
    public PoolGrowth Growth24h { get; init; } = PoolGrowth.Unmeasured(TimeSpan.FromHours(24));
    /// <summary>현재 부팅 세션에서 추세를 관측한 활성 시간.</summary>
    public TimeSpan Observed { get; init; }

    public MemoryAssessment Assessment { get; init; } = MemoryAssessment.Unknown;

    /// <summary>최근 Pool Tag 보고서. 수집 전이면 null, 실패하면 Error에 사유가 있다.</summary>
    public PoolTagReport? PoolTags { get; init; }
    /// <summary>최근 프로세스 보고서. 수집 전이면 null.</summary>
    public ProcessReport? Processes { get; init; }
    /// <summary>이벤트 기반 프로세스 생성·종료 통계.</summary>
    public ProcessEventSummary ProcessEvents { get; init; } = ProcessEventSummary.NotStarted;
}

/// <summary>
/// Pool Tag 하나의 누적 카운터. 할당·반환 횟수는 32비트라 넘칠 수 있으므로 차이(미반환 수)는 부호 없는 뺄셈으로 구한다.
/// </summary>
public readonly record struct PoolTagEntry(uint Key, string Tag, ulong PagedUsed, ulong NonPagedUsed,
    uint PagedAllocs, uint PagedFrees, uint NonPagedAllocs, uint NonPagedFrees)
{
    public ulong TotalUsed => PagedUsed + NonPagedUsed;
    /// <summary>할당 - 반환(미반환 할당 수). 32비트 카운터 넘침을 고려한 값.</summary>
    public uint Outstanding => unchecked(PagedAllocs - PagedFrees + (NonPagedAllocs - NonPagedFrees));
}

/// <summary>Pool Tag 수집 결과와 태그별 증가량. Tags가 비어 있고 Error가 있으면 수집 실패(0으로 간주하지 않는다).</summary>
public sealed record PoolTagReport(DateTimeOffset Time, IReadOnlyList<PoolTagEntry> Tags, string? Error)
{
    /// <summary>태그 키 → 증가량(바이트). 관측이 부족하면 해당 구간 사전은 null(미측정).</summary>
    public IReadOnlyDictionary<uint, long>? Growth10m { get; init; }
    public IReadOnlyDictionary<uint, long>? Growth1h { get; init; }
    public bool Available => Error is null;
    public PoolTagEntry? Largest => Tags.Count == 0 ? null : Tags.MaxBy(t => t.TotalUsed);
}

/// <summary>프로세스 메모리 정보. 명령줄·환경 변수는 수집하지 않는다.</summary>
public sealed record ProcessMemoryInfo(int Pid, int ParentPid, string Name, long CreateTime, uint HandleCount, uint ThreadCount,
    ulong PrivateBytes, ulong WorkingSet, ulong PagedPoolQuota, ulong NonPagedPoolQuota)
{
    /// <summary>같은 실행 단위를 식별하는 키(PID 재사용 구분).</summary>
    public (int, long) Key => (Pid, CreateTime);
}

/// <summary>프로세스 수집 결과. 생성·종료 수는 이전 스냅샷과 비교한 값이라 짧게 실행된 프로세스는 빠질 수 있다.</summary>
public sealed record ProcessReport(DateTimeOffset Time, IReadOnlyList<ProcessMemoryInfo> Processes, string? Error)
{
    /// <summary>프로세스 키 → (Private Bytes 변화, 핸들 변화). 1시간 전 스냅샷이 없으면 null.</summary>
    public IReadOnlyDictionary<(int, long), (long Private, long Handles)>? Change1h { get; init; }
    public int StartedSinceLast { get; init; }
    public int ExitedSinceLast { get; init; }
}

public enum ProcessEventKind { Start, Stop }

public sealed record ProcessEventRecord(DateTimeOffset Time, ProcessEventKind Kind, int Pid, int? ParentPid, string Name);

/// <summary>
/// 이벤트 기반 프로세스 생성·종료 통계. Available이 false면 이벤트 수집을 하지 못한 것이며(Error에 사유),
/// 이때는 스냅샷 비교 값만 참고할 수 있다.
/// </summary>
public sealed record ProcessEventSummary(bool Available, string? Error, DateTimeOffset? Since,
    int Starts10m, int Stops10m, int Starts1h, int Stops1h,
    IReadOnlyList<(string Name, int Starts)> TopStarters1h, IReadOnlyList<ProcessEventRecord> Recent, long Overflowed)
{
    public static ProcessEventSummary NotStarted { get; } = new(false, "수집 전", null, 0, 0, 0, 0, [], [], 0);
}

/// <summary>Windows 버전·빠른 시작 등 시스템 정보. 읽지 못한 값은 null.</summary>
public sealed record SystemInfo(string? ProductName, string? DisplayVersion, int? Major, int? Minor, int? Build, int? Ubr,
    bool? HiberbootEnabled, bool? HibernateEnabled)
{
    public string VersionText => Build is { } b ? $"{Major}.{Minor}.{b}{(Ubr is { } u ? "." + u : "")}" : "알 수 없음";
    /// <summary>빠른 시작은 최대 절전이 켜져 있어야 동작한다.</summary>
    public bool? FastStartupActive => HiberbootEnabled is { } h ? h && HibernateEnabled != false : null;
}

/// <summary>실행 중인 드라이버·서비스. 서비스 실행 경로는 인자가 포함될 수 있어 수집하지 않는다.</summary>
public sealed record DriverInfo(string Name, string? DisplayName, string? Path, string? Company, string? FileVersion);
public sealed record ServiceInfo(string Name, string? DisplayName, string? StartMode, int? ProcessId);

/// <summary>감시 상태. Unknown은 측정 불가(값이 없거나 수집 실패)이며 정상으로 간주하지 않는다.</summary>
public enum MemoryLevel { Unknown, Normal, Warn, High }

public static class MemoryLevelText
{
    public static string Of(MemoryLevel level) => level switch
    {
        MemoryLevel.Normal => "정상",
        MemoryLevel.Warn => "주의",
        MemoryLevel.High => "위험",
        _ => "측정 불가",
    };
}

/// <summary>주의·위험 기준. LowerIsWorse면 값이 기준 이하일 때 이상이다(가용 RAM 비율 등).</summary>
public sealed record MetricThreshold(double Warn, double High, bool LowerIsWorse = false)
{
    public MemoryLevel Evaluate(double? value)
    {
        if (value is not { } v || !double.IsFinite(v)) return MemoryLevel.Unknown;
        if (LowerIsWorse) return v <= High ? MemoryLevel.High : v <= Warn ? MemoryLevel.Warn : MemoryLevel.Normal;
        return v >= High ? MemoryLevel.High : v >= Warn ? MemoryLevel.Warn : MemoryLevel.Normal;
    }
}

/// <summary>판정에 쓰는 한 항목의 현재 값. Value가 null이면 측정 불가. IsTrend는 증가 추세 항목(누수 의심 판단용).</summary>
public sealed record MetricReading(string Key, string Label, double? Value, MetricThreshold Threshold,
    string ValueText, string ThresholdText, bool IsTrend = false);

/// <summary>항목별 판정 결과. Sustained는 이상 상태가 지속 시간 이상 이어졌는지 여부.</summary>
public sealed record MetricStatus(string Key, string Label, MemoryLevel Level, string ValueText, string ThresholdText,
    bool Sustained, bool IsTrend);

public sealed record MemoryAlert(DateTimeOffset Time, MemoryLevel Level, string Key, string Label,
    string ValueText, string ThresholdText, bool Escalated, bool Repeated, bool IsTrend);

/// <summary>전체 판정. LeakSuspected는 증가 추세가 지속된 '누수 의심' 단계이며 원인 확정을 의미하지 않는다.</summary>
public sealed record MemoryAssessment(MemoryLevel Overall, IReadOnlyList<MetricStatus> Metrics, bool LeakSuspected)
{
    public static MemoryAssessment Unknown { get; } = new(MemoryLevel.Unknown, [], false);

    public string Summary
    {
        get
        {
            if (Overall <= MemoryLevel.Normal) return MemoryLevelText.Of(Overall);
            var worst = Metrics.FirstOrDefault(m => m.Level == Overall)?.Label;
            string text = worst is null ? MemoryLevelText.Of(Overall) : $"{MemoryLevelText.Of(Overall)} · {worst}";
            return LeakSuspected ? text + " · 누수 의심" : text;
        }
    }
}

/// <summary>알림 문구. 누수 의심과 원인 확정을 구분해 표현한다.</summary>
public static class MemoryAlertText
{
    public static (string Title, string Body) Compose(IReadOnlyList<MemoryAlert> alerts, TimeSpan sustain)
    {
        var level = alerts.Max(a => a.Level);
        string title = $"SysGlass 메모리 {MemoryLevelText.Of(level)}" + (alerts.Any(a => a.Escalated) ? " (악화)" : "");
        var lines = alerts.OrderByDescending(a => a.Level).Select(a => $"{a.Label} {a.ValueText} ({a.ThresholdText})");
        string tail = alerts.Any(a => a.IsTrend)
            ? "증가 추세가 이어져 누수가 의심됩니다. 원인은 확정되지 않았습니다."
            : $"{(int)sustain.TotalMinutes}분 이상 지속되었습니다. 원인은 확정되지 않았습니다.";
        return (title, string.Join("\n", lines) + "\n" + tail);
    }
}
