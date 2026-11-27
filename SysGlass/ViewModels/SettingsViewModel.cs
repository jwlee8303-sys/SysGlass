using System.Reflection;
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
        LoadGroupOrder();
    }

    // ── 표시 그룹 순서 (버튼·드래그로 변경) ──
    private static readonly Dictionary<string, string> GroupTitles = new()
    {
        ["cpu"] = "CPU", ["ram"] = "RAM", ["memory"] = "메모리 감시", ["gpu"] = "GPU · VRAM", ["disk"] = "디스크", ["net"] = "네트워크",
    };

    public System.Collections.ObjectModel.ObservableCollection<GroupOrderItem> GroupOrder { get; } = [];

    private void LoadGroupOrder()
    {
        GroupOrder.Clear();
        foreach (var key in AppSettings.NormalizeGroupOrder(_s.GroupOrder))
            GroupOrder.Add(new GroupOrderItem(key, GroupTitles[key]));
    }

    /// <summary>그룹을 from 위치에서 to 위치로 옮긴다. 범위를 벗어나거나 같은 위치면 무시한다.</summary>
    public void MoveGroup(int from, int to)
    {
        if (from < 0 || from >= GroupOrder.Count || to < 0 || to >= GroupOrder.Count || from == to) return;
        GroupOrder.Move(from, to);
        _s.GroupOrder = GroupOrder.Select(g => g.Key).ToList();
        _main.NotifySettingsChanged(nameof(AppSettings.GroupOrder));
    }

    public DeviceSelectionViewModel Gpus => _main.GpuSelection;
    public DeviceSelectionViewModel Disks => _main.DiskSelection;

    /// <summary>설정 창 하단에 표시할 앱 버전(예: "SysGlass 1.2.0"). 빌드 메타데이터(+커밋)는 제외한다.</summary>
    public string AppVersionText { get; } = BuildVersionText();

    private static string BuildVersionText()
    {
        var asm = typeof(SettingsViewModel).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = info?.Split('+')[0] ?? asm.GetName().Version?.ToString(3) ?? "알 수 없음";
        return $"SysGlass {version}";
    }

    // ── 업데이트 확인 ──
    private string _updateStatusText = "";
    private bool _isUpdateBusy;
    private Services.UpdateInfo? _availableUpdate;

    public string UpdateStatusText { get => _updateStatusText; private set => Set(ref _updateStatusText, value); }

    public bool IsUpdateBusy
    {
        get => _isUpdateBusy;
        private set { if (Set(ref _isUpdateBusy, value)) Raise(nameof(IsUpdateIdle)); }
    }

    public bool IsUpdateIdle => !_isUpdateBusy;

    public Services.UpdateInfo? AvailableUpdate
    {
        get => _availableUpdate;
        private set { if (Set(ref _availableUpdate, value)) Raise(nameof(HasUpdate)); }
    }

    public bool HasUpdate => _availableUpdate != null;

    /// <summary>GitHub Releases에서 최신 정식 버전을 확인하고 결과를 상태 문구로 표시한다.</summary>
    public async Task CheckForUpdateAsync()
    {
        if (IsUpdateBusy) return;
        IsUpdateBusy = true;
        AvailableUpdate = null;
        UpdateStatusText = "최신 버전을 확인하는 중…";
        try
        {
            var info = await Services.UpdateService.CheckAsync(TimeSpan.FromSeconds(15));
            AvailableUpdate = info;
            UpdateStatusText = info == null
                ? $"최신 버전을 사용 중입니다. ({Services.UpdateService.CurrentVersion})"
                : $"새 버전 {info.Version}을 사용할 수 있습니다." + (Services.UpdateService.CanApply ? "" : " (개발 빌드에서는 적용할 수 없습니다)");
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            UpdateStatusText = "업데이트를 확인할 수 없습니다. 인터넷 연결을 확인하세요.";
        }
        catch (Exception ex)
        {
            Utilities.Logger.Error("업데이트 확인 실패", ex);
            UpdateStatusText = "업데이트를 확인할 수 없습니다.";
        }
        finally { IsUpdateBusy = false; }
    }

    /// <summary>업데이트를 내려받아 적용한다. 실제 종료·재실행은 runner(메인 창)가 맡는다.</summary>
    public async Task ApplyUpdateAsync(Func<Services.UpdateInfo, IProgress<double>, Task<bool>> runner)
    {
        if (IsUpdateBusy || AvailableUpdate is not { } info) return;
        IsUpdateBusy = true;
        UpdateStatusText = "업데이트를 내려받는 중… 0%";
        var progress = new Progress<double>(p => UpdateStatusText = $"업데이트를 내려받는 중… {p * 100:0}%");
        try
        {
            if (await runner(info, progress))
                UpdateStatusText = "업데이트를 적용하기 위해 SysGlass를 다시 시작합니다.";
            else
                UpdateStatusText = $"새 버전 {info.Version}을 사용할 수 있습니다.";
        }
        finally { IsUpdateBusy = false; }
    }

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
    public bool ShowPagedPool { get => _s.ShowPagedPool; set => Put(_s.ShowPagedPool, value, v => _s.ShowPagedPool = v); }
    public bool ShowNonPagedPool { get => _s.ShowNonPagedPool; set => Put(_s.ShowNonPagedPool, value, v => _s.ShowNonPagedPool = v); }
    public bool ShowPoolGrowth { get => _s.ShowPoolGrowth; set => Put(_s.ShowPoolGrowth, value, v => _s.ShowPoolGrowth = v); }
    public bool ShowUptime { get => _s.ShowUptime; set => Put(_s.ShowUptime, value, v => _s.ShowUptime = v); }
    public bool ShowMemoryStatus { get => _s.ShowMemoryStatus; set => Put(_s.ShowMemoryStatus, value, v => _s.ShowMemoryStatus = v); }

    // ── 메모리 감시 ──
    public bool MemoryWatchEnabled { get => _s.MemoryWatchEnabled; set => Put(_s.MemoryWatchEnabled, value, v => _s.MemoryWatchEnabled = v); }
    public bool MemAlertsEnabled { get => _s.MemAlertsEnabled; set => Put(_s.MemAlertsEnabled, value, v => _s.MemAlertsEnabled = v); }
    public int MemColorScheme { get => _s.MemColorScheme; set => Put(_s.MemColorScheme, Math.Clamp(value, 0, 1), v => _s.MemColorScheme = v); }

    public int MemKernelIntervalSec { get => _s.MemKernelIntervalSec; set => PutInt(_s.MemKernelIntervalSec, value, 1, 60, v => _s.MemKernelIntervalSec = v); }
    public int MemResidentIntervalSec { get => _s.MemResidentIntervalSec; set => PutInt(_s.MemResidentIntervalSec, value, 5, 600, v => _s.MemResidentIntervalSec = v); }
    public int MemPoolTagIntervalSec { get => _s.MemPoolTagIntervalSec; set => PutInt(_s.MemPoolTagIntervalSec, value, 15, 3600, v => _s.MemPoolTagIntervalSec = v); }
    public int MemProcessIntervalSec { get => _s.MemProcessIntervalSec; set => PutInt(_s.MemProcessIntervalSec, value, 15, 3600, v => _s.MemProcessIntervalSec = v); }
    public bool MemLogEnabled { get => _s.MemLogEnabled; set => Put(_s.MemLogEnabled, value, v => _s.MemLogEnabled = v); }
    public int MemRetentionDays { get => _s.MemRetentionDays; set => PutInt(_s.MemRetentionDays, value, 1, 365, v => _s.MemRetentionDays = v); }
    public int MemLogMaxMb { get => _s.MemLogMaxMb; set => PutInt(_s.MemLogMaxMb, value, 10, 4096, v => _s.MemLogMaxMb = v); }
    public int MemUptimeIntervalSec { get => _s.MemUptimeIntervalSec; set => PutInt(_s.MemUptimeIntervalSec, value, 5, 3600, v => _s.MemUptimeIntervalSec = v); }

    // 감지 기준: 주의 < 위험이 되도록(가용 RAM은 주의 > 위험) 반대쪽 값을 넘지 않게 보정한다.
    public int MemPagedWarnMb { get => _s.MemPagedWarnMb; set => PutInt(_s.MemPagedWarnMb, value, AppSettings.PoolMbRange.Min, _s.MemPagedHighMb - 1, v => _s.MemPagedWarnMb = v); }
    public int MemPagedHighMb { get => _s.MemPagedHighMb; set => PutInt(_s.MemPagedHighMb, value, _s.MemPagedWarnMb + 1, AppSettings.PoolMbRange.Max, v => _s.MemPagedHighMb = v); }
    public int MemNonPagedWarnMb { get => _s.MemNonPagedWarnMb; set => PutInt(_s.MemNonPagedWarnMb, value, AppSettings.PoolMbRange.Min, _s.MemNonPagedHighMb - 1, v => _s.MemNonPagedWarnMb = v); }
    public int MemNonPagedHighMb { get => _s.MemNonPagedHighMb; set => PutInt(_s.MemNonPagedHighMb, value, _s.MemNonPagedWarnMb + 1, AppSettings.PoolMbRange.Max, v => _s.MemNonPagedHighMb = v); }
    public int MemPoolTagWarnMb { get => _s.MemPoolTagWarnMb; set => PutInt(_s.MemPoolTagWarnMb, value, AppSettings.TagMbRange.Min, _s.MemPoolTagHighMb - 1, v => _s.MemPoolTagWarnMb = v); }
    public int MemPoolTagHighMb { get => _s.MemPoolTagHighMb; set => PutInt(_s.MemPoolTagHighMb, value, _s.MemPoolTagWarnMb + 1, AppSettings.TagMbRange.Max, v => _s.MemPoolTagHighMb = v); }
    public int MemGrowthWarnMbPerHour { get => _s.MemGrowthWarnMbPerHour; set => PutInt(_s.MemGrowthWarnMbPerHour, value, AppSettings.GrowthRange.Min, _s.MemGrowthHighMbPerHour - 1, v => _s.MemGrowthWarnMbPerHour = v); }
    public int MemGrowthHighMbPerHour { get => _s.MemGrowthHighMbPerHour; set => PutInt(_s.MemGrowthHighMbPerHour, value, _s.MemGrowthWarnMbPerHour + 1, AppSettings.GrowthRange.Max, v => _s.MemGrowthHighMbPerHour = v); }
    public int MemCommitWarnPercent { get => _s.MemCommitWarnPercent; set => PutInt(_s.MemCommitWarnPercent, value, AppSettings.CommitRange.Min, _s.MemCommitHighPercent - 1, v => _s.MemCommitWarnPercent = v); }
    public int MemCommitHighPercent { get => _s.MemCommitHighPercent; set => PutInt(_s.MemCommitHighPercent, value, _s.MemCommitWarnPercent + 1, AppSettings.CommitRange.Max, v => _s.MemCommitHighPercent = v); }
    public int MemAvailWarnPercent { get => _s.MemAvailWarnPercent; set => PutInt(_s.MemAvailWarnPercent, value, _s.MemAvailHighPercent + 1, AppSettings.AvailRange.Max, v => _s.MemAvailWarnPercent = v); }
    public int MemAvailHighPercent { get => _s.MemAvailHighPercent; set => PutInt(_s.MemAvailHighPercent, value, AppSettings.AvailRange.Min, _s.MemAvailWarnPercent - 1, v => _s.MemAvailHighPercent = v); }
    public int MemAlertSustainMin { get => _s.MemAlertSustainMin; set => PutInt(_s.MemAlertSustainMin, value, AppSettings.SustainRange.Min, AppSettings.SustainRange.Max, v => _s.MemAlertSustainMin = v); }
    public int MemAlertRepeatMin { get => _s.MemAlertRepeatMin; set => PutInt(_s.MemAlertRepeatMin, value, AppSettings.RepeatRange.Min, AppSettings.RepeatRange.Max, v => _s.MemAlertRepeatMin = v); }

    /// <summary>정수 설정을 범위로 보정해 저장한다. 보정 결과가 현재 값과 같아도 화면 입력값을 되돌리도록 알린다.</summary>
    private void PutInt(int cur, int value, int min, int max, Action<int> assign, [CallerMemberName] string? prop = null)
    {
        value = Math.Clamp(value, min, Math.Max(min, max));
        if (value == cur) { Raise(prop); return; }
        assign(value);
        Raise(prop);
        _main.NotifySettingsChanged(prop ?? string.Empty);
    }

    // ── 화면 및 디자인 ──
    /// <summary>불투명도(%). 20~100</summary>
    public double OpacityPercent
    {
        get => Math.Round(_s.BackgroundOpacity * 100);
        set => Put(_s.BackgroundOpacity, Math.Clamp(Math.Round(value) / 100.0, 0.2, 1.0), v => _s.BackgroundOpacity = v);
    }

    public bool CompactMode { get => _s.CompactMode; set => Put(_s.CompactMode, value, v => _s.CompactMode = v); }
    public int CompactStyle { get => _s.CompactStyle; set => Put(_s.CompactStyle, Math.Clamp(value, 0, 1), v => _s.CompactStyle = v); }
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
        LoadGroupOrder();
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

    public void Dispose()
    {
        _controller.ControllerStateChanged -= OnControllerChanged;
    }
}

/// <summary>설정 창의 그룹 순서 목록 한 줄.</summary>
public sealed record GroupOrderItem(string Key, string Title);
