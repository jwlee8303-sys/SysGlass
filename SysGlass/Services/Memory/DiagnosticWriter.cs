using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SysGlass.Models;
using SysGlass.Utilities;

namespace SysGlass.Services.Memory;

/// <summary>
/// 메모리 감시 기록. 정상 상태의 경량 요약(CSV), 알림 이력(JSONL), 이상 감지 시 상세 진단(JSON)을 저장하고
/// 보존 기간·총용량을 넘는 오래된 기록부터 정리한다. 이 클래스가 만든 이름 형식의 파일만 정리 대상이다.
/// 외부로 전송하지 않으며 명령줄·환경 변수·인증 정보는 기록하지 않는다.
/// </summary>
public sealed class DiagnosticWriter
{
    private const double MB = 1024 * 1024;
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    private static readonly string[] OwnedPatterns = ["summary-*.csv", "alerts-*.jsonl", "diag-*.json"];
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 한글을 그대로 기록(파일 전용, HTML에 넣지 않음)
    };
    /// <summary>JSON은 BOM 없이(RFC 8259), CSV는 Excel에서 한글이 깨지지 않도록 BOM을 붙여 쓴다.</summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private static readonly JsonSerializerOptions JsonLine = new(Json) { WriteIndented = false };

    public const string Notice = "이 파일은 메모리 누수 '의심' 상황의 참고 자료이며 원인을 확정하지 않습니다. " +
        "Pool Tag 설명과 드라이버 목록은 참고용입니다. 프로세스 명령줄·환경 변수·인증 정보는 수집하지 않습니다.";

    public DiagnosticWriter(string directory) => Directory = directory;

    public static string DefaultDirectory => Path.Combine(AppPaths.DataDir, "memory");
    public string Directory { get; }

    // ───────── 경량 요약 ─────────

    private const string SummaryHeader = "time,boot_time,uptime_min,status,paged_mb,nonpaged_mb,paged_resident_mb,commit_mb,commit_limit_mb," +
        "available_mb,total_mb,handles,processes,growth10m_mb,growth1h_mb,top_tag,top_tag_mb,top_growth1h_tag,top_growth1h_mb";

    /// <summary>요약 한 줄을 날짜별 CSV에 추가한다. 측정하지 못한 값은 빈 칸으로 남긴다(0으로 쓰지 않는다).</summary>
    public void AppendSummary(MemoryWatchState s)
    {
        var k = s.Kernel;
        var top = s.PoolTags is { Available: true } pt ? pt.Largest : null;
        var topGrowth = TopGrowth(s.PoolTags, s.PoolTags?.Growth1h, 1).FirstOrDefault();
        string[] cells =
        [
            s.Kernel?.Time.ToString("yyyy-MM-ddTHH:mm:sszzz", C) ?? DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", C),
            s.BootTime?.ToString("yyyy-MM-ddTHH:mm:sszzz", C) ?? "",
            s.Uptime is { } u ? ((long)u.TotalMinutes).ToString(C) : "",
            s.Assessment.Overall.ToString(),
            Mb(k?.PagedPool), Mb(k?.NonPagedPool), Mb(s.PagedResident), Mb(k?.CommitTotal), Mb(k?.CommitLimit),
            Mb(k?.PhysicalAvailable), Mb(k?.PhysicalTotal),
            k?.HandleCount?.ToString(C) ?? "", k?.ProcessCount?.ToString(C) ?? "",
            s.Growth10m.TotalDelta is { } g10 ? (g10 / MB).ToString("0.0", C) : "",
            s.Growth1h.TotalDelta is { } g1 ? (g1 / MB).ToString("0.0", C) : "",
            top?.Tag ?? "", top is { } t ? (t.TotalUsed / MB).ToString("0.0", C) : "",
            topGrowth.Tag ?? "", topGrowth.Tag is null ? "" : (topGrowth.Delta / MB).ToString("0.0", C),
        ];
        string path = Path.Combine(Directory, $"summary-{DateTime.Now:yyyyMMdd}.csv");
        Append(path, string.Join(",", cells.Select(Csv)), SummaryHeader);
    }

    /// <summary>알림 이력을 월별 JSONL에 추가한다.</summary>
    public void AppendAlerts(IEnumerable<MemoryAlert> alerts)
    {
        var sb = new StringBuilder();
        foreach (var a in alerts)
            sb.AppendLine(JsonSerializer.Serialize(new
            {
                time = a.Time, level = a.Level.ToString(), levelText = MemoryLevelText.Of(a.Level), a.Key, a.Label,
                value = a.ValueText, threshold = a.ThresholdText, escalated = a.Escalated, repeated = a.Repeated, trend = a.IsTrend,
            }, JsonLine));
        if (sb.Length == 0) return;
        System.IO.Directory.CreateDirectory(Directory);
        File.AppendAllText(Path.Combine(Directory, $"alerts-{DateTime.Now:yyyyMM}.jsonl"), sb.ToString(), Utf8NoBom);
    }

    // ───────── 상세 진단 ─────────

    public sealed record Context(
        string Reason, MemoryWatchState State, IReadOnlyList<MemoryAlert> Alerts, SystemInfo System,
        IReadOnlyCollection<MemoryHistory.Point> History, IReadOnlyList<DriverInfo> Drivers, string? DriverError,
        IReadOnlyList<ServiceInfo> Services, string? ServiceError, object? ProcessEvents);

    /// <summary>상세 진단 파일을 쓰고 경로를 돌려준다.</summary>
    public string WriteDiagnostic(Context c, DateTimeOffset now, string? targetPath = null)
    {
        var doc = BuildDiagnostic(c, now);
        string path = targetPath ?? UniquePath($"diag-{now:yyyyMMdd-HHmmss}", ".json");
        WriteAtomic(path, JsonSerializer.Serialize(doc, Json));
        return path;
    }

    /// <summary>알림 후 일정 시간이 지나 '발생 후' 변화를 기록한다.</summary>
    public string WriteFollowUp(string diagnosticPath, MemoryWatchState at, MemoryWatchState now)
    {
        var tagsNow = at.PoolTags?.Tags.ToDictionary(t => t.Key, t => (long)t.TotalUsed);
        var changes = new List<object>();
        if (tagsNow is not null && now.PoolTags is { Available: true } pn)
            foreach (var t in pn.Tags
                         .Select(t => (t.Tag, Delta: (long)t.TotalUsed - tagsNow.GetValueOrDefault(t.Key)))
                         .Where(x => x.Delta != 0).OrderByDescending(x => x.Delta).Take(30))
                changes.Add(new { tag = t.Tag, deltaMb = Round(t.Delta / MB), description = PoolTagCatalog.Describe(t.Tag) });
        var doc = new
        {
            schema = "sysglass-memory-followup/1",
            notice = Notice,
            diagnostic = Path.GetFileName(diagnosticPath),
            from = at.Kernel?.Time,
            to = now.Kernel?.Time,
            pagedDeltaMb = Delta(at.Kernel?.PagedPool, now.Kernel?.PagedPool),
            nonPagedDeltaMb = Delta(at.Kernel?.NonPagedPool, now.Kernel?.NonPagedPool),
            commitDeltaMb = Delta(at.Kernel?.CommitTotal, now.Kernel?.CommitTotal),
            statusAfter = MemoryLevelText.Of(now.Assessment.Overall),
            poolTagChanges = now.PoolTags is { Available: true } ? (object)changes : $"Pool Tag 수집 불가: {now.PoolTags?.Error ?? "수집 전"}",
        };
        string path = Path.Combine(Directory, Path.GetFileNameWithoutExtension(diagnosticPath) + "-after.json");
        WriteAtomic(path, JsonSerializer.Serialize(doc, Json));
        return path;
    }

    internal static object BuildDiagnostic(Context c, DateTimeOffset now)
    {
        var s = c.State;
        var k = s.Kernel;
        var a = s.Assessment;
        return new
        {
            schema = "sysglass-memory-diagnostic/1",
            createdAt = now,
            reason = c.Reason,
            notice = Notice,
            system = new
            {
                product = c.System.ProductName, displayVersion = c.System.DisplayVersion, version = c.System.VersionText,
                fastStartup = new { hiberbootEnabled = c.System.HiberbootEnabled, hibernateEnabled = c.System.HibernateEnabled, active = c.System.FastStartupActive },
                bootTime = s.BootTime, uptimeMinutes = s.Uptime is { } u ? (long?)u.TotalMinutes : null,
            },
            assessment = new
            {
                overall = MemoryLevelText.Of(a.Overall), leakSuspected = a.LeakSuspected, summary = a.Summary,
                metrics = a.Metrics.Select(m => new { m.Key, m.Label, level = MemoryLevelText.Of(m.Level), value = m.ValueText, threshold = m.ThresholdText, m.Sustained }),
            },
            alerts = c.Alerts.Select(x => new { x.Time, level = MemoryLevelText.Of(x.Level), x.Label, value = x.ValueText, threshold = x.ThresholdText, x.Escalated, x.Repeated }),
            kernel = k is null ? null : new
            {
                k.Time, pagedMb = Mb2(k.PagedPool), nonPagedMb = Mb2(k.NonPagedPool), pagedResidentMb = Mb2(s.PagedResident),
                commitMb = Mb2(k.CommitTotal), commitLimitMb = Mb2(k.CommitLimit), commitPercent = Round(k.CommitPercent),
                availableMb = Mb2(k.PhysicalAvailable), totalMb = Mb2(k.PhysicalTotal), availablePercent = Round(k.AvailablePercent),
                k.HandleCount, k.ProcessCount, k.ThreadCount, error = s.KernelError,
            },
            growth = new
            {
                observedMinutes = (long)s.Observed.TotalMinutes,
                last10m = GrowthDoc(s.Growth10m), last1h = GrowthDoc(s.Growth1h), last24h = GrowthDoc(s.Growth24h),
                note = "시간축은 절전 시간을 제외한 활성 시간입니다. 관측이 부족한 구간은 null입니다.",
            },
            // 발생 전 변화: 1분 간격으로 줄인 풀 사용량 이력
            history = c.History.GroupBy(p => p.Time.ToString("yyyy-MM-ddTHH:mm", C)).Select(g => g.Last())
                .Select(p => new { p.Time, pagedMb = Round(p.Paged / MB), nonPagedMb = Round(p.NonPaged / MB) }),
            poolTags = PoolTagsDoc(s.PoolTags),
            processes = ProcessesDoc(s.Processes),
            processEvents = c.ProcessEvents is ProcessEventSummary pe ? ProcessEventsDoc(pe) : c.ProcessEvents,
            drivers = new { error = c.DriverError, count = c.Drivers.Count, list = c.Drivers },
            services = new { error = c.ServiceError, count = c.Services.Count, list = c.Services },
        };
    }

    private static object GrowthDoc(PoolGrowth g) => new
    {
        measured = g.Measured, pagedMb = g.PagedDelta is { } p ? Round(p / MB) : null, nonPagedMb = g.NonPagedDelta is { } n ? Round(n / MB) : null,
        spanMinutes = g.Measured ? (long?)g.Span.TotalMinutes : null,
    };

    private static object PoolTagsDoc(PoolTagReport? r)
    {
        if (r is null) return new { available = false, error = "수집 전" };
        if (!r.Available) return new { available = false, error = r.Error };
        object Row(PoolTagEntry t) => new
        {
            tag = t.Tag, pagedMb = Round(t.PagedUsed / MB), nonPagedMb = Round(t.NonPagedUsed / MB),
            pagedAllocs = t.PagedAllocs, pagedFrees = t.PagedFrees, nonPagedAllocs = t.NonPagedAllocs, nonPagedFrees = t.NonPagedFrees,
            outstanding = t.Outstanding,
            growth10mMb = Growth(r.Growth10m, t.Key), growth1hMb = Growth(r.Growth1h, t.Key),
            description = PoolTagCatalog.Describe(t.Tag),
        };
        return new
        {
            available = true, r.Time, count = r.Tags.Count,
            note = PoolTagCatalog.Disclaimer + " 증가량이 null이면 관측 시간이 부족한 것입니다. 할당·반환 횟수는 32비트 누적 카운터라 넘칠 수 있습니다.",
            topUsage = r.Tags.OrderByDescending(t => t.TotalUsed).Take(30).Select(Row),
            topGrowth1h = TopGrowth(r, r.Growth1h, 30).Select(x => new { tag = x.Tag, deltaMb = Round(x.Delta / MB), description = PoolTagCatalog.Describe(x.Tag!) }),
            topGrowth10m = TopGrowth(r, r.Growth10m, 30).Select(x => new { tag = x.Tag, deltaMb = Round(x.Delta / MB), description = PoolTagCatalog.Describe(x.Tag!) }),
            all = r.Tags.OrderByDescending(t => t.TotalUsed).Select(Row),
        };
    }

    private static object ProcessesDoc(ProcessReport? r)
    {
        if (r is null) return new { available = false, error = "수집 전" };
        if (r.Error is not null) return new { available = false, error = r.Error };
        var byPid = r.Processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
        object Row(ProcessMemoryInfo p)
        {
            // 부모 PID가 재사용됐을 수 있으므로 부모가 자식보다 먼저 생성된 경우에만 이름을 붙인다.
            string? parent = byPid.TryGetValue(p.ParentPid, out var pp) && pp.CreateTime <= p.CreateTime && pp.Pid != p.Pid ? pp.Name : null;
            var ch = r.Change1h is { } c && c.TryGetValue(p.Key, out var v) ? v : ((long, long)?)null;
            return new
            {
                pid = p.Pid, name = p.Name, parentPid = p.ParentPid, parentName = parent,
                privateMb = Round(p.PrivateBytes / MB), workingSetMb = Round(p.WorkingSet / MB), handles = p.HandleCount, threads = p.ThreadCount,
                pagedPoolQuotaKb = p.PagedPoolQuota / 1024, nonPagedPoolQuotaKb = p.NonPagedPoolQuota / 1024,
                privateChange1hMb = ch is { } x ? Round(x.Item1 / MB) : null, handleChange1h = ch?.Item2,
            };
        }
        var withChange = r.Change1h is { } chg ? r.Processes.Where(p => chg.ContainsKey(p.Key)).ToList() : [];
        return new
        {
            available = true, r.Time, count = r.Processes.Count,
            startedSinceLastSnapshot = r.StartedSinceLast, exitedSinceLastSnapshot = r.ExitedSinceLast,
            note = "생성·종료 수는 스냅샷 비교 값이라 짧게 실행된 프로세스는 빠질 수 있습니다. 1시간 변화량이 null이면 1시간 전 스냅샷이 없는 것입니다.",
            topPrivate = r.Processes.OrderByDescending(p => p.PrivateBytes).Take(30).Select(Row),
            topHandles = r.Processes.OrderByDescending(p => p.HandleCount).Take(30).Select(Row),
            topPrivateGrowth1h = withChange.OrderByDescending(p => r.Change1h![p.Key].Private).Take(20).Select(Row),
            topHandleGrowth1h = withChange.OrderByDescending(p => r.Change1h![p.Key].Handles).Take(20).Select(Row),
        };
    }

    internal static object ProcessEventsDoc(ProcessEventSummary e) => new
    {
        available = e.Available, error = e.Error, since = e.Since,
        method = "WMI 이벤트(Win32_ProcessStartTrace/StopTrace)",
        starts10m = e.Starts10m, stops10m = e.Stops10m, starts1h = e.Starts1h, stops1h = e.Stops1h,
        topStarters1h = e.TopStarters1h.Select(x => new { name = x.Name, starts = x.Starts }),
        namesMergedAsOther = e.Overflowed,
        recent = e.Recent.Take(100).Select(r => new { r.Time, kind = r.Kind.ToString(), pid = r.Pid, parentPid = r.ParentPid, name = r.Name }),
        note = e.Available ? "구독 시작 이후 1시간까지만 집계합니다. 명령줄은 수집하지 않습니다."
                           : "이벤트 수집을 하지 못했습니다. processes의 스냅샷 비교 값은 짧게 실행된 프로세스를 놓칠 수 있습니다.",
    };

    private static IEnumerable<(string? Tag, long Delta)> TopGrowth(PoolTagReport? r, IReadOnlyDictionary<uint, long>? growth, int n)
    {
        if (r is not { Available: true } || growth is null) return [];
        var names = r.Tags.ToDictionary(t => t.Key, t => t.Tag);
        return growth.Where(g => g.Value > 0).OrderByDescending(g => g.Value).Take(n)
            .Select(g => ((string?)(names.TryGetValue(g.Key, out var name) ? name : PoolTagCollector.TagText(g.Key)), g.Value));
    }

    private static double? Growth(IReadOnlyDictionary<uint, long>? g, uint key) => g is null ? null : Round(g.GetValueOrDefault(key) / MB);

    // ───────── 정리 ─────────

    /// <summary>보존 기간이 지난 기록을 지우고, 총용량이 상한을 넘으면 오래된 기록부터 지운다. 지운 파일 수를 돌려준다.</summary>
    public int Cleanup(int retentionDays, long maxBytes, DateTime nowUtc)
    {
        if (!System.IO.Directory.Exists(Directory)) return 0;
        var files = OwnedPatterns.SelectMany(p => new DirectoryInfo(Directory).EnumerateFiles(p))
            .OrderBy(f => f.LastWriteTimeUtc).ToList();
        int deleted = 0;
        long total = files.Sum(f => f.Length);
        foreach (var f in files)
        {
            bool expired = nowUtc - f.LastWriteTimeUtc > TimeSpan.FromDays(retentionDays);
            if (!expired && total <= maxBytes) continue;
            try
            {
                long len = f.Length;
                f.Delete();
                total -= len;
                deleted++;
            }
            catch (Exception ex) { Logger.Error($"진단 기록 정리 실패: {f.Name}", ex); }
        }
        return deleted;
    }

    // ───────── 도우미 ─────────

    private void Append(string path, string line, string header)
    {
        System.IO.Directory.CreateDirectory(Directory);
        bool isNew = !File.Exists(path);
        File.AppendAllText(path, (isNew ? header + Environment.NewLine : "") + line + Environment.NewLine, Encoding.UTF8);
    }

    private string UniquePath(string stem, string ext)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string path = Path.Combine(Directory, stem + ext);
        for (int i = 2; File.Exists(path) && i < 100; i++) path = Path.Combine(Directory, $"{stem}-{i}{ext}");
        return path;
    }

    private static void WriteAtomic(string path, string text)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, Utf8NoBom);
        File.Move(tmp, path, true);
    }

    private static string Csv(string v) => v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    private static string Mb(ulong? b) => b is { } x ? (x / MB).ToString("0.0", C) : "";
    private static double? Mb2(ulong? b) => b is { } x ? Round(x / MB) : null;
    private static double? Delta(ulong? from, ulong? to) => from is { } f && to is { } t ? Round(((double)t - f) / MB) : null;
    private static double? Round(double? v) => v is { } x ? Math.Round(x, 1) : null;
}
