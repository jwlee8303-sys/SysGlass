using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml.Linq;
using SysGlass.Utilities;

namespace SysGlass.Services.Memory;

/// <summary>
/// 사용자 동의 후에만 Windows 기본 도구 wpr.exe를 한 번 실행해 Pool 할당·반환 이벤트(호출 스택 포함)를 기록한다.
/// - 전용 인스턴스 이름(SysGlassPool)을 써서 다른 WPR·ETW 세션을 건드리지 않고, 정리할 때도 이 인스턴스만 취소한다.
/// - 메모리(순환 버퍼) 모드라 버퍼 크기 이상으로 커지지 않으며, 지정 시간이 지나면 자동으로 멈추고 저장한다.
/// - 앱 종료 시 진행 중인 기록은 취소한다. 주기적으로 외부 프로세스를 실행하지 않는다.
/// </summary>
public sealed class PrecisionTraceService : IDisposable
{
    public const string InstanceName = "SysGlassPool";
    public const string ProfileName = "SysGlassPool";
    public const int MaxTags = 8, KeepTraces = 3, RetentionDays = 30;
    public static readonly (int Min, int Max, int Default) MinutesRange = (1, 30, 5);
    public static readonly (int Min, int Max, int Default) BufferMbRange = (64, 1024, 256);
    /// <summary>wpr가 '실행 중인 기록 없음'을 알리는 종료 코드.</summary>
    private const int NoTraceRunning = unchecked((int)0xC5583000);

    public enum TraceState { Idle, Starting, Recording, Stopping, CancelFailed }
    public sealed record Options(IReadOnlyList<string> Tags, int Minutes, int BufferMb);

    private readonly string _dir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _timer;
    private volatile bool _disposed;

    private readonly Func<TimeSpan, string[], Task<(int Code, string Output)>> _run;

    public PrecisionTraceService(string directory) : this(directory, RunWpr) { }

    internal PrecisionTraceService(string directory, Func<TimeSpan, string[], Task<(int Code, string Output)>> run)
    {
        _dir = directory;
        _run = run;
    }

    public string Directory => _dir;
    public static string WprPath => Path.Combine(Environment.SystemDirectory, "wpr.exe");
    public static bool IsAvailable => File.Exists(WprPath);

