using System.Diagnostics;
using System.Runtime.InteropServices;
using SysGlass.Models;
using SysGlass.Utilities;

namespace SysGlass.Services.Memory;

/// <summary>
/// 커널·시스템 메모리를 공식 API로 수집한다. GetPerformanceInfo(커밋·풀·핸들), GlobalMemoryStatusEx(물리 메모리),
/// PDH 성능 카운터(페이징 풀 상주량)를 프로세스 안에서 직접 조회하며 외부 프로세스를 실행하지 않는다.
/// </summary>
public sealed class KernelMemoryCollector : IDisposable
{
    private static readonly long RetryDelayTicks = 60 * Stopwatch.Frequency;

    private readonly object _gate = new();
    private PerformanceCounter? _resident;
    private long _residentNextTry;
    private bool _disposed;

    /// <summary>커널 메모리 표본을 읽는다. 실패한 항목은 null이며, 전체 실패 시 error에 사유를 담는다.</summary>
    public KernelMemorySample Read(out string? error)
    {
        error = null;
        var now = DateTimeOffset.Now;
        long active = NativeMethods.QueryUnbiasedInterruptTime(out ulong unbiased) ? (long)unbiased : Environment.TickCount64 * 10_000;
        var sample = new KernelMemorySample { Time = now, ActiveTime100ns = active };

        try
        {
            if (NativeMethods.GetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<NativeMethods.PERFORMANCE_INFORMATION>()))
            {
                ulong page = pi.PageSize;
                sample = sample with
                {
                    CommitTotal = pi.CommitTotal * page,
                    CommitLimit = pi.CommitLimit * page,
                    PagedPool = pi.KernelPaged * page,
                    NonPagedPool = pi.KernelNonpaged * page,
                    HandleCount = pi.HandleCount,
                    ProcessCount = pi.ProcessCount,
                    ThreadCount = pi.ThreadCount,
                };
            }
            else error = $"GetPerformanceInfo 실패 (Win32 오류 {Marshal.GetLastWin32Error()})";
        }
        catch (Exception ex) { error = "GetPerformanceInfo 호출 실패: " + ex.Message; }

        try
        {
            var status = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
            if (NativeMethods.GlobalMemoryStatusEx(ref status))
                sample = sample with { PhysicalTotal = status.ullTotalPhys, PhysicalAvailable = status.ullAvailPhys };
        }
        catch (Exception ex) { error ??= "GlobalMemoryStatusEx 호출 실패: " + ex.Message; }

        return sample;
    }

    /// <summary>
    /// 페이징 풀 상주량(\Memory\Pool Paged Resident Bytes)을 읽는다. 카운터를 열 수 없으면 null과 사유를 돌려주고
    /// 1분 뒤 다시 시도한다(성능 카운터가 비활성화된 시스템 대비).
    /// </summary>
    public ulong? ReadPagedResident(out string? error)
    {
        error = null;
        lock (_gate)
        {
            if (_disposed) { error = "종료됨"; return null; }
            long now = Stopwatch.GetTimestamp();
            if (_resident is null && now < _residentNextTry) { error = "성능 카운터를 사용할 수 없음 (재시도 대기)"; return null; }
            try
            {
                _resident ??= new PerformanceCounter("Memory", "Pool Paged Resident Bytes", readOnly: true);
                long raw = _resident.RawValue;
                if (raw < 0) throw new InvalidOperationException("음수 값");
                return (ulong)raw;
            }
            catch (Exception ex)
            {
                if (_residentNextTry == 0) Logger.Error("페이징 풀 상주량 카운터 읽기 실패", ex);
                _resident?.Dispose();
                _resident = null;
                _residentNextTry = now + RetryDelayTicks;
                error = "성능 카운터 읽기 실패: " + ex.Message;
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _resident?.Dispose();
            _resident = null;
        }
    }
}
