namespace SysGlass.Services;

/// <summary>센서 라이브러리가 제공하는 값(온도, 클럭, GPU, 디스크). 구현체 교체 가능.</summary>
public sealed record SensorReading(
    string? CpuName, double? CpuTempC, double? CpuClockMhz,
    IReadOnlyList<Models.GpuInfo> Gpus, IReadOnlyList<Models.DiskInfo> Disks,
    string? Notice = null, double? CpuPowerW = null);

/// <summary>어떤 하드웨어를 조회할지 선택하는 옵션. 꺼진 항목은 조회하지 않는다.</summary>
public sealed record SensorOptions(bool Cpu, bool Gpu, bool Disk, bool Network = false);

public interface ISensorProvider : IDisposable
{
    /// <summary>예외를 던지지 않고, 읽지 못한 값은 null로 반환한다.</summary>
    SensorReading Read(SensorOptions options);
}
