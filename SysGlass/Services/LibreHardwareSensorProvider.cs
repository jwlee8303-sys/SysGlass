using LibreHardwareMonitor.Hardware;
using SysGlass.Models;
using SysGlass.Utilities;

namespace SysGlass.Services;

/// <summary>LibreHardwareMonitorLib(MPL-2.0) 기반 센서 제공자. 모든 실패는 null/빈 값으로 처리한다.</summary>
public sealed class LibreHardwareSensorProvider : ISensorProvider
{
    private Computer? _computer;
    private bool _openFailed;
    private DiskCatalog? _catalog;
    private (bool cpu, bool gpu, bool disk) _enabled;

    public SensorReading Read(SensorOptions o)
    {
        string? cpuName = null;
        double? cpuTemp = null, cpuClock = null, cpuPower = null;
        var gpus = new List<GpuInfo>();
        var disks = new List<DiskInfo>();

        if (_openFailed || (!o.Cpu && !o.Gpu && !o.Disk))
            return new SensorReading(null, null, null, gpus, disks);

        try
        {
            EnsureOpen(o);
            if (_computer is null) return new SensorReading(null, null, null, gpus, disks);
            _catalog ??= new DiskCatalog();

            foreach (var hw in _computer.Hardware)
            {
                try
                {
                    switch (hw.HardwareType)
                    {
                        case HardwareType.Cpu when o.Cpu:
                            hw.Update();
                            cpuName ??= hw.Name;
                            cpuTemp ??= PickCpuTemp(hw);
                            cpuClock ??= AverageCoreClock(hw);
                            cpuPower ??= hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Name == "CPU Package")?.Value;
                            break;
                        case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel when o.Gpu:
                            hw.Update();
                            gpus.Add(ReadGpu(hw));
                            break;
                        case HardwareType.Storage when o.Disk:
                            hw.Update();
                            disks.Add(ReadDisk(hw, disks));
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"센서 읽기 실패: {hw.Name}", ex);
                }
            }
            ResolveDiskNumbers(disks);
        }
        catch (Exception ex)
        {
            Logger.Error("센서 수집 실패", ex);
        }
        // CPU 온도를 못 읽는 원인이 PawnIO 미설치라면 사용자에게 알린다(값은 추정하지 않음).
        string? notice = o.Cpu && cpuTemp is null && !IsPawnIoInstalled()
            ? "CPU 온도/클럭/전력 센서를 읽으려면 PawnIO 드라이버 설치가 필요합니다."
            : null;
        return new SensorReading(cpuName, cpuTemp, cpuClock, gpus, disks, notice, cpuPower);
    }

    /// <summary>LHM이 MSR/SuperIO 접근에 쓰는 PawnIO 설치 여부(레지스트리 Uninstall 항목).</summary>
    private static bool IsPawnIoInstalled()
    {
        try
        {
            using var k = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return k != null;
        }
        catch { return true; } // 확인 불가 시 안내 문구를 띄우지 않는다.
    }

    private void EnsureOpen(SensorOptions o)
    {
        if (_computer is null)
        {
            try
            {
                _computer = new Computer();
                _computer.Open();
            }
            catch (Exception ex)
            {
                Logger.Error("LibreHardwareMonitor 초기화 실패", ex);
                _openFailed = true;
                _computer = null;
                return;
            }
            _enabled = default;
        }
        // 표시 항목이 바뀌면 해당 하드웨어만 추가/제거한다 (LHM은 동적 변경 지원).
        if (_enabled != (o.Cpu, o.Gpu, o.Disk))
        {
            _computer.IsCpuEnabled = o.Cpu;
            _computer.IsGpuEnabled = o.Gpu;
            _computer.IsStorageEnabled = o.Disk;
            _enabled = (o.Cpu, o.Gpu, o.Disk);
        }
    }

    private static double? PickCpuTemp(IHardware hw)
    {
        var temps = hw.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 150).ToList();
        foreach (var name in new[] { "CPU Package", "Core (Tctl/Tdie)", "Core (Tdie)", "Core Max" })
            if (temps.FirstOrDefault(s => s.Name == name)?.Value is { } v) return v;
        // 위 이름이 없으면 센서가 보고한 최댓값(측정값)을 사용한다.
        return temps.Count > 0 ? temps.Max(s => s.Value) : null;
    }

    private static double? AverageCoreClock(IHardware hw)
    {
        var all = hw.Sensors
            .Where(s => s.SensorType == SensorType.Clock && s.Name.Contains("Core") && !s.Name.Contains("Effective") && s.Value is > 0)
            .ToList();
        // 하이브리드 CPU(P/E 코어)는 P-코어 평균만 사용한다.
        var pCores = all.Where(s => s.Name.StartsWith("P-Core")).ToList();
        var clocks = (pCores.Count > 0 ? pCores : all).Select(s => (double)s.Value!.Value).ToList();
        return clocks.Count > 0 ? clocks.Average() : null;
    }

    private static GpuInfo ReadGpu(IHardware hw)
    {
        var s = hw.Sensors;
        double? V(SensorType t, string name) => s.FirstOrDefault(x => x.SensorType == t && x.Name == name)?.Value;

        double? usage = V(SensorType.Load, "GPU Core")
            ?? s.Where(x => x.SensorType == SensorType.Load && x.Name.StartsWith("D3D 3D") && x.Value != null).Select(x => (double?)x.Value).DefaultIfEmpty(null).Max();
        double? temp = V(SensorType.Temperature, "GPU Core")
            ?? s.FirstOrDefault(x => x.SensorType == SensorType.Temperature && x.Value != null)?.Value;
        double? total = V(SensorType.SmallData, "GPU Memory Total") ?? V(SensorType.SmallData, "D3D Dedicated Memory Total");
        double? used = V(SensorType.SmallData, "GPU Memory Used") ?? V(SensorType.SmallData, "D3D Dedicated Memory Used");
        // 총량을 알 수 없으면 VRAM 비율은 표시하지 않는다.
        if (total is null) used = null;
        return new GpuInfo(hw.Identifier.ToString(), hw.Name, usage, temp, total, used);
    }

    private DiskInfo ReadDisk(IHardware hw, List<DiskInfo> existing)
    {
        var s = hw.Sensors;
        double? Val(SensorType t, string name) => s.FirstOrDefault(x => x.SensorType == t && x.Name == name)?.Value;

        double? temp = Val(SensorType.Temperature, "Temperature")
            ?? s.FirstOrDefault(x => x.SensorType == SensorType.Temperature && x.Value is > 0 and < 150)?.Value;
        return new DiskInfo(hw.Identifier.ToString(), null, hw.Name, _catalog!.KindFor(hw.Name),
            Val(SensorType.Load, "Total Activity"), temp,
            Val(SensorType.Throughput, "Read Rate"), Val(SensorType.Throughput, "Write Rate"));
    }

    /// <summary>모델명이 유일하게 일치할 때만 Windows 디스크 번호를 연결한다. 모호하면 번호를 비워 오매핑을 방지한다.</summary>
    private void ResolveDiskNumbers(List<DiskInfo> disks)
    {
        for (int i = 0; i < disks.Count; i++)
        {
            bool uniqueHere = disks.Count(d => d.Name == disks[i].Name) == 1;
            int? n = uniqueHere ? _catalog!.NumberFor(disks[i].Name) : null;
            disks[i] = disks[i] with { Number = n };
        }
    }

    public void Dispose()
    {
        try { _computer?.Close(); } catch { /* 무시 */ }
        _computer = null;
    }
}
