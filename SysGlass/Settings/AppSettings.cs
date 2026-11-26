using System.Reflection;
using System.Text.Json.Serialization;

namespace SysGlass.Settings;

/// <summary>선택된 장치(GPU/디스크)의 식별 정보. 순서가 바뀌어도 매칭할 수 있도록 Id와 모델명을 함께 저장한다.</summary>
public sealed class DeviceKey
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>JSON으로 저장되는 사용자 설정. 새 필드는 안전한 기본값을 가진다.</summary>
public sealed class AppSettings
{
    // ── 화면 및 디자인 ──
    /// <summary>배경 불투명도 (0.2 ~ 1.0)</summary>
    public double BackgroundOpacity { get; set; } = 0.8;
    /// <summary>글자 크기 단계: 0=작게, 1=보통, 2=크게</summary>
    public int FontSizeLevel { get; set; } = 1;
    public double WindowWidth { get; set; } = 280;
    /// <summary>컴팩트 보기: 항목을 위젯형 칸으로 작게 표시한다.</summary>
    public bool CompactMode { get; set; }
    /// <summary>컴팩트 보기 모양. 0 = 원형 링, 1 = 배터리 캡슐</summary>
    public int CompactStyle { get; set; }

    // ── 동작 ──
    public bool AlwaysOnTop { get; set; } = true;
    /// <summary>마우스 클릭 통과 모드. 트레이가 준비된 뒤에만 시작 시 적용된다.</summary>
    public bool IsClickThroughEnabled { get; set; }
    public int RefreshIntervalMs { get; set; } = 1000;

    // ── 표시 그룹 순서 ──
    /// <summary>오버레이 그룹 키의 기본 순서: CPU, RAM, 메모리 감시, GPU, 디스크, 네트워크.</summary>
    public static readonly string[] DefaultGroupOrder = ["cpu", "ram", "memory", "gpu", "disk", "net"];
    public List<string> GroupOrder { get; set; } = [.. DefaultGroupOrder];

    /// <summary>알 수 없는 키·중복을 버리고, 빠진 그룹은 기본 순서대로 뒤에 붙인다.</summary>
    public static List<string> NormalizeGroupOrder(IEnumerable<string>? order)
    {
        var result = new List<string>();
        foreach (var key in order ?? [])
            if (key is not null && DefaultGroupOrder.Contains(key) && !result.Contains(key)) result.Add(key);
        foreach (var key in DefaultGroupOrder)
            if (!result.Contains(key)) result.Add(key);
        return result;
    }

    // ── 표시 항목 ──
    public bool ShowCpuUsage { get; set; } = true;
    public bool ShowCpuTemp { get; set; } = true;
    public bool ShowCpuPower { get; set; } = true;
    public bool ShowCpuClock { get; set; } = true;
    public bool ShowRamUsage { get; set; } = true;
    public bool ShowGpuUsage { get; set; } = true;
    public bool ShowGpuTemp { get; set; } = true;
    public bool ShowVram { get; set; } = true;
    public bool ShowDiskUsage { get; set; } = true;
    public bool ShowDiskTemp { get; set; } = true;
    public bool ShowDiskRead { get; set; } = true;
    public bool ShowDiskWrite { get; set; } = true;
    public bool ShowNetDown { get; set; } = true;
    public bool ShowNetUp { get; set; } = true;
    public bool ShowPagedPool { get; set; } = true;
    public bool ShowNonPagedPool { get; set; } = true;
    public bool ShowPoolGrowth { get; set; } = true;
    public bool ShowUptime { get; set; }

    // 이전 버전 설정 파일 호환용. Normalize()에서 새 필드로 옮기고 비운다(저장되지 않음).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ShowDiskIo { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? ShowNetwork { get; set; }

    // ── 하드웨어 선택 (null = 감지된 모두 표시) ──
    public List<DeviceKey>? SelectedGpus { get; set; }
    public List<DeviceKey>? SelectedDisks { get; set; }

    // ── 온도 색상 기준(°C). 참고용이며 하드웨어 안전 한계를 의미하지 않는다 ──
    public double CpuTempWarn { get; set; } = 75;
    public double CpuTempHigh { get; set; } = 90;
    public double GpuTempWarn { get; set; } = 75;
    public double GpuTempHigh { get; set; } = 85;
    public double DiskTempWarn { get; set; } = 55;
    public double DiskTempHigh { get; set; } = 70;

