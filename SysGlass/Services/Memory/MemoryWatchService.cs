using Microsoft.Win32;
using SysGlass.Models;
using SysGlass.Settings;
using SysGlass.Utilities;

namespace SysGlass.Services.Memory;

/// <summary>
/// 메모리 감시 루프. 기존 센서 수집 루프와 분리된 백그라운드 작업 하나에서 항목별 주기에 맞춰 수집·판정·기록한다.
/// 주기는 매 반복마다 설정에서 읽으며, 설정 변경 시 Wake()로 대기를 끊는다. 루프는 중복 실행되지 않는다.
/// 이벤트는 백그라운드 스레드에서 발생하므로 화면 갱신은 구독자가 디스패처로 넘겨야 한다.
/// </summary>
public sealed class MemoryWatchService : IDisposable
{
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan FollowUpDelay = TimeSpan.FromMinutes(10);
    private const int MaxRecentAlerts = 200;

    private readonly AppSettings _s;
    private readonly KernelMemoryCollector _kernel = new();
    private readonly PoolTagCollector _poolTags = new();
    private readonly ProcessMemoryCollector _processes = new();
    private readonly MemoryHistory _history = new();
    private readonly object _historyLock = new();
    private readonly ProcessEventStats _events = new();
    private readonly ProcessEventCollector _eventCollector;
    private bool _eventsTried;
    private volatile string? _exportPath;
    private readonly PoolTagHistory _tagHistory = new();
    private readonly MemoryAnomalyDetector _detector = new();
    private readonly DiagnosticWriter _writer;
    private readonly object _alertLock = new();
    private readonly LinkedList<MemoryAlert> _recentAlerts = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _wakeLock = new();
    private CancellationTokenSource? _wake;
    private bool _wakeRequested;
    private volatile bool _rescheduled, _resumed, _diagnosticRequested;
    private Task? _loop;
    private int _disposed;

    // 항목별 최근 값 (루프 스레드 전용)
    private KernelMemorySample? _lastKernel;
    private string? _kernelError, _residentError;
    private ulong? _resident;
    private DateTimeOffset? _residentTime;
    private TimeSpan? _uptime;
    private DateTimeOffset? _bootTime;
    private PoolTagReport? _poolReport;
    private ProcessReport? _processReport;
    private DateTime _nextKernel, _nextResident, _nextUptime, _nextPoolTag, _nextProcess, _nextSummary, _nextCleanup;
    private SystemInfo? _system;
    private List<DriverInfo> _drivers = [];
    private List<ServiceInfo> _services = [];
    private string? _driverError, _serviceError;
    private bool _inventoryLoaded;
    private IReadOnlyList<MemoryAlert>? _pendingAlerts;
    private (string Path, MemoryWatchState At, DateTime DueUtc)? _followUp;

    public MemoryWatchService(AppSettings settings, string? logDirectory = null)
    {
        _s = settings;
        _writer = new DiagnosticWriter(logDirectory ?? DiagnosticWriter.DefaultDirectory);
        _eventCollector = new ProcessEventCollector(_events);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>진단 기록 폴더.</summary>
    public string LogDirectory => _writer.Directory;

    /// <summary>새 상태가 준비되면 발생한다(백그라운드 스레드).</summary>
    public event Action<MemoryWatchState>? Updated;

    /// <summary>알림 조건을 만족한 항목이 생기면 발생한다(백그라운드 스레드). 표시 여부는 구독자가 설정에 따라 정한다.</summary>
    public event Action<IReadOnlyList<MemoryAlert>>? AlertRaised;

    /// <summary>상세 진단 파일을 저장하면 발생한다(경로, 실패 시 null과 사유).</summary>
    public event Action<string?, string?>? DiagnosticWritten;

    /// <summary>가장 최근에 발행한 상태.</summary>
    public MemoryWatchState Current { get; private set; } = new();

    /// <summary>최근 알림 이력(최대 200건, 최신이 앞).</summary>
    public IReadOnlyList<MemoryAlert> RecentAlerts
    {
        get { lock (_alertLock) return _recentAlerts.ToList(); }
    }

    public void Start()
    {
        if (_loop is not null || Volatile.Read(ref _disposed) != 0) return;
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(token), token);
    }