    public TraceState State { get; private set; }
    public DateTimeOffset? EndsAt { get; private set; }
    public Options? Current { get; private set; }
    public string? LastPath { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>상태가 바뀔 때. 백그라운드 스레드에서 올 수 있다.</summary>
    public event Action? Changed;
    /// <summary>기록이 끝났을 때(저장 경로 또는 오류). 취소는 둘 다 null.</summary>
    public event Action<string?, string?>? Finished;

    // ───────── 입력 검증·프로필 ─────────

    /// <summary>
    /// 쉼표·공백으로 구분한 태그를 검사한다. 4글자보다 짧으면 뒤를 공백으로 채운다(예: "Irp" → "Irp ").
    /// 비어 있으면 모든 태그를 기록한다(파일이 빨리 찹니다). 오류가 있으면 메시지를 돌려준다.
    /// </summary>
    public static string? ParseTags(string? text, out List<string> tags)
    {
        tags = [];
        foreach (var raw in (text ?? "").Split([',', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            if (t.Length > 4) return $"'{t}': 태그는 4글자 이하여야 합니다.";
            if (!t.All(c => c is > ' ' and < (char)127 && c != '"' && c != '<' && c != '>' && c != '&'))
                return $"'{t}': 영문·숫자 등 출력 가능한 ASCII 문자만 쓸 수 있습니다.";
            t = t.PadRight(4);
            if (!tags.Contains(t, StringComparer.Ordinal)) tags.Add(t);
        }
        return tags.Count > MaxTags ? $"태그는 최대 {MaxTags}개까지 지정할 수 있습니다." : null;
    }

    /// <summary>
    /// WPR 사용자 프로필을 만든다. 커널(System) 공급자에서 Pool·프로세스·이미지 로드 이벤트와 Pool 할당/반환 호출 스택만 켠다.
    /// 태그를 지정하면 해당 태그만 기록한다.
    /// </summary>
    public static string BuildProfile(Options o)
    {
        const string collector = "SysGlass_System_Collector", provider = "SysGlass_System_Provider";
        var sysProvider = new XElement("SystemProvider", new XAttribute("Id", provider),
            new XElement("Keywords", new[] { "Loader", "ProcessThread", "Pool" }.Select(k => new XElement("Keyword", new XAttribute("Value", k)))),
            new XElement("Stacks", new[] { "PoolAllocation", "PoolAllocationSession", "PoolFree", "PoolFreeSession" }
                .Select(s => new XElement("Stack", new XAttribute("Value", s)))));
        if (o.Tags.Count > 0)
            sysProvider.Add(new XElement("PoolTags", o.Tags.Select(t => new XElement("PoolTag", new XAttribute("Value", t)))));

        var doc = new XDocument(
            new XElement("WindowsPerformanceRecorder", new XAttribute("Version", "1.0"),
                new XAttribute("Author", "SysGlass"), new XAttribute("Comments", "SysGlass Pool 정밀 추적 (사용자 동의 후 1회 실행)"),
                new XElement("Profiles",
                    new XElement("SystemCollector", new XAttribute("Id", collector), new XAttribute("Name", "SysGlass Pool Collector"),
                        new XElement("BufferSize", new XAttribute("Value", 1024)),
                        new XElement("Buffers", new XAttribute("Value", o.BufferMb))),
                    sysProvider,
                    new XElement("Profile", new XAttribute("Id", ProfileName + ".Verbose.Memory"), new XAttribute("Name", ProfileName),
                        new XAttribute("Description", "SysGlass pool allocation trace"), new XAttribute("LoggingMode", "Memory"),
                        new XAttribute("DetailLevel", "Verbose"),
                        new XElement("Collectors",
                            new XElement("SystemCollectorId", new XAttribute("Value", collector),
                                new XElement("SystemProviderId", new XAttribute("Value", provider))))))));
        return doc.Declaration + doc.ToString();
    }

    // ───────── 시작·중지 ─────────

    /// <summary>기록을 시작한다. 실패하면 사유를 돌려준다(null이면 성공).</summary>
    public async Task<string?> StartAsync(Options o)
    {
        if (_disposed) return "종료 중입니다.";
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return "다른 정밀 추적 작업이 진행 중입니다.";
        try
        {
            if (_disposed) return "종료 중입니다.";
            if (State != TraceState.Idle) return "이미 정밀 추적이 진행 중입니다.";

            if (o.Minutes < MinutesRange.Min || o.Minutes > MinutesRange.Max || o.BufferMb < BufferMbRange.Min || o.BufferMb > BufferMbRange.Max)
                return "기록 시간 또는 버퍼 크기가 허용 범위를 벗어났습니다.";
            System.IO.Directory.CreateDirectory(_dir);
            long needMb = o.BufferMb * 2L + 1024;
            long freeMb = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_dir))!).AvailableFreeSpace / (1024 * 1024);
            if (freeMb < needMb) return $"디스크 여유 공간이 부족합니다 (필요 약 {needMb:N0}MB, 여유 {freeMb:N0}MB).";

            SetState(TraceState.Starting, o);
            LastError = null;
            Cleanup(DateTime.UtcNow);
            string profile = Path.Combine(_dir, "SysGlassPool.wprp");
            await File.WriteAllTextAsync(profile, BuildProfile(o), new UTF8Encoding(false)).ConfigureAwait(false);

            // 이전 실행이 비정상 종료돼 남은 '자기' 인스턴스만 정리한다(다른 세션은 건드리지 않음).
            if (await CancelSessionAsync().ConfigureAwait(false) is { } cancelError)
            {
                LastError = cancelError;
                SetState(TraceState.CancelFailed, o);
                return cancelError;
            }
            var (code, output) = await _run(TimeSpan.FromSeconds(60), ["-start", profile + "!" + ProfileName, "-instancename", InstanceName]).ConfigureAwait(false);
            if (code != 0)
            {
                var cleanupError = await CancelSessionAsync().ConfigureAwait(false);
                LastError = cleanupError ?? (code == unchecked((int)0x80070005)
                    ? "정밀 추적에는 관리자 권한이 필요합니다 (코드 0x80070005)."
                    : $"정밀 추적을 시작하지 못했습니다 (코드 0x{code:X8}). {Summarize(output)}");
                SetState(cleanupError is null ? TraceState.Idle : TraceState.CancelFailed, cleanupError is null ? null : o);
                return LastError;
            }

            var cts = _timer = new CancellationTokenSource();
            EndsAt = DateTimeOffset.Now.AddMinutes(o.Minutes);
            SetState(TraceState.Recording, o);
            _ = Task.Delay(TimeSpan.FromMinutes(o.Minutes), cts.Token)
                .ContinueWith(t => { if (!t.IsCanceled) _ = StopAsync(save: true, cts); }, TaskScheduler.Default);
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error("정밀 추적 시작 실패", ex);
            if (State == TraceState.Starting && await CancelSessionAsync().ConfigureAwait(false) is { } cleanupError)
            {
                LastError = cleanupError;
                SetState(TraceState.CancelFailed, o);
                return cleanupError;
            }
            LastError = "정밀 추적을 시작하지 못했습니다: " + ex.Message;
            SetState(TraceState.Idle, null);
            return LastError;
        }
        finally { _gate.Release(); }
    }

    /// <summary>기록을 멈춘다. save면 .etl로 저장하고, 아니면 버린다.</summary>
    public Task StopAsync(bool save) => StopAsync(save, null);

