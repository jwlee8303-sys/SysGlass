using System.Management;
using SysGlass.Models;
using SysGlass.Utilities;

namespace SysGlass.Services.Memory;

/// <summary>
/// WMI 이벤트(Win32_ProcessStartTrace·Win32_ProcessStopTrace)로 프로세스 생성·종료를 받아 집계한다.
/// 짧게 실행되고 끝나는 프로세스도 잡을 수 있는 이벤트 방식이며, 관리자 권한이 필요하다.
/// 이 이벤트 클래스에는 명령줄 속성이 없으며, 이름·PID·부모 PID만 읽는다.
/// (속성을 골라 선택하는 쿼리는 WMI가 '잘못된 매개 변수'로 거부하므로 SELECT *를 쓴다.) Stop/Dispose 시 구독을 해제한다.
/// </summary>
public sealed class ProcessEventCollector : IDisposable
{
    private readonly ProcessEventStats _stats;
    private readonly object _lock = new();
    private ManagementEventWatcher? _start, _stop;
    private int _sinceCollect;
    private long _lastCollectTicks;
    private int _collectionQueued;
    private int _lastGen1Count = GC.CollectionCount(1);
    private int _eventsWithoutCollection;

    /// <summary>
    /// WMI 이벤트 객체는 Dispose해도 네이티브(COM) 메모리가 종료자(finalizer)가 돌 때까지 남는다(실측: 이벤트당 약 400KB).
    /// 프로세스가 대량으로 생성될 때 메모리가 수백 MB까지 불어나지 않도록, 이벤트가 이만큼 쌓이면 작은 GC를 요청한다.
    /// </summary>
    internal const int CollectEvery = 64;
    internal static readonly TimeSpan CollectMinInterval = TimeSpan.FromSeconds(1);

    public ProcessEventCollector(ProcessEventStats stats) => _stats = stats;

    public bool IsRunning { get { lock (_lock) return _start is not null; } }

    /// <summary>구독을 시작한다. 실패하면 집계에 사유를 남기고 false.</summary>
    public bool Start()
    {
        lock (_lock)
        {
            if (_start is not null) return true;
            try
            {
                _start = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
                _start.EventArrived += OnStart;
                _stop = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace"));
                _stop.EventArrived += OnStop;
                _start.Start();
                _stop.Start();
                _stats.MarkStarted(DateTimeOffset.Now);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("프로세스 이벤트 구독 실패", ex);
                StopLocked();
                _stats.MarkUnavailable(ex is ManagementException { ErrorCode: ManagementStatus.AccessDenied } or UnauthorizedAccessException
                    ? "권한 부족 (관리자 권한 필요)" : "이벤트 구독 실패: " + ex.Message);
                return false;
            }
        }
    }

    public void Stop()
    {
        lock (_lock) StopLocked();
    }

    private void StopLocked()
    {
        foreach (var w in new[] { _start, _stop })
        {
            if (w is null) continue;
            try { w.Stop(); } catch { /* 이미 중지됨 */ }
            w.EventArrived -= OnStart;
            w.EventArrived -= OnStop;
            w.Dispose();
        }
        _start = _stop = null;
    }

    private void OnStart(object sender, EventArrivedEventArgs e) => Record(e, ProcessEventKind.Start);
    private void OnStop(object sender, EventArrivedEventArgs e) => Record(e, ProcessEventKind.Stop);

    private void Record(EventArrivedEventArgs e, ProcessEventKind kind)
    {
        try
        {
            using var ev = e.NewEvent;
            int pid = ev["ProcessID"] is uint p ? (int)p : -1;
            int? parent = kind == ProcessEventKind.Start && ev["ParentProcessID"] is uint pp ? (int)pp : null;
            _stats.Record(new ProcessEventRecord(DateTimeOffset.Now, kind, pid, parent, ev["ProcessName"] as string ?? ""));
        }
        catch (Exception ex) { Logger.Error("프로세스 이벤트 처리 실패", ex); }
        ReleaseNativeIfNeeded();
    }

    /// <summary>쌓인 이벤트 객체의 종료자를 실행시켜 네이티브 메모리를 돌려준다(최대 초당 1회, 1세대까지만 수집).</summary>
    private void ReleaseNativeIfNeeded()
    {
        if (Interlocked.Increment(ref _sinceCollect) < CollectEvery) return;
        long now = Environment.TickCount64, last = Interlocked.Read(ref _lastCollectTicks);
        if (now - last < (long)CollectMinInterval.TotalMilliseconds) return;
        if (Interlocked.CompareExchange(ref _lastCollectTicks, now, last) != last) return;
        if (Interlocked.Exchange(ref _collectionQueued, 1) != 0) return;
        int events = Interlocked.Exchange(ref _sinceCollect, 0);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                // CLR이 이미 수집한 구간에는 추가 GC를 요청하지 않는다.
                int current = GC.CollectionCount(1);
                if (current != _lastGen1Count) _eventsWithoutCollection = 0;
                else
                {
                    _eventsWithoutCollection += events;
                    // 최적화 요청을 계속 건너뛰면 종료자를 기다리는 WMI 객체가 누적된다.
                    var mode = _eventsWithoutCollection >= 4 * CollectEvery ? GCCollectionMode.Forced : GCCollectionMode.Optimized;
                    GC.Collect(1, mode, blocking: false);
                    if (GC.CollectionCount(1) != current) _eventsWithoutCollection = 0;
                }
                _lastGen1Count = GC.CollectionCount(1);
            }
            finally { Volatile.Write(ref _collectionQueued, 0); }
        });
    }

    public void Dispose() => Stop();
}