    /// <summary>설정이 바뀌었을 때 대기를 끊고 즉시 다시 계산하게 한다.</summary>
    public void Wake()
    {
        lock (_wakeLock)
        {
            _wakeRequested = true;
            _rescheduled = true; // 주기가 바뀌었을 수 있으므로 다음 반복에서 모든 항목을 다시 수집한다.
            try { _wake?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>실행 중인 드라이버 목록(시작 시·이상 감지 시 갱신).</summary>
    public IReadOnlyList<DriverInfo> Drivers => Volatile.Read(ref _drivers);

    /// <summary>풀 사용량 이력 복사본(시간순). 그래프용.</summary>
    public List<MemoryHistory.Point> GetHistory()
    {
        lock (_historyLock)
            return _history.Coarse.Concat(_history.Fine).OrderBy(p => p.Active100ns).DistinctBy(p => p.Active100ns).ToList();
    }

    /// <summary>
    /// 사용자 요청으로 지금 상세 진단을 저장한다. exportPath가 있으면 그 경로에 쓴다(보존 기간 정리 대상 아님).
    /// 결과는 DiagnosticWritten으로 알린다.
    /// </summary>
    public void RequestDiagnostic(string? exportPath = null)
    {
        _exportPath = exportPath;
        _diagnosticRequested = true;
        lock (_wakeLock) { try { _wake?.Cancel(); } catch (ObjectDisposedException) { } }
    }

    /// <summary>절전 복귀 시 다음 판정에서 지속 시간을 다시 세도록 표시하고 즉시 수집한다.</summary>
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        _resumed = true;
        Wake();
    }

    private async Task RunAsync(CancellationToken token)
    {
        bool wasEnabled = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_s.MemoryWatchEnabled)
                {
                    wasEnabled = true;
                    if (!_eventsTried) { _eventsTried = true; _eventCollector.Start(); }
                    if (Tick()) Publish(Assess(BuildState()));
                    AfterPublish(token);
                }
                else
                {
                    if (_diagnosticRequested)
                    {
                        _diagnosticRequested = false;
                        DiagnosticWritten?.Invoke(null, "메모리 감시가 꺼져 있습니다.");
                    }
                    if (wasEnabled || Current.Enabled)
                    {
                        // 비활성화하면 이력을 비워 다시 켰을 때 끊긴 구간이 증가량에 섞이지 않게 한다.
                        wasEnabled = false;
                        _eventCollector.Stop();
                        _eventsTried = false;
                        ResetValues();
                        Publish(new MemoryWatchState { Enabled = false });
                    }
                }
            }
            catch (Exception ex) { Logger.Error("메모리 감시 수집 실패", ex); }

            if (!await DelayAsync(token)) break;
        }
    }

    /// <summary>주기가 된 항목만 수집한다. 바뀐 항목이 있으면 true.</summary>
    private bool Tick()
    {
        var now = DateTime.UtcNow;
        bool changed = false;
        if (_rescheduled)
        {
            _rescheduled = false;
            _nextKernel = _nextResident = _nextUptime = _nextPoolTag = _nextProcess = DateTime.MinValue;
        }
        if (now >= _nextKernel)
        {
            var sample = _kernel.Read(out _kernelError);
            _lastKernel = sample;
            bool reset;
            lock (_historyLock) reset = _history.Add(sample);
            if (reset) Logger.Error("메모리 감시: 활성 시간이 초기화되어 추세 이력을 다시 시작함");
            _nextKernel = now.AddSeconds(_s.MemKernelIntervalSec);
            changed = true;
        }
        if (now >= _nextResident)
        {
            _resident = _kernel.ReadPagedResident(out _residentError);
            _residentTime = _resident is null ? _residentTime : DateTimeOffset.Now;
            _nextResident = now.AddSeconds(_s.MemResidentIntervalSec);
            changed = true;
        }
        if (now >= _nextUptime)
        {
            // 작업 관리자의 '작동 시간'과 같은 기준(절전 시간 포함). 빠른 시작에서는 종료 후에도 이어진다.
            _uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            _bootTime = DateTimeOffset.Now - _uptime;
            _nextUptime = now.AddSeconds(_s.MemUptimeIntervalSec);
            changed = true;
        }
        if (now >= _nextPoolTag)
        {
            _poolReport = ReadPoolTags();
            _nextPoolTag = now.AddSeconds(_s.MemPoolTagIntervalSec);
            changed = true;
        }
        if (now >= _nextProcess)
        {
            try { _processReport = _processes.Read(ActiveTime()); }
            catch (Exception ex) { _processReport = new ProcessReport(DateTimeOffset.Now, [], "프로세스 수집 실패: " + ex.Message); }
            _nextProcess = now.AddSeconds(_s.MemProcessIntervalSec);
            changed = true;
        }
        return changed;
    }

    private PoolTagReport ReadPoolTags()
    {
        try
        {
            var (tags, error) = _poolTags.Read();
            if (error is not null) return new PoolTagReport(DateTimeOffset.Now, [], error);
            _tagHistory.Add(ActiveTime(), tags);
            return new PoolTagReport(DateTimeOffset.Now, tags, null)
            {
                Growth10m = _tagHistory.Growth(TimeSpan.FromMinutes(10)),
                Growth1h = _tagHistory.Growth(TimeSpan.FromHours(1)),
            };
        }
        catch (Exception ex)
        {
            Logger.Error("Pool Tag 수집 실패", ex);
            return new PoolTagReport(DateTimeOffset.Now, [], "Pool Tag 수집 실패: " + ex.Message);
        }
    }

    private static long ActiveTime() =>
        NativeMethods.QueryUnbiasedInterruptTime(out ulong t) ? (long)t : Environment.TickCount64 * 10_000;

    private MemoryWatchState BuildState()
    {
        lock (_historyLock) return BuildStateLocked();
    }

    private MemoryWatchState BuildStateLocked() => new()
    {
        Enabled = true,
        Kernel = _lastKernel,
        KernelError = _kernelError,
        PagedResident = _resident,
        PagedResidentTime = _residentTime,
        PagedResidentError = _residentError,
        Uptime = _uptime,
        BootTime = _bootTime,
        Growth10m = _history.Growth(TimeSpan.FromMinutes(10)),
        Growth1h = _history.Growth(TimeSpan.FromHours(1)),
        Growth24h = _history.Growth(TimeSpan.FromHours(24)),
        Observed = _history.Observed,
        PoolTags = _poolReport,
        Processes = _processReport,
        ProcessEvents = _events.Snapshot(DateTimeOffset.Now),
    };

    /// <summary>이상 상태를 판정하고, 알림 조건을 만족한 항목이 있으면 알린다.</summary>
    private MemoryWatchState Assess(MemoryWatchState state)
    {
        _detector.Sustain = TimeSpan.FromMinutes(_s.MemAlertSustainMin);
        _detector.Repeat = TimeSpan.FromMinutes(_s.MemAlertRepeatMin);
        _detector.MaxGap = TimeSpan.FromSeconds(Math.Max(90, _s.MemKernelIntervalSec * 3));
        if (_resumed) { _resumed = false; _detector.MarkDiscontinuity(); }

        var (assessment, alerts) = _detector.Evaluate(DateTimeOffset.Now, MemoryReadings.Build(state, _s),
            available: state.Kernel?.PagedPool is not null);
        if (alerts.Count > 0)
        {
            lock (_alertLock)
                foreach (var a in alerts)
                {
                    _recentAlerts.AddFirst(a);
                    if (_recentAlerts.Count > MaxRecentAlerts) _recentAlerts.RemoveLast();
                }
            _pendingAlerts = alerts;
            try { AlertRaised?.Invoke(alerts); }
            catch (Exception ex) { Logger.Error("메모리 알림 전달 실패", ex); }
        }
        return state with { Assessment = assessment };
    }

    /// <summary>발행 뒤에 하는 느린 작업: 시스템 정보 조회, 기록, 정리. 알림 표시를 늦추지 않도록 판정 뒤에 처리한다.</summary>
    private void AfterPublish(CancellationToken token)
    {
        var now = DateTime.UtcNow;
        var state = Current;
        if (!_inventoryLoaded)
        {
            _inventoryLoaded = true; // 프로그램 시작 시 1회
            _system = SystemInfoCollector.ReadSystemInfo();
            RefreshInventory();
        }
        if (token.IsCancellationRequested) return;

        var alerts = _pendingAlerts;
        _pendingAlerts = null;
        bool manual = _diagnosticRequested;
        _diagnosticRequested = false;
        string? exportPath = manual ? _exportPath : null;
        _exportPath = null;

        if (!_s.MemLogEnabled)
        {
            if (manual) DiagnosticWritten?.Invoke(null, "설정에서 메모리 감시 기록이 꺼져 있습니다.");
            _followUp = null;
            return;
        }

        if (alerts is not null) TryLog(() => _writer.AppendAlerts(alerts), "알림 이력 기록 실패");
        if (alerts is not null || manual)
        {
            if (alerts is not null) RefreshInventory(); // 이상 감지 시점의 드라이버·서비스
            try
            {
                var context = new DiagnosticWriter.Context(
                    alerts is null ? "manual" : "alert", state, alerts ?? [], _system ?? SystemInfoCollector.ReadSystemInfo(),
                    GetHistory(), _drivers, _driverError, _services, _serviceError, state.ProcessEvents);
                string path = _writer.WriteDiagnostic(context, DateTimeOffset.Now);
                if (alerts is not null) _followUp = (path, state, now + FollowUpDelay);
                DiagnosticWritten?.Invoke(path, null);
                if (exportPath is not null)
                {
                    _writer.WriteDiagnostic(context with { Reason = "export" }, DateTimeOffset.Now, exportPath);
                    DiagnosticWritten?.Invoke(exportPath, null);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("상세 진단 저장 실패", ex);
                DiagnosticWritten?.Invoke(null, "상세 진단 저장 실패: " + ex.Message);
            }
        }

        if (_followUp is { } f && now >= f.DueUtc)
        {
            _followUp = null;
            TryLog(() => _writer.WriteFollowUp(f.Path, f.At, state), "발생 후 진단 기록 실패");
        }
        if (now >= _nextSummary && state.Kernel is not null)
        {
            _nextSummary = now + SummaryInterval;
            TryLog(() => _writer.AppendSummary(state), "메모리 요약 기록 실패");
        }
        if (now >= _nextCleanup)
        {
            _nextCleanup = now + CleanupInterval;
            TryLog(() => _writer.Cleanup(_s.MemRetentionDays, _s.MemLogMaxMb * 1024L * 1024, now), "진단 기록 정리 실패");
        }
    }

    private void RefreshInventory()
    {
        _drivers = SystemInfoCollector.ReadDrivers(out _driverError);
        _services = SystemInfoCollector.ReadServices(out _serviceError);
    }

    private static void TryLog(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) { Logger.Error(message, ex); }
    }

    private void ResetValues()
    {
        lock (_historyLock) _history.Clear();
        _events.Reset();
        _tagHistory.Clear();
        _detector.Reset();
        _lastKernel = null;
        _kernelError = _residentError = null;
        _resident = null;
        _residentTime = null;
        _uptime = null;
        _bootTime = null;
        _poolReport = null;
        _processReport = null;
        _pendingAlerts = null;
        _followUp = null;
        _nextKernel = _nextResident = _nextUptime = _nextPoolTag = _nextProcess = DateTime.MinValue;
    }

    private void Publish(MemoryWatchState state)
    {
        Current = state;
        try { Updated?.Invoke(state); }
        catch (Exception ex) { Logger.Error("메모리 감시 상태 전달 실패", ex); }
    }

    /// <summary>가장 가까운 다음 수집 시각까지 기다린다. 취소되면 false.</summary>
    private async Task<bool> DelayAsync(CancellationToken token)
    {
        CancellationTokenSource? wake = null;
        TimeSpan delay;
        lock (_wakeLock)
        {
            if (_wakeRequested || _diagnosticRequested) { _wakeRequested = false; delay = TimeSpan.FromMilliseconds(200); }
            else
            {
                var next = new[] { _nextKernel, _nextResident, _nextUptime, _nextPoolTag, _nextProcess }.Min();
                delay = _s.MemoryWatchEnabled ? next - DateTime.UtcNow : TimeSpan.FromSeconds(30);
                delay = TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 200, 60_000));
                _wake?.Dispose();
                wake = _wake = CancellationTokenSource.CreateLinkedTokenSource(token);
            }
        }
        try { await Task.Delay(delay, wake?.Token ?? token).ConfigureAwait(false); }
        catch (OperationCanceledException) { if (token.IsCancellationRequested) return false; }
        return !token.IsCancellationRequested;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Updated = null;
        AlertRaised = null;
        DiagnosticWritten = null;
        _cts.Cancel();
        _eventCollector.Dispose();
        bool stopped;
        try { stopped = _loop?.Wait(TimeSpan.FromSeconds(5)) ?? true; }
        catch { stopped = true; } // 취소로 끝난 작업
        // 루프가 아직 네이티브 버퍼를 쓰는 중이면 해제하지 않는다(프로세스 종료 시 OS가 회수).
        if (stopped)
        {
            _kernel.Dispose();
            _poolTags.Dispose();
            _processes.Dispose();
        }
        lock (_wakeLock) { _wake?.Dispose(); _wake = null; }
        if (stopped) _cts.Dispose();
    }
}