    private async Task StopAsync(bool save, CancellationTokenSource? expectedTimer)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        string? path = null, error = null, cancelError = null;
        try
        {
            if (expectedTimer is not null && expectedTimer != _timer) return;
            if (State != TraceState.Recording && (save || State != TraceState.CancelFailed)) return;
            _timer?.Cancel();
            SetState(TraceState.Stopping, Current);
            if (save)
            {
                path = Path.Combine(_dir, $"sysglass-pool-{DateTime.Now:yyyyMMdd-HHmmss}.etl");
                // 병합에 시간이 걸릴 수 있다. -skipPdbGen으로 추가 심볼 파일 생성을 막는다.
                var (code, output) = await _run(TimeSpan.FromMinutes(10), ["-stop", path, "SysGlass pool trace", "-skipPdbGen", "-instancename", InstanceName]).ConfigureAwait(false);
                if (code != 0 || !File.Exists(path))
                {
                    error = $"추적 파일을 저장하지 못했습니다 (코드 0x{code:X8}). {Summarize(output)}";
                    path = null;
                    cancelError = await CancelSessionAsync().ConfigureAwait(false);
                }
            }
            else cancelError = await CancelSessionAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error("정밀 추적 중지 실패", ex);
            error = "정밀 추적 중지 실패: " + ex.Message;
            cancelError = await CancelSessionAsync().ConfigureAwait(false);
            path = null;
        }
        finally
        {
            if (State == TraceState.Stopping)
            {
                LastPath = path ?? LastPath;
                if (cancelError is not null) error = string.Join(" ", new[] { error, cancelError }.OfType<string>());
                LastError = error;
                _timer?.Dispose();
                _timer = null;
                EndsAt = null;
                SetState(cancelError is null ? TraceState.Idle : TraceState.CancelFailed, cancelError is null ? null : Current);
                try { Finished?.Invoke(path, error); } catch (Exception ex) { Logger.Error("정밀 추적 종료 알림 실패", ex); }
            }
            _gate.Release();
        }
    }

    private async Task<string?> CancelSessionAsync()
    {
        try
        {
            var (code, output) = await _run(TimeSpan.FromSeconds(15), ["-cancel", "-instancename", InstanceName]).ConfigureAwait(false);
            if (code == 0 || code == NoTraceRunning) return null;
            return $"추적 취소 실패 (0x{code:X8}). 취소를 다시 시도하세요. {Summarize(output)}";
        }
        catch (Exception ex) { return "추적 취소 실패. 취소를 다시 시도하세요. " + ex.Message; }
    }

    private void SetState(TraceState s, Options? o)
    {
        State = s;
        Current = o;
        try { Changed?.Invoke(); } catch (Exception ex) { Logger.Error("정밀 추적 상태 알림 실패", ex); }
    }

    private static string Summarize(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("Microsoft Windows Performance Recorder", StringComparison.Ordinal) && !l.StartsWith("Copyright", StringComparison.Ordinal));
        var s = string.Join(" ", lines);
        return s.Length > 300 ? s[..300] + "…" : s;
    }

    /// <summary>wpr.exe를 창 없이 실행한다. 제한 시간을 넘기면 종료시키고 실패로 본다.</summary>
    private static async Task<(int Code, string Output)> RunWpr(TimeSpan timeout, params string[] args)
    {
        if (!IsAvailable) throw new FileNotFoundException("Windows Performance Recorder(wpr.exe)를 찾을 수 없습니다.");
        var psi = new ProcessStartInfo(WprPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("wpr.exe를 실행할 수 없습니다.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(); } catch { /* 이미 종료 */ }
            return (-1, "wpr.exe 응답 시간 초과");
        }
        string output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
        int code = p.ExitCode == NoTraceRunning && args.Contains("-cancel") ? 0 : p.ExitCode;
        return (code, output);
    }

    /// <summary>SysGlass가 만든 추적 파일만 정리한다: 30일 지난 파일, 최근 3개를 넘는 파일.</summary>
    internal void Cleanup(DateTime nowUtc)
    {
        try
        {
            if (!System.IO.Directory.Exists(_dir)) return;
            var files = new DirectoryInfo(_dir).GetFiles("sysglass-pool-*.etl").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            for (int i = 0; i < files.Count; i++)
                if (i >= KeepTraces || nowUtc - files[i].LastWriteTimeUtc > TimeSpan.FromDays(RetentionDays))
                {
                    try { files[i].Delete(); } catch (IOException) { /* 사용 중 */ } catch (UnauthorizedAccessException) { }
                }
        }
        catch (Exception ex) { Logger.Error("추적 파일 정리 실패", ex); }
    }

    /// <summary>새 추적을 차단한다. 창 종료 경로에서는 먼저 StopAsync의 완료를 기다린다.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAsync(save: false).GetAwaiter().GetResult();
        if (State == TraceState.CancelFailed) Logger.Error(LastError ?? "종료 시 정밀 추적 취소 실패");
    }
}
