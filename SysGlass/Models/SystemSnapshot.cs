namespace SysGlass.Models;

public sealed record GpuInfo(string Id, string Name, double? UsagePercent, double? TempC,
    double? VramTotalMb, double? VramUsedMb)
{
    public double? VramPercent =>
        VramTotalMb is { } t && t > 0 && VramUsedMb is { } u ? Math.Clamp(u * 100 / t, 0, 100) : null;
}

/// <summary>물리 디스크. Number는 Windows 디스크 번호이며 매핑이 불확실하면 null.</summary>
public sealed record DiskInfo(string Id, int? Number, string Name, string Kind, double? ActivityPercent,
    double? TempC, double? ReadBytesPerSec, double? WriteBytesPerSec);

/// <summary>한 번의 수집 결과. 측정 불가한 값은 null(화면에는 N/A로 표시).</summary>
public sealed record SystemSnapshot(
    double? CpuUsagePercent,
    ulong? MemoryTotalBytes,
    ulong? MemoryUsedBytes)
{
    public ulong? MemoryAvailableBytes =>
        MemoryTotalBytes is { } t && MemoryUsedBytes is { } u ? t - u : null;

    public double? MemoryUsagePercent =>
        MemoryTotalBytes is { } t && t > 0 && MemoryUsedBytes is { } u ? u * 100.0 / t : null;

    public string? CpuName { get; init; }
    public double? CpuTempC { get; init; }
    public double? CpuClockMhz { get; init; }
    /// <summary>true면 CpuClockMhz는 센서 측정값이 아니라 성능 카운터 기반 계산값.</summary>
    public bool CpuClockIsEstimate { get; init; }
    public string? SensorNotice { get; init; }
    /// <summary>CPU Package 전력(W). 센서 실측값만 사용.</summary>
    public double? CpuPowerW { get; init; }
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];
    public IReadOnlyList<DiskInfo> Disks { get; init; } = [];
    public double? NetDownBytesPerSec { get; init; }
    public double? NetUpBytesPerSec { get; init; }
}