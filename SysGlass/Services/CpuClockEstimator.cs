using System.Diagnostics;
using SysGlass.Utilities;

namespace SysGlass.Services;

/// <summary>
/// Windows 성능 카운터 기반 CPU 클럭 "계산값" = 기준 클럭 × % Processor Performance.
/// 센서가 직접 측정한 클럭이 아니므로 화면에서 (계산)으로 구분 표시한다.
/// </summary>
public sealed class CpuClockEstimator : IDisposable
{
    private PerformanceCounter? _perf, _baseMhz;
    private bool _failed;

    public double? Read()
    {
        if (_failed) return null;
        try
        {
            _perf ??= new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
            _baseMhz ??= new PerformanceCounter("Processor Information", "Processor Frequency", "_Total", true);
            double perf = _perf.NextValue(), baseMhz = _baseMhz.NextValue();
            // 첫 호출은 0을 반환할 수 있다.
            return perf > 0 && baseMhz > 0 ? baseMhz * perf / 100.0 : null;
        }
        catch (Exception ex)
        {
            Logger.Error("CPU 클럭 계산용 성능 카운터 사용 불가", ex);
            _failed = true;
            return null;
        }
    }

    public void Dispose()
    {
        _perf?.Dispose();
        _baseMhz?.Dispose();
    }
}
