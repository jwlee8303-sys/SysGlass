using SysGlass.Models;
using SysGlass.Utilities;

namespace SysGlass.Services;

/// <summary>Windows API(GetSystemTimes, GlobalMemoryStatusEx)로 CPU/RAM을 수집한다.</summary>
public sealed class WindowsSystemMonitor : ISystemMonitor, IDisposable
{
    private ulong _prevIdle, _prevTotal;
    private bool _hasPrev;

    private readonly ISensorProvider _sensors = new LibreHardwareSensorProvider();
    private readonly NetworkMonitor _network = new();
    private readonly CpuClockEstimator _clock = new();

    public SystemSnapshot Sample(SensorOptions options)
    {
        var cpu = ReadCpu();
        var (total, used) = ReadMemory();
        var r = _sensors.Read(options);
        var (down, up) = options.Network ? _network.Sample() : (null, null);
        // 센서 클럭이 없을 때만 계산값으로 대체하고, 계산값임을 표시한다.
        double? clock = r.CpuClockMhz;
        bool estimate = false;
        if (clock is null && options.Cpu) { clock = _clock.Read(); estimate = clock is not null; }
        return new SystemSnapshot(cpu, total, used)
        {
            CpuName = r.CpuName, CpuTempC = r.CpuTempC, CpuClockMhz = clock, CpuClockIsEstimate = estimate, SensorNotice = r.Notice, CpuPowerW = r.CpuPowerW,
            Gpus = r.Gpus, Disks = r.Disks, NetDownBytesPerSec = down, NetUpBytesPerSec = up
        };
    }

    public void Dispose() { _sensors.Dispose(); _clock.Dispose(); }

    /// <summary>이전 호출과의 차이로 CPU 사용률을 계산한다. 첫 호출은 기준값이 없어 null.</summary>
    private double? ReadCpu()
    {
        try
        {
            if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
                return null;

            // kernel 시간에는 idle 시간이 포함되어 있다.
            ulong idleNow = idle.Value;
            ulong totalNow = kernel.Value + user.Value;

            double? result = null;
            if (_hasPrev)
            {
                ulong dTotal = totalNow - _prevTotal;
                ulong dIdle = idleNow - _prevIdle;
                if (dTotal > 0)
                    result = Math.Clamp((dTotal - dIdle) * 100.0 / dTotal, 0, 100);
            }

            _prevIdle = idleNow;
            _prevTotal = totalNow;
            _hasPrev = true;
            return result;
        }
        catch (Exception ex)
        {
            Logger.Error("CPU 수집 실패", ex);
            return null;
        }
    }

    private static (ulong? total, ulong? used) ReadMemory()
    {
        try
        {
            var status = new NativeMethods.MEMORYSTATUSEX
            {
                dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
            };
            if (!NativeMethods.GlobalMemoryStatusEx(ref status))
                return (null, null);
            return (status.ullTotalPhys, status.ullTotalPhys - status.ullAvailPhys);
        }
        catch (Exception ex)
        {
            Logger.Error("메모리 수집 실패", ex);
            return (null, null);
        }
    }
}

