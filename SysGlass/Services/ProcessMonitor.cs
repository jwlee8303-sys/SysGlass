using System.Diagnostics;
using System.Text.RegularExpressions;
using SysGlass.Models;
using SysGlass.Utilities;

namespace SysGlass.Services;

/// <summary>
/// 실행 중인 프로세스의 자원 사용량(CPU/GPU/메모리/I/O)을 수집해 이름별로 합산한다.
/// 사용률은 PID별 이전 표본과의 차이로 계산하므로 첫 표본의 CPU/I/O는 0이다. 한 스레드에서만 호출해야 한다.
/// </summary>
public sealed partial class ProcessMonitor
{
    /// <summary>PID별 이전 표본. 지표마다 유효 여부와 측정 시각을 따로 두어, 조회 실패 뒤 복구돼도 잘못된 차분이 나오지 않게 한다.</summary>
    private readonly record struct Prev(ulong CpuTime, ulong IoBytes, ulong CreateTime, bool HasTimes, bool HasIo, long Ticks);

    private readonly Dictionary<int, Prev> _prev = new();
    private readonly GpuProcessCounters _gpu = new();

    public bool GpuAvailable { get; private set; } = true;

    public List<ProcessGroupSample> Sample()
    {
        long nowTicks = Stopwatch.GetTimestamp();
        int cores = Math.Max(1, Environment.ProcessorCount);

        // PID → (엔진 ID → 사용률). 읽지 못하면 null.
        var gpuByPid = _gpu.Read();
        GpuAvailable = gpuByPid is not null;

        var groups = new Dictionary<string, ProcessGroupSample>(StringComparer.OrdinalIgnoreCase);
        var gpuEngines = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        var alive = new HashSet<int>();

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                int pid = p.Id;
                if (pid == 0) continue; // 시스템 유휴 프로세스는 제외 (CPU 여유분을 나타낼 뿐이다)

                string name;
                try { name = p.ProcessName; } catch { continue; }

                if (!groups.TryGetValue(name, out var g))
                    groups[name] = g = new ProcessGroupSample { Name = name };
                g.Count++;
                alive.Add(pid);

                // GPU: 같은 이름의 프로세스는 엔진별로 합산하고, 마지막에 가장 바쁜 엔진 값을 대표값으로 쓴다.
                if (gpuByPid is not null)
                {
                    if (!gpuEngines.TryGetValue(name, out var engines))
                        gpuEngines[name] = engines = new Dictionary<string, double>();
                    if (gpuByPid.TryGetValue(pid, out var perEngine))
                        foreach (var (engine, util) in perEngine)
                            engines[engine] = engines.GetValueOrDefault(engine) + util;
                }

                var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) continue; // 보호된 프로세스는 이름만 표시한다. (측정 개수에 포함되지 않음)
                try
                {
                    g.Measured++;

                    if (NativeMethods.GetProcessMemoryInfo(h, out var mem, (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.PROCESS_MEMORY_COUNTERS>()))
                        g.MemoryBytes += mem.WorkingSetSize;

                    ulong cpuTime = 0, ioBytes = 0, create = 0;
                    bool hasTimes = false, hasIo = false;
                    if (NativeMethods.GetProcessTimes(h, out var c, out _, out var k, out var u))
                    {
                        cpuTime = k.Value + u.Value; // 100ns 단위
                        create = c.Value;
                        hasTimes = true;
                    }
                    if (NativeMethods.GetProcessIoCounters(h, out var io))
                    {
                        ioBytes = io.ReadTransferCount + io.WriteTransferCount;
                        hasIo = true;
                    }

                    // 이 PID의 직전 표본과 같은 프로세스(생성 시각 일치)일 때만 차분을 구한다.
                    // 경과 시간은 PID별 표본 시각으로 계산해, 중간에 조회가 실패한 구간도 정확히 반영한다.
                    if (_prev.TryGetValue(pid, out var prev) && hasTimes && prev.HasTimes && prev.CreateTime == create)
                    {
                        double elapsed = (nowTicks - prev.Ticks) / (double)Stopwatch.Frequency;
                        if (elapsed > 0)
                        {
                            if (cpuTime >= prev.CpuTime)
                                g.CpuPercent += (cpuTime - prev.CpuTime) / 1e7 / elapsed / cores * 100.0;
                            if (hasIo && prev.HasIo && ioBytes >= prev.IoBytes)
                                g.IoBytesPerSec += (ioBytes - prev.IoBytes) / elapsed;
                        }
                    }
                    _prev[pid] = new Prev(cpuTime, ioBytes, create, hasTimes, hasIo, nowTicks);

                    // 설명이 내장 목록에 없는 프로세스만 경로를 조회한다. 같은 이름인데 경로가 다르면 표시해 둔다.
                    if (!ProcessCatalog.IsKnown(name) && QueryPath(h) is { } path)
                    {
                        if (g.ImagePath is null) g.ImagePath = path;
                        else if (!string.Equals(g.ImagePath, path, StringComparison.OrdinalIgnoreCase)) g.MixedPaths = true;
                    }
                }
                finally { NativeMethods.CloseHandle(h); }
            }
        }

        // 종료된 프로세스의 기준값 정리
        foreach (var pid in _prev.Keys.Where(k => !alive.Contains(k)).ToList()) _prev.Remove(pid);

        foreach (var g in groups.Values)
        {
            g.CpuPercent = Math.Clamp(g.CpuPercent, 0, 100);
            if (gpuEngines.TryGetValue(g.Name, out var engines))
                g.GpuPercent = engines.Count == 0 ? 0 : Math.Clamp(engines.Values.Max(), 0, 100);
        }
        return groups.Values.ToList();
    }

    /// <summary>실행 파일 경로를 조회한다. 버퍼가 부족하면 최대 길이로 한 번 더 시도한다.</summary>
    private static string? QueryPath(IntPtr handle)
    {
        foreach (int capacity in new[] { 1024, 32768 })
        {
            var buf = new char[capacity];
            uint size = (uint)buf.Length;
            if (NativeMethods.QueryFullProcessImageNameW(handle, 0, buf, ref size))
                return new string(buf, 0, (int)size);
            // ERROR_INSUFFICIENT_BUFFER(122)일 때만 큰 버퍼로 다시 시도한다.
            if (System.Runtime.InteropServices.Marshal.GetLastWin32Error() != 122) return null;
        }
        return null;
    }

    /// <summary>
    /// "GPU Engine" 성능 카운터에서 PID별·엔진별 GPU 사용률을 읽는다.
    /// 인스턴스 이름의 GPU(luid)·물리 GPU·엔진 번호·엔진 종류를 엔진 ID로 유지해, 서로 다른 엔진을 섞지 않는다.
    /// 읽기에 실패하면 null을 돌려주고, 일정 시간 뒤 다시 시도한다.
    /// </summary>
    private sealed partial class GpuProcessCounters
    {
        private static readonly long RetryDelayTicks = 15 * Stopwatch.Frequency;

        private readonly Dictionary<string, CounterSample> _prev = new();
        private long _nextTry;
        private bool _loggedFailure;

        [GeneratedRegex(@"pid_(\d+)_(.+?_engtype_.+)$", RegexOptions.IgnoreCase)]
        private static partial Regex InstanceRegex();

        public Dictionary<int, Dictionary<string, double>>? Read()
        {
            long now = Stopwatch.GetTimestamp();
            if (now < _nextTry) return null;
            try
            {
                var data = new PerformanceCounterCategory("GPU Engine").ReadCategory();
                if (!data.Contains("Utilization Percentage"))
                    throw new InvalidOperationException("GPU Engine 카운터에 Utilization Percentage가 없음");

                var result = new Dictionary<int, Dictionary<string, double>>();
                var seen = new HashSet<string>();
                foreach (InstanceData d in data["Utilization Percentage"].Values)
                {
                    var name = d.InstanceName;
                    seen.Add(name);
                    var cur = d.Sample;
                    if (_prev.TryGetValue(name, out var prev) && InstanceRegex().Match(name) is { Success: true } m)
                    {
                        double v = Math.Clamp(CounterSample.Calculate(prev, cur), 0, 100);
                        int pid = int.Parse(m.Groups[1].Value);
                        string engine = m.Groups[2].Value; // luid_phys_eng_engtype
                        if (!result.TryGetValue(pid, out var perEngine)) result[pid] = perEngine = new Dictionary<string, double>();
                        perEngine[engine] = perEngine.GetValueOrDefault(engine) + v;
                    }
                    _prev[name] = cur;
                }
                foreach (var k in _prev.Keys.Where(k => !seen.Contains(k)).ToList()) _prev.Remove(k);

                _loggedFailure = false;
                return result;
            }
            catch (Exception ex)
            {
                // 일시적인 오류(드라이버 재설정 등)일 수 있으므로 표본을 버리고 잠시 뒤 다시 시도한다.
                if (!_loggedFailure) Logger.Error("GPU 프로세스 카운터 읽기 실패 (잠시 뒤 다시 시도)", ex);
                _loggedFailure = true;
                _prev.Clear();
                _nextTry = now + RetryDelayTicks;
                return null;
            }
        }
    }
}