    // ── 메모리 감시 (수집 주기는 초 단위) ──
    public bool MemoryWatchEnabled { get; set; } = true;
    public int MemKernelIntervalSec { get; set; } = 5;
    public int MemResidentIntervalSec { get; set; } = 30;
    public int MemPoolTagIntervalSec { get; set; } = 60;
    public int MemProcessIntervalSec { get; set; } = 60;
    public int MemUptimeIntervalSec { get; set; } = 60;
    public bool ShowMemoryStatus { get; set; } = true;
    public bool MemAlertsEnabled { get; set; } = true;
    /// <summary>상태 색상: 0=기본(노랑/빨강), 1=고대비(주황/자홍)</summary>
    public int MemColorScheme { get; set; }

    // ── 메모리 이상 감지 기준. 가용 RAM은 값이 기준 '이하'일 때 이상 ──
    public int MemPagedWarnMb { get; set; } = 2048;
    public int MemPagedHighMb { get; set; } = 4096;
    public int MemNonPagedWarnMb { get; set; } = 2048;
    public int MemNonPagedHighMb { get; set; } = 4096;
    public int MemPoolTagWarnMb { get; set; } = 512;
    public int MemPoolTagHighMb { get; set; } = 1024;
    public int MemGrowthWarnMbPerHour { get; set; } = 200;
    public int MemGrowthHighMbPerHour { get; set; } = 500;
    public int MemCommitWarnPercent { get; set; } = 80;
    public int MemCommitHighPercent { get; set; } = 90;
    public int MemAvailWarnPercent { get; set; } = 15;
    public int MemAvailHighPercent { get; set; } = 5;
    public int MemAlertSustainMin { get; set; } = 5;
    public int MemAlertRepeatMin { get; set; } = 30;

    // ── 메모리 감시 기록 ──
    public bool MemLogEnabled { get; set; } = true;
    public int MemRetentionDays { get; set; } = 30;
    public int MemLogMaxMb { get; set; } = 200;

    /// <summary>감지 기준 범위(최소, 최대). 설정 화면과 보정에서 함께 쓴다.</summary>
    public static readonly (int Min, int Max) PoolMbRange = (64, 262144), TagMbRange = (16, 65536),
        GrowthRange = (10, 65536), CommitRange = (10, 100), AvailRange = (1, 90),
        SustainRange = (1, 120), RepeatRange = (5, 1440);

    // ── 창 위치 (null이면 기본 위치) ──
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    public const double MinWidth = 200, MaxWidth = 500;
    public const double MinTemp = 30, MaxTemp = 110;

