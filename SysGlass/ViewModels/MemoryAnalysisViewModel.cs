using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using SysGlass.Models;
using SysGlass.Services.Memory;
using SysGlass.Settings;
using SysGlass.Utilities;
using SysGlass.Views;

namespace SysGlass.ViewModels;

public sealed record PoolTagRow(uint Key, string Tag, double PagedMb, double NonPagedMb, uint Outstanding,
    double? Growth10mMb, double? Growth1hMb, string Description)
{
    public double TotalMb => PagedMb + NonPagedMb;
    public string PagedText => Mb(PagedMb);
    public string NonPagedText => Mb(NonPagedMb);
    public string TotalText => Mb(TotalMb);
    public string OutstandingText => Outstanding.ToString("N0", CultureInfo.InvariantCulture);
    public string Growth10mText => Delta(Growth10mMb);
    public string Growth1hText => Delta(Growth1hMb);
    internal static string Mb(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    internal static string Delta(double? v) => v is { } x ? (x >= 0 ? "+" : "") + x.ToString("0.0", CultureInfo.InvariantCulture) : "미측정";
}

public sealed record ProcessRow(int Pid, string Name, string Parent, double PrivateMb, double? PrivateChange1hMb,
    uint Handles, long? HandleChange1h, double WorkingSetMb, uint Threads, ulong PagedQuotaKb)
{
    public string PrivateText => PoolTagRow.Mb(PrivateMb);
    public string PrivateChangeText => PoolTagRow.Delta(PrivateChange1hMb);
    public string HandlesText => Handles.ToString("N0", CultureInfo.InvariantCulture);
    public string HandleChangeText => HandleChange1h is { } h ? (h >= 0 ? "+" : "") + h.ToString("N0", CultureInfo.InvariantCulture) : "미측정";
    public string WorkingSetText => PoolTagRow.Mb(WorkingSetMb);
}

public sealed record EventRow(string Time, string Kind, int Pid, string ParentPid, string Name);
public sealed record CountRow(string Name, int Count);
public sealed record AlertRow(DateTimeOffset Time, string Level, Brush LevelBrush, string Label, string Value, string Threshold, string Flags)
{
    public string TimeText => Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>
/// 메모리 상세 분석 창. 서비스 상태를 받아 그래프·Pool Tag·프로세스·이벤트·이상 이력을 보여준다.
/// 표는 새 보고서가 들어왔을 때만(기본 60초) 다시 만들고, 그래프는 15초 이상 간격으로 갱신한다.
/// </summary>
public sealed class MemoryAnalysisViewModel : ViewModelBase, IDisposable
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    private const double MB = 1024 * 1024;

    private readonly MemoryWatchService _svc;
    private readonly AppSettings _s;
    private PoolTagReport? _lastTags;
    private ProcessReport? _lastProcs;
    private List<PoolTagRow> _allTags = [];
    private List<ProcessRow> _allProcs = [];
    private DateTime _lastChart;
    private CancellationTokenSource? _searchCts;
    private readonly List<AlertRow> _alerts = [];
    private bool _disposed;

    public MemoryAnalysisViewModel(MemoryWatchService svc, AppSettings settings)
    {
        _svc = svc;
        _s = settings;
    }

    public void Start()
    {
        _svc.Updated += OnUpdated;
        _svc.AlertRaised += OnAlerts;
        _svc.DiagnosticWritten += OnDiagnostic;
        LoadAlertHistory();
        Apply(_svc.Current, force: true);
    }

    // ───────── 개요 ─────────
    private string _statusText = "", _overviewText = "", _diagnosticStatus = "";
    private Brush _statusBrush = MemoryBrushes.Unknown;
    private IReadOnlyList<ChartPoint>? _chart;
    private int _chartHours = 1;

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }
    public string OverviewText { get => _overviewText; private set => Set(ref _overviewText, value); }
    public string DiagnosticStatus { get => _diagnosticStatus; private set => Set(ref _diagnosticStatus, value); }
    public IReadOnlyList<ChartPoint>? ChartPoints { get => _chart; private set => Set(ref _chart, value); }
    public double WarnMb => _s.MemPagedWarnMb;
    public int ChartHours
    {
        get => _chartHours;
        set { if (Set(ref _chartHours, value is 24 ? 24 : 1)) RefreshChart(); }
    }

    // ───────── Pool Tag ─────────
    private IReadOnlyList<PoolTagRow> _tags = [];
    private string _tagFilter = "", _tagStatus = "", _tagSort = "Total", _searchResult = "";
    private bool _tagAsc, _searching;
    private PoolTagRow? _selectedTag;

    public IReadOnlyList<PoolTagRow> TagRows { get => _tags; private set => Set(ref _tags, value); }
    public string TagStatus { get => _tagStatus; private set => Set(ref _tagStatus, value); }
    public string TagFilter { get => _tagFilter; set { if (Set(ref _tagFilter, value ?? "")) ApplyTags(); } }
    public string TagSortKey => _tagSort;
    public bool TagSortAscending => _tagAsc;
    public PoolTagRow? SelectedTag { get => _selectedTag; set { if (Set(ref _selectedTag, value)) Raise(nameof(CanSearch)); } }
    public string SearchResult { get => _searchResult; private set => Set(ref _searchResult, value); }
    public bool IsSearching { get => _searching; private set { if (Set(ref _searching, value)) Raise(nameof(CanSearch)); } }
    public bool CanSearch => !_searching && _selectedTag is { } t && DriverTagSearch.IsSearchable(t.Tag);

    public void SortTags(string key)
    {
        _tagAsc = _tagSort == key ? !_tagAsc : key == "Tag";
        _tagSort = key;
        ApplyTags();
        SortChanged?.Invoke();
    }

    // ───────── 프로세스 ─────────
    private IReadOnlyList<ProcessRow> _procs = [];
    private string _procFilter = "", _procStatus = "", _procSort = "Private";
    private bool _procAsc;

    public IReadOnlyList<ProcessRow> ProcessRows { get => _procs; private set => Set(ref _procs, value); }
    public string ProcessStatus { get => _procStatus; private set => Set(ref _procStatus, value); }
    public string ProcessFilter { get => _procFilter; set { if (Set(ref _procFilter, value ?? "")) ApplyProcs(); } }
    public string ProcessSortKey => _procSort;
    public bool ProcessSortAscending => _procAsc;

    public void SortProcesses(string key)
    {
        _procAsc = _procSort == key ? !_procAsc : key is "Name" or "Parent";
        _procSort = key;
        ApplyProcs();
        SortChanged?.Invoke();
    }

    public event Action? SortChanged;

    // ───────── 이벤트·이력 ─────────
    private string _eventStatus = "";
    private IReadOnlyList<CountRow> _topStarters = [];
    private IReadOnlyList<EventRow> _recentEvents = [];
    private IReadOnlyList<AlertRow> _alertRows = [];

    public string EventStatus { get => _eventStatus; private set => Set(ref _eventStatus, value); }
    public IReadOnlyList<CountRow> TopStarters { get => _topStarters; private set => Set(ref _topStarters, value); }
    public IReadOnlyList<EventRow> RecentEvents { get => _recentEvents; private set => Set(ref _recentEvents, value); }
    public IReadOnlyList<AlertRow> AlertRows { get => _alertRows; private set => Set(ref _alertRows, value); }

    // ───────── 갱신 ─────────

    private void OnUpdated(MemoryWatchState s) =>
        Application.Current?.Dispatcher.BeginInvoke(() => { if (!_disposed) Apply(s, force: false); });

    private void OnAlerts(IReadOnlyList<MemoryAlert> alerts) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            foreach (var a in alerts) AddAlert(ToRow(a));
            PublishAlerts();
        });

    private void OnDiagnostic(string? path, string? error) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!_disposed) DiagnosticStatus = path is not null ? $"저장됨: {path}" : error ?? "저장 실패";
        });

    private void Apply(MemoryWatchState s, bool force)
    {
        var a = s.Assessment;
        StatusText = s.Enabled ? a.Summary : "메모리 감시 꺼짐";
        StatusBrush = MemoryBrushes.For(s.Enabled ? a.Overall : MemoryLevel.Unknown, _s.MemColorScheme);
        OverviewText = BuildOverview(s);
        if (force || DateTime.UtcNow - _lastChart > TimeSpan.FromSeconds(15)) RefreshChart();

        if (force || !ReferenceEquals(s.PoolTags, _lastTags))
        {
            _lastTags = s.PoolTags;
            _allTags = s.PoolTags is { Available: true } tr
                ? tr.Tags.Select(t => new PoolTagRow(t.Key, t.Tag, t.PagedUsed / MB, t.NonPagedUsed / MB, t.Outstanding,
                    tr.Growth10m is { } g10 ? g10.GetValueOrDefault(t.Key) / MB : null,
                    tr.Growth1h is { } g1 ? g1.GetValueOrDefault(t.Key) / MB : null,
                    PoolTagCatalog.Describe(t.Tag) ?? "")).ToList()
                : [];
            TagStatus = s.PoolTags switch
            {
                null => "Pool Tag 수집 전입니다.",
                { Available: false } f => $"Pool Tag 수집 불가: {f.Error} (일반 모니터링은 계속됩니다)",
                var r => $"{r.Tags.Count:N0}개 태그 · {r.Time:HH:mm:ss} 수집 · 단위 MB" +
                         (r.Growth1h is null ? " · 1시간 증가량은 관측 1시간 후부터 표시" : "") + " · " + PoolTagCatalog.Disclaimer,
            };
            ApplyTags();
        }
        if (force || !ReferenceEquals(s.Processes, _lastProcs))
        {
            _lastProcs = s.Processes;
            _allProcs = BuildProcessRows(s.Processes);
            ProcessStatus = s.Processes switch
            {
                null => "프로세스 수집 전입니다.",
                { Error: { } e } => $"프로세스 수집 불가: {e}",
                var r => $"{r.Processes.Count:N0}개 프로세스 · {r.Time:HH:mm:ss} 수집 · 단위 MB · 명령줄은 수집하지 않습니다" +
                         (r.Change1h is null ? " · 1시간 변화량은 관측 1시간 후부터 표시" : ""),
            };
            ApplyProcs();
        }
        ApplyEvents(s);
    }

    private static string BuildOverview(MemoryWatchState s)
    {
        if (!s.Enabled) return "설정에서 메모리 감시를 켜면 표시됩니다.";
        var k = s.Kernel;
        string G(PoolGrowth g) => g.Measured ? Format.SignedBytes(g.TotalDelta) : "미측정";
        var sb = new StringBuilder();
        sb.AppendLine($"페이징 풀 {Format.Bytes(k?.PagedPool)} (상주 {Format.Bytes(s.PagedResident)}) · 비페이징 풀 {Format.Bytes(k?.NonPagedPool)}");
        sb.AppendLine($"풀 증가량  10분 {G(s.Growth10m)} · 1시간 {G(s.Growth1h)} · 24시간 {G(s.Growth24h)} (관측 {Format.Duration(s.Observed)}, 절전 시간 제외)");
        sb.AppendLine($"커밋 {Format.Bytes(k?.CommitTotal)} / {Format.Bytes(k?.CommitLimit)} · 가용 RAM {Format.Bytes(k?.PhysicalAvailable)} / {Format.Bytes(k?.PhysicalTotal)}");
        sb.AppendLine($"핸들 {k?.HandleCount?.ToString("N0", C) ?? Format.NA} · 프로세스 {k?.ProcessCount?.ToString("N0", C) ?? Format.NA} · 가동 시간 {Format.Duration(s.Uptime)}");
        foreach (var m in s.Assessment.Metrics)
            sb.AppendLine($"  {m.Label}: {m.ValueText} · {MemoryLevelText.Of(m.Level)}{(m.Level >= MemoryLevel.Warn && !m.Sustained ? " (지속 확인 중)" : "")} · {m.ThresholdText}");
        if (s.Assessment.LeakSuspected) sb.AppendLine("누수 의심 단계입니다. 원인은 확정되지 않았습니다.");
        return sb.ToString().TrimEnd();
    }

    private void RefreshChart()
    {
        _lastChart = DateTime.UtcNow;
        var cutoff = DateTimeOffset.Now.AddHours(-_chartHours);
        var hist = _svc.GetHistory();
        // 24시간 보기는 1분 간격 이력이면 충분하고, 그래프 점 수를 줄인다.
        var pts = hist.Where(p => p.Time >= cutoff)
            .Select(p => new ChartPoint(p.Time, p.Paged / MB, p.NonPaged / MB)).ToList();
        if (pts.Count > 1500) pts = pts.Where((_, i) => i % (pts.Count / 1500 + 1) == 0).ToList();
        ChartPoints = pts;
        Raise(nameof(WarnMb));
    }

    private void ApplyTags()
    {
        IEnumerable<PoolTagRow> q = _allTags;
        if (_tagFilter.Trim() is { Length: > 0 } f)
            q = q.Where(t => t.Tag.Contains(f, StringComparison.OrdinalIgnoreCase) || t.Description.Contains(f, StringComparison.OrdinalIgnoreCase));
        Func<PoolTagRow, IComparable?> key = _tagSort switch
        {
            "Tag" => t => t.Tag,
            "Paged" => t => t.PagedMb,
            "NonPaged" => t => t.NonPagedMb,
            "Outstanding" => t => t.Outstanding,
            "Growth10m" => t => t.Growth10mMb,
            "Growth1h" => t => t.Growth1hMb,
            _ => t => t.TotalMb,
        };
        var selectedKey = _selectedTag?.Key;
        TagRows = Sort(q, key, _tagAsc);
        SelectedTag = selectedKey is { } k ? TagRows.FirstOrDefault(t => t.Key == k) : null;
    }

    private static List<ProcessRow> BuildProcessRows(ProcessReport? r)
    {
        if (r is not { Error: null }) return [];
        var byPid = r.Processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
        return r.Processes.Select(p =>
        {
            string parent = byPid.TryGetValue(p.ParentPid, out var pp) && pp.CreateTime <= p.CreateTime && pp.Pid != p.Pid
                ? $"{pp.Name} ({p.ParentPid})" : p.ParentPid == 0 ? "" : $"종료됨 ({p.ParentPid})";
            var ch = r.Change1h is { } c && c.TryGetValue(p.Key, out var v) ? v : ((long Private, long Handles)?)null;
            return new ProcessRow(p.Pid, p.Name, parent, p.PrivateBytes / MB, ch is { } x ? x.Private / MB : null,
                p.HandleCount, ch?.Handles, p.WorkingSet / MB, p.ThreadCount, p.PagedPoolQuota / 1024);
        }).ToList();
    }

    private void ApplyProcs()
    {
        IEnumerable<ProcessRow> q = _allProcs;
        if (_procFilter.Trim() is { Length: > 0 } f)
            q = q.Where(p => p.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || p.Parent.Contains(f, StringComparison.OrdinalIgnoreCase)
                             || p.Pid.ToString(C) == f);
        Func<ProcessRow, IComparable?> key = _procSort switch
        {
            "Name" => p => p.Name,
            "Pid" => p => p.Pid,
            "Parent" => p => p.Parent,
            "PrivateChange" => p => p.PrivateChange1hMb,
            "Handles" => p => p.Handles,
            "HandleChange" => p => p.HandleChange1h,
            "WorkingSet" => p => p.WorkingSetMb,
            "Threads" => p => p.Threads,
            _ => p => p.PrivateMb,
        };
        ProcessRows = Sort(q, key, _procAsc);
    }

    /// <summary>값이 없는(미측정) 행은 정렬 방향과 관계없이 뒤로 보낸다.</summary>
    private static List<T> Sort<T>(IEnumerable<T> rows, Func<T, IComparable?> key, bool asc)
    {
        var list = rows.ToList();
        list.Sort((a, b) =>
        {
            var x = key(a);
            var y = key(b);
            if (x is null || y is null) return x is null ? (y is null ? 0 : 1) : -1;
            int c = x is string sx && y is string sy ? string.Compare(sx, sy, StringComparison.OrdinalIgnoreCase) : x.CompareTo(y);
            return asc ? c : -c;
        });
        return list;
    }

    private void ApplyEvents(MemoryWatchState s)
    {
        var e = s.ProcessEvents;
        var snap = s.Processes;
        string snapshot = snap is { Error: null } ? $" · 스냅샷 비교(직전 수집 대비): 생성 {snap.StartedSinceLast} · 종료 {snap.ExitedSinceLast}" : "";
        EventStatus = e.Available
            ? $"이벤트 수집 중 (시작 {e.Since:HH:mm:ss}) · 최근 10분 생성 {e.Starts10m:N0} · 종료 {e.Stops10m:N0} · 최근 1시간 생성 {e.Starts1h:N0} · 종료 {e.Stops1h:N0}" +
              (e.Overflowed > 0 ? $" · 이름 {e.Overflowed:N0}건은 '(기타)'로 합침" : "")
            : $"이벤트 수집 불가: {e.Error}. 스냅샷 비교 값만 표시되며 짧게 실행된 프로세스는 빠질 수 있습니다." + snapshot;
        TopStarters = e.TopStarters1h.Select(x => new CountRow(x.Name, x.Starts)).ToList();
        RecentEvents = e.Recent.Select(r => new EventRow(r.Time.ToString("HH:mm:ss", C), r.Kind == ProcessEventKind.Start ? "생성" : "종료",
            r.Pid, r.ParentPid?.ToString(C) ?? "", r.Name)).ToList();
    }

    // ───────── 이상 이력 ─────────

    private AlertRow ToRow(MemoryAlert a) => new(a.Time, MemoryLevelText.Of(a.Level), MemoryBrushes.For(a.Level, _s.MemColorScheme),
        a.Label, a.ValueText, a.ThresholdText, a.Escalated ? "악화" : a.Repeated ? "재알림" : a.IsTrend ? "증가 추세" : "");

    private void AddAlert(AlertRow row)
    {
        if (_alerts.Any(x => x.Time == row.Time && x.Label == row.Label)) return;
        _alerts.Add(row);
    }

    private void PublishAlerts() => AlertRows = _alerts.OrderByDescending(a => a.Time).Take(500).ToList();

    /// <summary>최근 두 달의 알림 이력 파일과 현재 세션의 알림을 합쳐 보여준다(최대 500건).</summary>
    private void LoadAlertHistory()
    {
        try
        {
            if (Directory.Exists(_svc.LogDirectory))
                foreach (var file in Directory.GetFiles(_svc.LogDirectory, "alerts-*.jsonl").OrderDescending().Take(2))
                    foreach (var line in File.ReadLines(file).TakeLast(500))
                    {
                        try
                        {
                            var j = JsonDocument.Parse(line).RootElement;
                            var level = Enum.TryParse<MemoryLevel>(j.GetProperty("level").GetString(), out var l) ? l : MemoryLevel.Unknown;
                            bool flag(string n) => j.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
                            AddAlert(new AlertRow(j.GetProperty("time").GetDateTimeOffset(), MemoryLevelText.Of(level), MemoryBrushes.For(level, _s.MemColorScheme),
                                j.GetProperty("Label").GetString() ?? "", j.GetProperty("value").GetString() ?? "", j.GetProperty("threshold").GetString() ?? "",
                                flag("escalated") ? "악화" : flag("repeated") ? "재알림" : flag("trend") ? "증가 추세" : ""));
                        }
                        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { /* 손상된 줄은 건너뜀 */ }
                    }
        }
        catch (Exception ex) { Logger.Error("알림 이력 읽기 실패", ex); }
        foreach (var a in _svc.RecentAlerts) AddAlert(ToRow(a));
        PublishAlerts();
    }

    // ───────── 동작 ─────────

    public async Task SearchDriversAsync()
    {
        if (_selectedTag is not { } tag || !CanSearch) return;
        _searchCts?.Dispose();
        var cts = _searchCts = new CancellationTokenSource();
        IsSearching = true;
        SearchResult = $"'{tag.Tag}' 문자열을 드라이버 파일에서 찾는 중...";
        try
        {
            var extra = _svc.Drivers.Select(d => d.Path).OfType<string>().ToList();
            var r = await Task.Run(() => DriverTagSearch.Search(tag.Tag, extra, cts.Token), cts.Token);
            if (_disposed) return;
            var sb = new StringBuilder();
            if (r.Error is not null) sb.AppendLine(r.Error);
            else
            {
                sb.AppendLine($"'{r.Tag}' 후보 {r.Candidates.Count}개 (검색 {r.Scanned}개, 읽지 못함 {r.Skipped}개, 긴 문자열의 일부로만 나온 파일 {r.SubstringOnly}개는 제외)");
                foreach (var c in r.Candidates) sb.AppendLine($"  {Path.GetFileName(c.Path)} · {c.Company ?? "회사 정보 없음"} · {c.Version ?? ""} · {c.Path}");
            }
            sb.Append(DriverTagSearch.Disclaimer);
            SearchResult = sb.ToString();
        }
        catch (OperationCanceledException) { if (!_disposed) SearchResult = "검색을 취소했습니다."; }
        catch (Exception ex) { if (!_disposed) SearchResult = "검색 실패: " + ex.Message; }
        finally { if (!_disposed) IsSearching = false; }
    }

    public void CancelSearch() => _searchCts?.Cancel();

    public void SaveDiagnosticNow()
    {
        DiagnosticStatus = "진단 파일 저장 중...";
        _svc.RequestDiagnostic();
    }

    public void ExportDiagnostic(string path)
    {
        DiagnosticStatus = "진단 파일 내보내는 중...";
        _svc.RequestDiagnostic(path);
    }

    /// <summary>현재 필터·정렬이 적용된 표를 CSV로 저장한다(Excel 호환 UTF-8 BOM).</summary>
    public void ExportCsv(int tabIndex, string path)
    {
        var sb = new StringBuilder();
        static string Q(string v) => v.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        static string N(double? v) => v is { } x ? x.ToString("0.0", C) : "";
        switch (tabIndex)
        {
            case 1:
                sb.AppendLine("tag,paged_mb,nonpaged_mb,total_mb,outstanding,growth10m_mb,growth1h_mb,description");
                foreach (var t in TagRows)
                    sb.AppendLine(string.Join(",", Q(t.Tag), N(t.PagedMb), N(t.NonPagedMb), N(t.TotalMb), t.Outstanding.ToString(C),
                        N(t.Growth10mMb), N(t.Growth1hMb), Q(t.Description)));
                break;
            case 2:
                sb.AppendLine("pid,name,parent,private_mb,private_change1h_mb,handles,handle_change1h,working_set_mb,threads,paged_pool_quota_kb");
                foreach (var p in ProcessRows)
                    sb.AppendLine(string.Join(",", p.Pid.ToString(C), Q(p.Name), Q(p.Parent), N(p.PrivateMb), N(p.PrivateChange1hMb),
                        p.Handles.ToString(C), p.HandleChange1h?.ToString(C) ?? "", N(p.WorkingSetMb), p.Threads.ToString(C), p.PagedQuotaKb.ToString(C)));
                break;
            case 3:
                sb.AppendLine("time,kind,pid,parent_pid,name");
                foreach (var e in RecentEvents) sb.AppendLine(string.Join(",", e.Time, e.Kind, e.Pid.ToString(C), e.ParentPid, Q(e.Name)));
                break;
            case 4:
                sb.AppendLine("time,level,label,value,threshold,flags");
                foreach (var a in AlertRows) sb.AppendLine(string.Join(",", a.TimeText, a.Level, Q(a.Label), Q(a.Value), Q(a.Threshold), a.Flags));
                break;
            default:
                sb.AppendLine("time,paged_mb,nonpaged_mb");
                foreach (var p in ChartPoints ?? []) sb.AppendLine($"{p.Time:yyyy-MM-ddTHH:mm:sszzz},{N(p.Paged)},{N(p.NonPaged)}");
                break;
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        DiagnosticStatus = $"CSV 저장됨: {path}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _svc.Updated -= OnUpdated;
        _svc.AlertRaised -= OnAlerts;
        _svc.DiagnosticWritten -= OnDiagnostic;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
    }
}
