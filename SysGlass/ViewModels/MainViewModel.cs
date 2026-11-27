using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using SysGlass.Models;
using SysGlass.Services;
using SysGlass.Services.Memory;
using SysGlass.Settings;
using SysGlass.Utilities;

namespace SysGlass.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private const double GiB = 1024.0 * 1024 * 1024;
    private const int MinSampleGapMs = 250; // 설정을 빠르게 바꿔도 수집이 몰리지 않도록 하는 최소 간격

    private readonly ISystemMonitor _monitor;
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _wakeLock = new();
    private CancellationTokenSource? _wake;
    private bool _wakeRequested;
    private bool _started;

    private SystemSnapshot? _lastSnapshot;
    private SensorOptions? _lastOptions;
    private volatile bool _needDeviceScan;

    private double _cpuPercent;
    private string _cpuText = Format.NA, _cpuTempText = Format.NA, _cpuClockText = Format.NA, _cpuName = "";
    private Brush _cpuTempBrush = TempBrushes.Normal;
    private double _ramPercent;
    private string _ramText = Format.NA, _netDownText = Format.NA, _netUpText = Format.NA;
    private string _sensorNotice = "", _cpuPowerText = Format.NA;
    private Brush _backgroundBrush;

    public MainViewModel(ISystemMonitor monitor, AppSettings settings)
    {
        _monitor = monitor;
        _settings = settings;
        TempBrushes.ApplyTextColor(settings.TextColor);
        MemoryService = new MemoryWatchService(settings);
        Trace = new PrecisionTraceService(Path.Combine(MemoryService.LogDirectory, "trace"));
        MemoryService.Updated += state =>
            Application.Current?.Dispatcher.BeginInvoke(() => { if (!_cts.IsCancellationRequested) Memory.Update(state, _settings.MemColorScheme); });
        _backgroundBrush = BuildBrush(settings.BackgroundOpacity);
        GpuSelection = new DeviceSelectionViewModel(() => _settings.SelectedGpus, v => _settings.SelectedGpus = v,
            () => NotifySettingsChanged(nameof(AppSettings.SelectedGpus)));
        DiskSelection = new DeviceSelectionViewModel(() => _settings.SelectedDisks, v => _settings.SelectedDisks = v,
            () => NotifySettingsChanged(nameof(AppSettings.SelectedDisks)));
    }

    public AppSettings Settings => _settings;
    public DeviceSelectionViewModel GpuSelection { get; }
    public DeviceSelectionViewModel DiskSelection { get; }
    public MemoryWatchService MemoryService { get; }
    /// <summary>사용자 동의 후에만 실행하는 ETW 정밀 추적. 앱 종료 시 진행 중인 자기 기록을 취소한다.</summary>
    public PrecisionTraceService Trace { get; }
    public MemoryOverlayViewModel Memory { get; } = new();

    /// <summary>설정이 바뀌었을 때(저장·창 반영용). 인자는 바뀐 설정 이름.</summary>
    public event Action<string>? SettingChanged;

    // ── 표시 항목 (설정에서 켜고 끔) ──
    public bool ShowCpuUsage => _settings.ShowCpuUsage;
    public bool ShowCpuTemp => _settings.ShowCpuTemp;
    public bool ShowCpuClock => _settings.ShowCpuClock;
    public bool ShowCpuPower => _settings.ShowCpuPower;
    public bool ShowRamUsage => _settings.ShowRamUsage;
    public bool ShowGpuUsage => _settings.ShowGpuUsage;
    public bool ShowGpuTemp => _settings.ShowGpuTemp;
    public bool ShowVram => _settings.ShowVram;
    public bool ShowDiskUsage => _settings.ShowDiskUsage;
    public bool ShowDiskTemp => _settings.ShowDiskTemp;
    public bool ShowDiskIo => _settings.ShowDiskRead || _settings.ShowDiskWrite;
    public bool ShowNetDown => _settings.ShowNetDown;
    public bool ShowNetUp => _settings.ShowNetUp;
    public bool ShowNetwork => ShowNetDown || ShowNetUp;
    public bool ShowNetBoth => ShowNetDown && ShowNetUp;
    public bool ShowPagedPool => _settings.ShowPagedPool;
    public bool ShowNonPagedPool => _settings.ShowNonPagedPool;
    public bool ShowPoolGrowth => _settings.ShowPoolGrowth;
    public bool ShowUptime => _settings.ShowUptime;
    public bool ShowMemoryStatus => _settings.ShowMemoryStatus;
    public bool ShowKernelMemory => ShowPagedPool || ShowNonPagedPool || ShowPoolGrowth || ShowUptime || ShowMemoryStatus;

    public bool ShowCpuSubRow => ShowCpuTemp || ShowCpuClock || ShowCpuPower;
    public bool ShowCpuBlock => ShowCpuUsage || ShowCpuSubRow;
    public bool ShowGpuHeader => ShowGpuUsage || ShowGpuTemp;
    public bool ShowGpuBoth => ShowGpuUsage && ShowGpuTemp;
    public bool ShowGpuSection => ShowGpuUsage || ShowGpuTemp || ShowVram;
    public bool ShowDiskBoth => ShowDiskUsage && ShowDiskTemp;
    public bool ShowDiskSection => ShowDiskUsage || ShowDiskTemp || ShowDiskIo;

    // ── 컴팩트 보기: 위젯형 칸(배터리 캡슐·추이 그래프·LED 미터·아크 게이지)으로 표시 ──
    public bool CompactMode => _settings.CompactMode;
    public bool ShowNormalView => !CompactMode;
    /// <summary>1 = 배터리 캡슐, 2 = 추이 그래프, 3 = LED 미터, 4 = 아크 게이지</summary>
    public int CompactStyle => _settings.CompactStyle;
    /// <summary>아크 게이지 보기의 한 줄 칸 수(2~6).</summary>
    public int ArcColumns => _settings.ArcColumns;
    /// <summary>아크 게이지 한 칸의 기본 크기(px). 칸이 적을수록 크게 그린다. 창이 좁으면 XAML에서 더 줄인다.</summary>
    public double ArcTileSize => _settings.ArcColumns switch { 2 => 80, 4 => 52, 5 => 44, 6 => 38, _ => 62 };
    /// <summary>
    /// 아크 게이지 가운데 값이 차지할 수 있는 최대 폭. 호 안쪽 지름(크기 − 두께·눈금 자리 16px)의 80%로,
    /// 작은 게이지에서도 글자가 호에 닿지 않게 한다.
    /// </summary>
    public double ArcValueWidth => Math.Max(10, (ArcTileSize - 16) * 0.8);
    public ObservableCollection<CompactTileViewModel> CompactTiles { get; } = [];

    public bool HasAnything => ShowCpuBlock || ShowRamUsage || ShowGpuSection || ShowDiskSection || ShowNetwork || ShowKernelMemory;
    public bool IsEmpty => !HasAnything;

    /// <summary>글자 크기 배율 (작게/보통/크게)</summary>
    public double FontScale => _settings.FontSizeLevel switch { 0 => 0.9, 2 => 1.2, _ => 1.0 };

    /// <summary>창 전체 Opacity 대신 배경 브러시의 알파만 조절해 글자는 선명하게 유지한다.</summary>
    public Brush BackgroundBrush { get => _backgroundBrush; private set => Set(ref _backgroundBrush, value); }

    public double CpuPercent { get => _cpuPercent; private set => Set(ref _cpuPercent, value); }
    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value); }
    public string CpuTempText { get => _cpuTempText; private set => Set(ref _cpuTempText, value); }
    public Brush CpuTempBrush { get => _cpuTempBrush; private set => Set(ref _cpuTempBrush, value); }
    public string CpuClockText { get => _cpuClockText; private set => Set(ref _cpuClockText, value); }
    public string CpuPowerText { get => _cpuPowerText; private set => Set(ref _cpuPowerText, value); }
    public string SensorNotice { get => _sensorNotice; private set => Set(ref _sensorNotice, value); }
    public string CpuName { get => _cpuName; private set => Set(ref _cpuName, value); }
    public double RamPercent { get => _ramPercent; private set => Set(ref _ramPercent, value); }
    public string RamText { get => _ramText; private set => Set(ref _ramText, value); }
    public string NetDownText { get => _netDownText; private set => Set(ref _netDownText, value); }
    public string NetUpText { get => _netUpText; private set => Set(ref _netUpText, value); }

    public ObservableCollection<GpuItemViewModel> Gpus { get; } = [];
    public ObservableCollection<DiskItemViewModel> Disks { get; } = [];

    /// <summary>설정 창이 열려 있는 동안에는 표시 여부와 관계없이 GPU/디스크를 감지한다.</summary>
    public bool NeedDeviceScan
    {
        get => _needDeviceScan;
        set { _needDeviceScan = value; if (_started) Wake(); }
    }

    private static Brush BuildBrush(double opacity)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), 0x1C, 0x1C, 0x1F));
        b.Freeze();
        return b;
    }

    /// <summary>
    /// 설정이 바뀌었음을 알린다. 모든 바인딩을 갱신하고, 마지막 수집값으로 즉시 다시 그리며,
    /// 센서 조회에 영향을 주는 변경이면 수집 루프를 깨운다.
    /// </summary>
    public void NotifySettingsChanged(string name)
    {
        BackgroundBrush = BuildBrush(_settings.BackgroundOpacity);
        TempBrushes.ApplyTextColor(_settings.TextColor); // 아래 Apply에서 수치 색을 바로 다시 고른다.
        Raise(string.Empty); // 모든 속성 갱신
        if (_lastSnapshot is { } s) Apply(s, _lastOptions);
        Memory.Refresh(_settings.MemColorScheme);
        if (_started && (name.StartsWith("Show") || name.StartsWith("Selected") || name == nameof(AppSettings.RefreshIntervalMs)))
            Wake();
        if (_started && (name.StartsWith("Mem") || name == "Reset")) MemoryService.Wake();
        SettingChanged?.Invoke(name);
    }

    private SensorOptions BuildOptions() => new(
        Cpu: ShowCpuTemp || ShowCpuClock || ShowCpuPower,
        Gpu: ShowGpuSection || _needDeviceScan,
        Disk: ShowDiskSection || _needDeviceScan,
        Network: ShowNetwork);

    /// <summary>대기 중인 수집 루프를 깨워 다음 수집을 앞당긴다.</summary>
    private void Wake()
    {
        lock (_wakeLock)
        {
            _wakeRequested = true;
            try { _wake?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// 수집 루프는 하나만 실행된다. 갱신 주기는 매 반복마다 설정에서 읽으므로
    /// 주기를 바꿔도 타이머/작업을 새로 만들지 않는다.
    /// </summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        var token = _cts.Token;
        MemoryService.Start();
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var options = BuildOptions();
                    var snap = _monitor.Sample(options);
                    Application.Current?.Dispatcher.BeginInvoke(() => Apply(snap, options));
                }
                catch (Exception ex) { Logger.Error("수집 실패", ex); }

                CancellationTokenSource? wake = null;
                int delay = MinSampleGapMs;
                lock (_wakeLock)
                {
                    if (_wakeRequested) _wakeRequested = false; // 대기 없이 최소 간격 후 바로 다음 수집
                    else
                    {
                        _wake?.Dispose();
                        wake = _wake = CancellationTokenSource.CreateLinkedTokenSource(token);
                        delay = _settings.RefreshIntervalMs;
                    }
                }

                try { await Task.Delay(delay, wake?.Token ?? token); }
                catch (OperationCanceledException)
                {
                    if (token.IsCancellationRequested) break;
                    try { await Task.Delay(MinSampleGapMs, token); } catch (OperationCanceledException) { break; }
                }
            }
        }, token);
    }

    private void Apply(SystemSnapshot s, SensorOptions? options)
    {
        _lastSnapshot = s;
        _lastOptions = options;

        if (s.CpuUsagePercent is { } cpu)
        {
            CpuPercent = cpu;
            CpuText = Format.Percent(cpu);
        }
        CpuTempText = Format.Temp(s.CpuTempC);
        CpuTempBrush = TempBrushes.For(s.CpuTempC, _settings.CpuTempWarn, _settings.CpuTempHigh);
        CpuClockText = s.CpuClockIsEstimate ? Format.Clock(s.CpuClockMhz) + " (계산)" : Format.Clock(s.CpuClockMhz);
        CpuPowerText = s.CpuPowerW is { } w ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} W", w) : Format.NA;
        SensorNotice = s.SensorNotice ?? "";
        if (s.CpuName is { Length: > 0 } name) CpuName = name;

        if (s.MemoryUsagePercent is { } ramPct && s.MemoryTotalBytes is { } total && s.MemoryUsedBytes is { } used)
        {
            RamPercent = ramPct;
            RamText = string.Format(CultureInfo.InvariantCulture, "{0:0.0} / {1:0.#} GB ({2:0}%)",
                used / GiB, total / GiB, ramPct);
        }
        else
        {
            RamPercent = 0;
            RamText = Format.NA;
        }

        // GPU/디스크: 감지 목록은 설정 화면용으로 갱신하고, 화면에는 선택된 장치만 표시한다.
        if (options is null || options.Gpu)
        {
            GpuSelection.UpdateDetected(s.Gpus.Select((g, i) => (g.Id, g.Name, $"GPU {i}: {g.Name}")).ToList());
            var all = s.Gpus.Select(g => (g.Id, g.Name)).ToList();
            var shown = s.Gpus.Where(g => ShowGpuSection && GpuSelection.IsIncluded(g.Id, g.Name, all)).ToList();
            Sync(Gpus, shown, g => g.Id, id => new GpuItemViewModel(id, _settings), (vm, g) => vm.Update(g), vm => vm.Id);
        }
        else Gpus.Clear();

        if (options is null || options.Disk)
        {
            DiskSelection.UpdateDetected(s.Disks.Select(d => (d.Id, d.Name, Format.DiskLabel(d.Number, d.Kind, d.Name))).ToList());
            var all = s.Disks.Select(d => (d.Id, d.Name)).ToList();
            var shown = s.Disks.Where(d => ShowDiskSection && DiskSelection.IsIncluded(d.Id, d.Name, all)).ToList();
            Sync(Disks, shown, d => d.Id, id => new DiskItemViewModel(id, _settings), (vm, d) => vm.Update(d), vm => vm.Id);
        }
        else Disks.Clear();

        NetDownText = Format.Rate(s.NetDownBytesPerSec);
        NetUpText = Format.Rate(s.NetUpBytesPerSec);

        UpdateCompactTiles(s);
    }

    private static readonly Brush CpuAccent = FrozenBrush(0x4C, 0xC2, 0xFF);
    private static readonly Brush RamAccent = FrozenBrush(0x9C, 0xDB, 0x7F);
    private static readonly Brush GpuAccent = FrozenBrush(0xC7, 0x9B, 0xFF);
    private static readonly Brush VramAccent = FrozenBrush(0xE6, 0xA3, 0xFF);
    private static readonly Brush DiskAccent = FrozenBrush(0xFF, 0xB8, 0x6B);

    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private sealed record TileData(string Id, string Label, string ValueText, Brush ValueBrush, double Percent,
        Brush Accent, string SubText, Brush SubBrush, string ToolTip);

    /// <summary>
    /// 컴팩트 보기의 칸 목록을 맞춘다. 사용률을 끈 항목은 온도를 주 값으로 보여 주고 게이지는 비운다.
    /// 클럭·전력·디스크 I/O는 컴팩트 보기에서 툴팁으로만 제공한다.
    /// </summary>
    private readonly Dictionary<string, Queue<double>> _tileHistory = [];

    private void UpdateCompactTiles(SystemSnapshot s)
    {
        if (!CompactMode) { CompactTiles.Clear(); _tileHistory.Clear(); return; }
        var normal = TempBrushes.Normal;
        var tiles = new List<TileData>();

        if (ShowCpuUsage || ShowCpuTemp)
        {
            string tip = string.Join("\n", new[] { CpuName, ShowCpuClock ? "클럭 " + CpuClockText : "", ShowCpuPower ? "전력 " + CpuPowerText : "" }
                .Where(x => x.Length > 0));
            tiles.Add(ShowCpuUsage
                ? new("cpu", "CPU", CpuText, normal, CpuPercent, CpuAccent, ShowCpuTemp ? CpuTempText : "", CpuTempBrush, tip)
                : new("cpu", "CPU", CpuTempText, CpuTempBrush, 0, CpuAccent, "", normal, tip));
        }

        if (ShowRamUsage)
        {
            string used = s.MemoryUsedBytes is { } u ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", u / GiB) : "";
            tiles.Add(new("ram", "RAM", s.MemoryUsagePercent is { } p ? Format.Percent(p) : Format.NA, normal, RamPercent,
                RamAccent, used, normal, RamText));
        }

        for (int i = 0; i < Gpus.Count; i++)
        {
            var g = Gpus[i];
            string label = Gpus.Count > 1 ? $"GPU {i}" : "GPU";
            if (ShowGpuUsage || ShowGpuTemp)
                tiles.Add(ShowGpuUsage
                    ? new("gpu:" + g.Id, label, g.UsageText, normal, g.Usage, GpuAccent, ShowGpuTemp ? g.TempText : "", g.TempBrush, g.Name)
                    : new("gpu:" + g.Id, label, g.TempText, g.TempBrush, 0, GpuAccent, "", normal, g.Name));
            if (ShowVram)
            {
                // 모든 보기에서 사용 중인 VRAM 용량(GB)을 보조 값으로 함께 보여 준다.
                string vramSub = g.VramUsedMb is { } mb
                    ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", mb / 1024)
                    : "";
                tiles.Add(new("vram:" + g.Id, Gpus.Count > 1 ? $"VRAM {i}" : "VRAM", Format.Percent(g.VramPercent), normal,
                    g.VramPercent, VramAccent, vramSub, normal, $"{g.Name}\n{g.VramText}"));
            }
        }

        foreach (var d in Disks)
        {
            if (!ShowDiskUsage && !ShowDiskTemp) continue;
            // 칸이 좁으므로 "DISK 0 (SSD)"에서 종류는 툴팁으로 옮긴다.
            int paren = d.Title.IndexOf(" (", StringComparison.Ordinal);
            string label = paren > 0 ? d.Title[..paren] : d.Title;
            string tip = ShowDiskIo ? $"{d.Title} · {d.Model}\n{d.IoText}" : $"{d.Title} · {d.Model}";
            tiles.Add(ShowDiskUsage
                ? new("disk:" + d.Id, label, d.UsageText, normal, d.Usage, DiskAccent, ShowDiskTemp ? d.TempText : "", d.TempBrush, tip)
                : new("disk:" + d.Id, label, d.TempText, d.TempBrush, 0, DiskAccent, "", normal, tip));
        }

        // 설정의 그룹 순서를 따른다(VRAM은 GPU 그룹). OrderBy는 안정 정렬이라 그룹 안의 순서는 유지된다.
        var order = AppSettings.NormalizeGroupOrder(_settings.GroupOrder);
        tiles = tiles.OrderBy(t => order.IndexOf(t.Id.Split(':')[0] switch { "vram" => "gpu", var k => k })).ToList();

        // 순서가 바뀌었으면 다시 만들고, 같으면 값만 갱신해 깜빡임을 피한다.
        if (!CompactTiles.Select(t => t.Id).SequenceEqual(tiles.Select(t => t.Id)))
        {
            CompactTiles.Clear();
            foreach (var t in tiles) CompactTiles.Add(new CompactTileViewModel(t.Id));
        }
        // 추이 그래프용 기록. 칸을 다시 만들어도 유지되도록 Id별로 따로 보관하고, 사라진 칸의 기록은 버린다.
        foreach (var gone in _tileHistory.Keys.Where(k => !tiles.Any(t => t.Id == k)).ToList())
            _tileHistory.Remove(gone);
        for (int i = 0; i < tiles.Count; i++)
        {
            var t = tiles[i];
            if (!_tileHistory.TryGetValue(t.Id, out var hist)) _tileHistory[t.Id] = hist = [];
            hist.Enqueue(double.IsFinite(t.Percent) ? t.Percent : 0);
            while (hist.Count > CompactTileViewModel.HistoryLength) hist.Dequeue();
            CompactTiles[i].Update(t.Label, t.ValueText, t.ValueBrush, t.Percent, t.Accent, t.SubText, t.SubBrush, t.ToolTip,
                hist.ToArray());
        }
    }

    /// <summary>장치가 추가/제거되면 목록을 맞추고, 있는 항목은 값만 갱신한다.</summary>
    private static void Sync<TVm, TInfo>(ObservableCollection<TVm> list, IReadOnlyList<TInfo> infos,
        Func<TInfo, string> infoId, Func<string, TVm> create, Action<TVm, TInfo> update, Func<TVm, string> vmId)
    {
        for (int i = list.Count - 1; i >= 0; i--)
            if (!infos.Any(x => infoId(x) == vmId(list[i]))) list.RemoveAt(i);
        foreach (var info in infos)
        {
            var vm = list.FirstOrDefault(x => vmId(x) == infoId(info));
            if (vm is null) { vm = create(infoId(info)); list.Add(vm); }
            update(vm, info);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        Trace.Dispose();
        MemoryService.Dispose();
        (_monitor as IDisposable)?.Dispose();
    }
}