    /// <summary>값 범위를 보정한다 (손상/수정된 파일, 이전 버전 파일 대비).</summary>
    public void Normalize()
    {
        if (ShowDiskIo is { } io) { ShowDiskRead = ShowDiskWrite = io; ShowDiskIo = null; }
        if (ShowNetwork is { } net) { ShowNetDown = ShowNetUp = net; ShowNetwork = null; }

        BackgroundOpacity = Finite(BackgroundOpacity, 0.8, 0.2, 1.0);
        FontSizeLevel = Math.Clamp(FontSizeLevel, 0, 2);
        CompactStyle = Math.Clamp(CompactStyle, 0, 1);
        GroupOrder = NormalizeGroupOrder(GroupOrder);
        WindowWidth = Finite(WindowWidth, 280, MinWidth, MaxWidth);
        if (RefreshIntervalMs is not (500 or 1000 or 2000 or 5000)) RefreshIntervalMs = 1000;

        (CpuTempWarn, CpuTempHigh) = Pair(CpuTempWarn, CpuTempHigh, 75, 90);
        (GpuTempWarn, GpuTempHigh) = Pair(GpuTempWarn, GpuTempHigh, 75, 85);
        (DiskTempWarn, DiskTempHigh) = Pair(DiskTempWarn, DiskTempHigh, 55, 70);

        MemKernelIntervalSec = Math.Clamp(MemKernelIntervalSec, 1, 60);
        MemResidentIntervalSec = Math.Clamp(MemResidentIntervalSec, 5, 600);
        MemPoolTagIntervalSec = Math.Clamp(MemPoolTagIntervalSec, 15, 3600);
        MemProcessIntervalSec = Math.Clamp(MemProcessIntervalSec, 15, 3600);
        MemUptimeIntervalSec = Math.Clamp(MemUptimeIntervalSec, 5, 3600);
        MemColorScheme = Math.Clamp(MemColorScheme, 0, 1);
        (MemPagedWarnMb, MemPagedHighMb) = IntPair(MemPagedWarnMb, MemPagedHighMb, 2048, 4096, PoolMbRange, false);
        (MemNonPagedWarnMb, MemNonPagedHighMb) = IntPair(MemNonPagedWarnMb, MemNonPagedHighMb, 2048, 4096, PoolMbRange, false);
        (MemPoolTagWarnMb, MemPoolTagHighMb) = IntPair(MemPoolTagWarnMb, MemPoolTagHighMb, 512, 1024, TagMbRange, false);
        (MemGrowthWarnMbPerHour, MemGrowthHighMbPerHour) = IntPair(MemGrowthWarnMbPerHour, MemGrowthHighMbPerHour, 200, 500, GrowthRange, false);
        (MemCommitWarnPercent, MemCommitHighPercent) = IntPair(MemCommitWarnPercent, MemCommitHighPercent, 80, 90, CommitRange, false);
        (MemAvailWarnPercent, MemAvailHighPercent) = IntPair(MemAvailWarnPercent, MemAvailHighPercent, 15, 5, AvailRange, true);
        MemAlertSustainMin = Math.Clamp(MemAlertSustainMin, SustainRange.Min, SustainRange.Max);
        MemAlertRepeatMin = Math.Clamp(MemAlertRepeatMin, RepeatRange.Min, RepeatRange.Max);
        MemRetentionDays = Math.Clamp(MemRetentionDays, 1, 365);
        MemLogMaxMb = Math.Clamp(MemLogMaxMb, 10, 4096);

        SelectedGpus = Clean(SelectedGpus);
        SelectedDisks = Clean(SelectedDisks);

        if (WindowLeft is { } l && !double.IsFinite(l)) WindowLeft = null;
        if (WindowTop is { } t && !double.IsFinite(t)) WindowTop = null;
    }

    private static double Finite(double v, double def, double min, double max) =>
        double.IsFinite(v) ? Math.Clamp(v, min, max) : def;

    /// <summary>주의 기준이 경고 기준보다 낮도록 보정한다.</summary>
    private static (double warn, double high) Pair(double warn, double high, double defWarn, double defHigh)
    {
        warn = Finite(warn, defWarn, MinTemp, MaxTemp - 1);
        high = Finite(high, defHigh, MinTemp + 1, MaxTemp);
        if (warn >= high) (warn, high) = (defWarn, defHigh);
        return (warn, high);
    }

    /// <summary>
    /// 주의·위험 기준을 범위 안으로 보정한다. 보통은 주의 &lt; 위험이고, lowerIsWorse(가용 RAM 등)면 주의 &gt; 위험이다.
    /// 순서가 맞지 않으면 기본값으로 되돌린다.
    /// </summary>
    internal static (int warn, int high) IntPair(int warn, int high, int defWarn, int defHigh, (int Min, int Max) range, bool lowerIsWorse)
    {
        warn = Math.Clamp(warn, range.Min, range.Max);
        high = Math.Clamp(high, range.Min, range.Max);
        bool ordered = lowerIsWorse ? warn > high : warn < high;
        return ordered ? (warn, high) : (defWarn, defHigh);
    }

    private static List<DeviceKey>? Clean(List<DeviceKey>? list) =>
        list?.Where(k => k is not null && (!string.IsNullOrEmpty(k.Id) || !string.IsNullOrEmpty(k.Name))).ToList();

    /// <summary>기본값으로 되돌린다. includeWindow가 false면 창 위치/너비는 유지한다.</summary>
    public void ResetToDefaults(bool includeWindow)
    {
        var defaults = new AppSettings();
        foreach (var p in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite) continue;
            if (!includeWindow && (p.Name is nameof(WindowLeft) or nameof(WindowTop) or nameof(WindowWidth))) continue;
            p.SetValue(this, p.GetValue(defaults));
        }
    }
}
