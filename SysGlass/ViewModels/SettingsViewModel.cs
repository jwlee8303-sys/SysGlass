using System.Runtime.CompilerServices;
using SysGlass.Settings;

namespace SysGlass.ViewModels;

/// <summary>메인 창의 동작(항상 위, 클릭 통과, 기본값 복원)을 설정 창에 연결하는 인터페이스. 기능을 중복 구현하지 않는다.</summary>
public interface IMainWindowController
{
    bool AlwaysOnTop { get; set; }
    bool ClickThrough { get; set; }
    /// <summary>기본값 복원 후 창 상태(위치/너비/항상 위/클릭 통과)를 다시 반영한다.</summary>
    void ApplyAfterReset(bool includeWindow);
    event Action? ControllerStateChanged;
}

/// <summary>설정 창의 ViewModel. 값을 AppSettings에 즉시 기록하고 메인 화면에 실시간 반영한다.</summary>
public sealed class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly AppSettings _s;
    private readonly MainViewModel _main;
    private readonly IMainWindowController _controller;

    public SettingsViewModel(MainViewModel main, IMainWindowController controller)
    {
        _main = main;
        _s = main.Settings;
        _controller = controller;
        _controller.ControllerStateChanged += OnControllerChanged;
    }

    public DeviceSelectionViewModel Gpus => _main.GpuSelection;
    public DeviceSelectionViewModel Disks => _main.DiskSelection;

    private void OnControllerChanged()
    {
        Raise(nameof(AlwaysOnTop));
        Raise(nameof(ClickThrough));
    }

    /// <summary>설정 값을 바꾸고, 변경되었을 때만 화면 반영/저장 요청을 한다.</summary>
    private void Put<T>(T current, T value, Action<T> assign, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign(value);
        Raise(prop);
        _main.NotifySettingsChanged(prop ?? string.Empty);
    }

    // ── 표시 항목 ──
    public bool ShowCpuUsage { get => _s.ShowCpuUsage; set => Put(_s.ShowCpuUsage, value, v => _s.ShowCpuUsage = v); }
    public bool ShowCpuTemp { get => _s.ShowCpuTemp; set => Put(_s.ShowCpuTemp, value, v => _s.ShowCpuTemp = v); }
    public bool ShowCpuPower { get => _s.ShowCpuPower; set => Put(_s.ShowCpuPower, value, v => _s.ShowCpuPower = v); }
    public bool ShowCpuClock { get => _s.ShowCpuClock; set => Put(_s.ShowCpuClock, value, v => _s.ShowCpuClock = v); }
    public bool ShowRamUsage { get => _s.ShowRamUsage; set => Put(_s.ShowRamUsage, value, v => _s.ShowRamUsage = v); }
    public bool ShowGpuUsage { get => _s.ShowGpuUsage; set => Put(_s.ShowGpuUsage, value, v => _s.ShowGpuUsage = v); }
    public bool ShowGpuTemp { get => _s.ShowGpuTemp; set => Put(_s.ShowGpuTemp, value, v => _s.ShowGpuTemp = v); }
    public bool ShowVram { get => _s.ShowVram; set => Put(_s.ShowVram, value, v => _s.ShowVram = v); }
    public bool ShowDiskUsage { get => _s.ShowDiskUsage; set => Put(_s.ShowDiskUsage, value, v => _s.ShowDiskUsage = v); }
    public bool ShowDiskTemp { get => _s.ShowDiskTemp; set => Put(_s.ShowDiskTemp, value, v => _s.ShowDiskTemp = v); }
    public bool ShowDiskRead { get => _s.ShowDiskRead; set => Put(_s.ShowDiskRead, value, v => _s.ShowDiskRead = v); }
    public bool ShowDiskWrite { get => _s.ShowDiskWrite; set => Put(_s.ShowDiskWrite, value, v => _s.ShowDiskWrite = v); }
    public bool ShowNetDown { get => _s.ShowNetDown; set => Put(_s.ShowNetDown, value, v => _s.ShowNetDown = v); }
    public bool ShowNetUp { get => _s.ShowNetUp; set => Put(_s.ShowNetUp, value, v => _s.ShowNetUp = v); }

    // ── 화면 및 디자인 ──
    /// <summary>불투명도(%). 20~100</summary>
    public double OpacityPercent
    {
        get => Math.Round(_s.BackgroundOpacity * 100);
        set => Put(_s.BackgroundOpacity, Math.Clamp(Math.Round(value) / 100.0, 0.2, 1.0), v => _s.BackgroundOpacity = v);
    }

    public int FontSizeLevel { get => _s.FontSizeLevel; set => Put(_s.FontSizeLevel, Math.Clamp(value, 0, 2), v => _s.FontSizeLevel = v); }

    public double WindowWidth
    {
        get => Math.Round(_s.WindowWidth);
        set => Put(_s.WindowWidth, Math.Clamp(Math.Round(value), AppSettings.MinWidth, AppSettings.MaxWidth), v => _s.WindowWidth = v);
    }
    public double MinWindowWidth => AppSettings.MinWidth;
    public double MaxWindowWidth => AppSettings.MaxWidth;

    // ── 동작 ──
    public bool AlwaysOnTop { get => _controller.AlwaysOnTop; set { _controller.AlwaysOnTop = value; Raise(); } }
    public bool ClickThrough { get => _controller.ClickThrough; set { _controller.ClickThrough = value; Raise(); } }

    public int RefreshIntervalMs
    {
        get => _s.RefreshIntervalMs;
        set { if (value is 500 or 1000 or 2000 or 5000) Put(_s.RefreshIntervalMs, value, v => _s.RefreshIntervalMs = v); }
    }

    // ── 온도 경고: 주의 < 경고가 되도록 반대쪽 값을 보정한다 ──
    public double TempMin => AppSettings.MinTemp;
    public double TempMax => AppSettings.MaxTemp;

    public double CpuTempWarn { get => _s.CpuTempWarn; set => PutWarn(_s.CpuTempWarn, value, _s.CpuTempHigh, v => _s.CpuTempWarn = v); }
    public double CpuTempHigh { get => _s.CpuTempHigh; set => PutHigh(_s.CpuTempHigh, value, _s.CpuTempWarn, v => _s.CpuTempHigh = v); }
    public double GpuTempWarn { get => _s.GpuTempWarn; set => PutWarn(_s.GpuTempWarn, value, _s.GpuTempHigh, v => _s.GpuTempWarn = v); }
    public double GpuTempHigh { get => _s.GpuTempHigh; set => PutHigh(_s.GpuTempHigh, value, _s.GpuTempWarn, v => _s.GpuTempHigh = v); }
    public double DiskTempWarn { get => _s.DiskTempWarn; set => PutWarn(_s.DiskTempWarn, value, _s.DiskTempHigh, v => _s.DiskTempWarn = v); }
    public double DiskTempHigh { get => _s.DiskTempHigh; set => PutHigh(_s.DiskTempHigh, value, _s.DiskTempWarn, v => _s.DiskTempHigh = v); }

    private void PutWarn(double cur, double value, double high, Action<double> assign, [CallerMemberName] string? prop = null)
    {
        value = Math.Clamp(Math.Round(value), AppSettings.MinTemp, high - 1);
        if (value == cur) { Raise(prop); return; } // 슬라이더가 보정된 값으로 돌아가도록 알림
        assign(value);
        Raise(prop);
        _main.NotifySettingsChanged(prop ?? string.Empty);
    }

    private void PutHigh(double cur, double value, double warn, Action<double> assign, [CallerMemberName] string? prop = null)
    {
        value = Math.Clamp(Math.Round(value), warn + 1, AppSettings.MaxTemp);
        if (value == cur) { Raise(prop); return; }
        assign(value);
        Raise(prop);
        _main.NotifySettingsChanged(prop ?? string.Empty);
    }

    /// <summary>설정 값을 모두 다시 읽어 화면에 반영한다(기본값 복원 후).</summary>
    public void RefreshAll()
    {
        Raise(string.Empty);
        Gpus.RefreshFromSettings();
        Disks.RefreshFromSettings();
    }

    /// <summary>기본값 복원. includeWindow가 true면 창 위치/너비도 복원한다.</summary>
    public void ResetToDefaults(bool includeWindow)
    {
        _s.ResetToDefaults(includeWindow);
        _controller.ApplyAfterReset(includeWindow);
        _main.NotifySettingsChanged("Reset");
        RefreshAll();
    }

    public void Dispose() => _controller.ControllerStateChanged -= OnControllerChanged;
}
