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

    // ── 동작 ──
    public bool AlwaysOnTop { get; set; } = true;
    /// <summary>마우스 클릭 통과 모드. 트레이가 준비된 뒤에만 시작 시 적용된다.</summary>
    public bool IsClickThroughEnabled { get; set; }
    public int RefreshIntervalMs { get; set; } = 1000;

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
        WindowWidth = Finite(WindowWidth, 280, MinWidth, MaxWidth);
        if (RefreshIntervalMs is not (500 or 1000 or 2000 or 5000)) RefreshIntervalMs = 1000;

        (CpuTempWarn, CpuTempHigh) = Pair(CpuTempWarn, CpuTempHigh, 75, 90);
        (GpuTempWarn, GpuTempHigh) = Pair(GpuTempWarn, GpuTempHigh, 75, 85);
        (DiskTempWarn, DiskTempHigh) = Pair(DiskTempWarn, DiskTempHigh, 55, 70);

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
