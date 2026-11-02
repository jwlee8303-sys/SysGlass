using System.Globalization;

namespace SysGlass.Utilities;

/// <summary>표시용 문자열 변환. null은 항상 N/A.</summary>
internal static class Format
{
    public const string NA = "N/A";
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static string Percent(double? v) => v is { } x ? string.Format(C, "{0:0}%", x) : NA;
    public static string Temp(double? v) => v is { } x ? string.Format(C, "{0:0}°C", x) : NA;

    public static string Clock(double? mhz) => mhz is { } m
        ? (m >= 1000 ? string.Format(C, "{0:0.00} GHz", m / 1000) : string.Format(C, "{0:0} MHz", m))
        : NA;

    /// <summary>bytes/s → KB/s 또는 MB/s 자동 변환</summary>
    public static string Rate(double? bytesPerSec)
    {
        if (bytesPerSec is not { } b) return NA;
        double kb = b / 1024;
        return kb >= 1000 ? string.Format(C, "{0:0.0} MB/s", kb / 1024) : string.Format(C, "{0:0} KB/s", kb);
    }

    /// <summary>디스크 번호/종류/모델명을 합친 선택 목록용 이름</summary>
    public static string DiskLabel(int? number, string kind, string name)
    {
        string head = number is { } n ? $"Disk {n}" : "Disk";
        if (kind.Length > 0) head += $" ({kind})";
        return $"{head}: {name}";
    }

    public static string Vram(double? usedMb, double? totalMb) =>
        usedMb is { } u && totalMb is { } t
            ? string.Format(C, "{0:0.0} / {1:0.#} GB", u / 1024, t / 1024)
            : NA;
}
