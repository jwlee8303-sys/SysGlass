using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Diagnostics;
using System.IO;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using SysGlass.Services;
using SysGlass.Settings;
using SysGlass.Utilities;
using SysGlass.ViewModels;
using SysGlass.Views;

namespace SysGlass
{
    public partial class MainWindow : Window, IMainWindowController
    {
        private readonly SettingsService _settingsService = new();
        private readonly AppSettings _settings;
        private readonly MainViewModel _vm;
        private readonly DispatcherTimer _saveTimer;

        private readonly TrayService _tray = new();
        private readonly HotKeyService _hotKey = new();
        private bool _clickThrough;
        private SettingsWindow? _settingsWindow;
        private ProcessWindow? _processWindow;
        private MemoryWindow? _memoryWindow;
        private PrecisionTraceWindow? _traceWindow;
        private bool _systemToolIconsLoaded;
        private bool _closingTrace;

        public event Action? ControllerStateChanged;

        public MainWindow()
        {
            _settings = _settingsService.Load();
            _vm = new MainViewModel(new WindowsSystemMonitor(), _settings);
            _vm.SettingChanged += OnSettingChanged;
            DataContext = _vm;

            // 슬라이더 등 빈번한 변경 시 디스크 쓰기를 줄이기 위해 저장을 모아서 수행한다.
            _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _settingsService.Save(_settings); };

            InitializeComponent();
            ApplyFontResources();
            ApplyTextColorResources();
            ApplyGroupOrder();

            Topmost = _settings.AlwaysOnTop;
            Width = _settings.WindowWidth;
            RestorePosition();

            Loaded += (_, _) => { _vm.Start(); ClampToWorkArea(); };
            Loaded += async (_, _) => await LoadSystemToolIconsAsync();
            Loaded += async (_, _) => await CheckUpdateOnStartupAsync();
            // 창 핸들이 만들어진 뒤에만 트레이/단축키/Win32 스타일을 다룬다.
            SourceInitialized += OnSourceInitialized;
            IsVisibleChanged += (_, _) => { ApplyClickThrough(); UpdateDisplayActivity(); };
            StateChanged += (_, _) => UpdateDisplayActivity();
            SizeChanged += (_, e) => { if (e.HeightChanged) ClampToWorkArea(); };
            Closing += OnClosing;
            SystemEvents_Subscribe();
        }

        private void UpdateDisplayActivity() => _vm.SetDisplayActive(IsVisible && WindowState != WindowState.Minimized);

        private IntPtr Hwnd => new System.Windows.Interop.WindowInteropHelper(this).Handle;

        /// <summary>시작 후 백그라운드에서 최신 버전을 확인하고, 새 버전이 있으면 업데이트 여부를 묻는다. 오프라인·시간 초과는 조용히 넘어간다.</summary>
        private async Task CheckUpdateOnStartupAsync()
        {
            await Task.Run(UpdateService.CleanupLeftovers);
            if (!UpdateService.CanApply) return; // 개발 빌드에서는 묻지 않는다.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                var info = await UpdateService.CheckAsync(TimeSpan.FromSeconds(10));
                if (info == null || !IsLoaded) return;

                var summary = UpdateService.SummarizeNotes(info.Notes, 800);
                var message = $"새 버전이 있습니다.\n\n현재 버전: {UpdateService.CurrentVersion}\n새 버전: {info.Version}"
                    + (summary.Length > 0 ? $"\n\n변경 요약:\n{summary}" : "")
                    + "\n\n지금 업데이트할까요?\n[예] 지금 업데이트 (SysGlass가 종료된 뒤 새 버전으로 다시 실행됩니다)\n[아니요] 나중에";
                if (MessageBox.Show(this, message, "SysGlass 업데이트", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No)
                    != MessageBoxResult.Yes) return;

                _tray.ShowNotification("SysGlass 업데이트", $"{info.Version} 버전을 내려받는 중입니다. 완료되면 자동으로 다시 실행됩니다.", warning: false);
                await RunUpdateAsync(info, this, progress: null);
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                // 오프라인·응답 지연: 기존 기능에 영향 없이 넘어간다.
            }
            catch (Exception ex)
            {
                Logger.Error("시작 시 업데이트 확인 실패", ex);
            }
        }

        /// <summary>
        /// 업데이트 파일을 준비하고 앱을 종료한다(교체 후 자동 재실행). 진행 중인 정밀 추적이 있으면 먼저 확인한다.
        /// 실패하면 안내 후 false. 설정 창의 [지금 업데이트]와 시작 시 확인이 함께 사용한다.
        /// </summary>
        public async Task<bool> RunUpdateAsync(UpdateInfo info, Window owner, IProgress<double>? progress)
        {
            if (_vm.Trace.State != Services.Memory.PrecisionTraceService.TraceState.Idle &&
                MessageBox.Show(owner, "정밀 추적이 진행 중입니다. 업데이트하면 기록이 저장되지 않고 중단됩니다. 계속할까요?",
                    "SysGlass 업데이트", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return false;
            try
            {
                if (_vm.Trace.State != Services.Memory.PrecisionTraceService.TraceState.Idle)
                {
                    await _vm.Trace.StopAsync(save: false);
                    if (_vm.Trace.State != Services.Memory.PrecisionTraceService.TraceState.Idle)
                    {
                        ShowPrecisionTrace();
                        return false;
                    }
                }
                if (!await UpdateService.PrepareAndLaunchAsync(info, progress)) return false; // 이미 진행 중
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, $"업데이트를 진행할 수 없습니다. 현재 버전을 계속 사용합니다.\n\n{ex.Message}",
                    "SysGlass 업데이트", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            Close(); // 설정 저장 후 종료하면 교체 스크립트가 새 버전을 실행한다.
            return true;
        }

        private async Task LoadSystemToolIconsAsync()
        {
            if (_systemToolIconsLoaded) return;
            _systemToolIconsLoaded = true;
            var icons = await Task.Run(() =>
            {
                string directory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var taskManager = ExtractToolIcon(Path.Combine(directory, "Taskmgr.exe"), 0);
                BitmapSource? computerManagement = null;
                try
                {
                    // Use the resource selected by the console itself, rather than the generic MMC file icon.
                    using var reader = XmlReader.Create(Path.Combine(directory, "compmgmt.msc"),
                        new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                    var icon = XDocument.Load(reader).Descendants().FirstOrDefault(e =>
                        e.Name.LocalName == "Icon" && e.Parent?.Name.LocalName == "VisualAttributes");
                    if (icon?.Attribute("File")?.Value is { } file && int.TryParse(icon.Attribute("Index")?.Value, out int index))
                        computerManagement = ExtractToolIcon(Environment.ExpandEnvironmentVariables(file), index);
                }
                catch (Exception ex) { Logger.Error("컴퓨터 관리 아이콘 로딩 실패", ex); }
                return (TaskManager: taskManager, ComputerManagement: computerManagement);
            });
            if (icons.TaskManager is { } manager) TaskManagerIcon.Source = manager;
            if (icons.ComputerManagement is { } management) ComputerManagementIcon.Source = management;
        }

        private static BitmapSource? ExtractToolIcon(string file, int index)
        {
            IntPtr icon = IntPtr.Zero;
            try
            {
                if (NativeMethods.ExtractIconEx(file, index, IntPtr.Zero, out icon, 1) != 1 || icon == IntPtr.Zero) return null;
                var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex) { Logger.Error("Windows 앱 아이콘 로딩 실패", ex); return null; }
            finally { if (icon != IntPtr.Zero) NativeMethods.DestroyIcon(icon); }
        }

        private void OnOpenTaskManager(object sender, RoutedEventArgs e) => OpenSystemTool("Taskmgr.exe", "작업 관리자");
        private void OnOpenComputerManagement(object sender, RoutedEventArgs e) => OpenSystemTool("compmgmt.msc", "컴퓨터 관리");

        private async void OpenSystemTool(string file, string title)
        {
            try
            {
                await Task.Run(() =>
                {
                    string directory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    bool console = file.EndsWith(".msc", StringComparison.OrdinalIgnoreCase);
                    var startInfo = new ProcessStartInfo(Path.Combine(directory, console ? "mmc.exe" : file)) { UseShellExecute = true };
                    if (console) startInfo.ArgumentList.Add(Path.Combine(directory, file));
                    using var process = Process.Start(startInfo);
                });
            }
            catch (Exception ex)
            {
                Logger.Error($"{title} 실행 실패", ex);
                MessageBox.Show(this, $"{title}를 실행할 수 없습니다. Windows의 실행 권한과 파일 상태를 확인하세요.",
                    title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ───────── 설정 반영 ─────────

        private void OnSettingChanged(string name)
        {
            if (name is nameof(AppSettings.FontSizeLevel) or "Reset") ApplyFontResources();
            if (name is nameof(AppSettings.TextColor) or "Reset") ApplyTextColorResources();
            if (name is nameof(AppSettings.GroupOrder) or "Reset") ApplyGroupOrder();
            if (name is nameof(AppSettings.WindowWidth) or "Reset")
            {
                Width = Math.Clamp(_settings.WindowWidth, AppSettings.MinWidth, AppSettings.MaxWidth);
                ClampToWorkArea();
            }
            RequestSave();
        }

        private void RequestSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>
        /// 설정의 그룹 순서대로 오버레이 블록을 재배치한다. 컴팩트 보기의 게이지 묶음은
        /// 게이지 그룹(CPU·RAM·GPU·디스크) 중 가장 앞 그룹의 자리에 놓는다.
        /// </summary>
        private void ApplyGroupOrder()
        {
            var blocks = new Dictionary<string, UIElement>
            {
                ["cpu"] = GroupCpu, ["ram"] = GroupRam, ["memory"] = GroupMemory,
                ["gpu"] = GroupGpu, ["disk"] = GroupDisk, ["net"] = GroupNet,
            };
            var ordered = new List<UIElement>();
            foreach (var key in AppSettings.NormalizeGroupOrder(_settings.GroupOrder))
            {
                if (key is "cpu" or "ram" or "gpu" or "disk" && !ordered.Contains(CompactTilesPanel))
                    ordered.Add(CompactTilesPanel);
                ordered.Add(blocks[key]);
            }

            // 안내 문구 등 순서 대상이 아닌 요소는 앞쪽에 그대로 두고, 그 뒤에 순서대로 다시 넣는다.
            foreach (var e in ordered) GroupHost.Children.Remove(e);
            foreach (var e in ordered) GroupHost.Children.Add(e);
        }

        /// <summary>글자 크기 단계에 맞게 크기 리소스를 갱신한다. 레이아웃은 자동으로 다시 계산된다.</summary>
        private void ApplyFontResources()
        {
            double s = _vm.FontScale;
            Resources["FsBase"] = Math.Round(13 * s * 2) / 2;
            Resources["FsLabel"] = Math.Round(11 * s * 2) / 2;
            Resources["FsLarge"] = Math.Round(16 * s * 2) / 2;
        }

        /// <summary>설정의 글자 색을 본문(TextBrush)과 항목 이름(LabelBrush) 리소스에 반영한다.</summary>
        private void ApplyTextColorResources()
        {
            Resources["TextBrush"] = TempBrushes.Normal;
            Resources["LabelBrush"] = TempBrushes.Label;
        }

        // ───────── IMainWindowController ─────────

        bool IMainWindowController.AlwaysOnTop
        {
            get => Topmost;
            set => SetTopmost(value);
        }

        bool IMainWindowController.ClickThrough
        {
            get => _clickThrough;
            set => SetClickThrough(value, save: true);
        }

        void IMainWindowController.ApplyAfterReset(bool includeWindow)
        {
            Topmost = _settings.AlwaysOnTop;
            SetClickThrough(_settings.IsClickThroughEnabled, save: false);
            if (includeWindow)
            {
                Width = _settings.WindowWidth;
                ResetPosition();
            }
            ControllerStateChanged?.Invoke();
        }

        // ───────── 트레이 / 단축키 / 클릭 통과 ─────────

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            // 트레이(복구 수단)가 준비된 뒤에만 클릭 통과를 적용한다.
            _tray.GetTopmost = () => Topmost;
            _tray.GetClickThrough = () => _clickThrough;
            _tray.Initialize(ToggleVisible, () => SetTopmost(!Topmost), () => SetClickThrough(!_clickThrough, true),
                ShowSettings, () => Close(),
                [("메모리 상세 분석...", ShowMemoryAnalysis), ("진단 기록 열기", OpenMemoryLogs), ("정밀 추적 시작...", ShowPrecisionTrace),
                 ("메모리 감시 설정...", ShowMemorySettings)]);
            _vm.MemoryService.AlertRaised += OnMemoryAlert;
            _vm.Trace.Finished += OnTraceFinished;
            _hotKey.Register(Hwnd, () => SetClickThrough(!_clickThrough, true));

            if (_settings.IsClickThroughEnabled)
            {
                if (_tray.IsReady || _hotKey.IsRegistered) SetClickThrough(true, save: false);
                else _settings.IsClickThroughEnabled = false; // 복구 수단이 없으면 켜지 않는다.
            }
        }

        private void ToggleVisible()
        {
            if (IsVisible) Hide();
            else { Show(); WindowState = WindowState.Normal; }
        }

        private void SetTopmost(bool on)
        {
            Topmost = on;
            _settings.AlwaysOnTop = on;
            if (_settingsWindow != null) _settingsWindow.Topmost = on;
            if (_processWindow != null) _processWindow.Topmost = on;
            if (_memoryWindow != null) _memoryWindow.Topmost = on;
            if (_traceWindow != null) _traceWindow.Topmost = on;
            RequestSave();
            ControllerStateChanged?.Invoke();
        }

        /// <summary>클릭 통과 전환. 복구 수단(트레이/단축키)이 없으면 켜지 않고, 오류 시 해제한다.</summary>
        private void SetClickThrough(bool enable, bool save)
        {
            if (enable && !_tray.IsReady && !_hotKey.IsRegistered)
            {
                Logger.Error("복구 수단이 없어 클릭 통과를 켤 수 없음");
                enable = false;
            }
            _clickThrough = enable;
            ApplyClickThrough();
            _settings.IsClickThroughEnabled = _clickThrough;
            if (save) RequestSave();
            ControllerStateChanged?.Invoke();
        }

        private void ApplyClickThrough()
        {
            var hwnd = Hwnd;
            if (hwnd == IntPtr.Zero) return; // 핸들 생성 전에는 스타일을 건드리지 않는다.
            try
            {
                if (!WindowStyleHelper.SetClickThrough(hwnd, _clickThrough))
                    throw new InvalidOperationException("확장 스타일 변경 실패");
            }
            catch (Exception ex)
            {
                Logger.Error("클릭 통과 적용 실패, 해제함", ex);
                _clickThrough = false;
                _settings.IsClickThroughEnabled = false;
                try { WindowStyleHelper.SetClickThrough(hwnd, false); } catch { /* 무시 */ }
            }
        }

        // ───────── 설정 창 (중복 생성 방지) ─────────

        private void ShowSettings()
        {
            try
            {
                if (_settingsWindow != null)
                {
                    if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
                    _settingsWindow.Activate();
                    return;
                }
                var vm = new SettingsViewModel(_vm, this);
                var win = new SettingsWindow(vm) { Topmost = Topmost };
                _settingsWindow = win;
                void UpdateDeviceScan() => _vm.NeedDeviceScan = win.IsVisible && win.WindowState != WindowState.Minimized;
                win.IsVisibleChanged += (_, _) => UpdateDeviceScan();
                win.StateChanged += (_, _) => UpdateDeviceScan();
                win.Closed += (_, _) =>
                {
                    _settingsWindow = null;
                    _vm.NeedDeviceScan = false;
                };
                win.Show();
                win.Activate();
            }
            catch (Exception ex)
            {
                Logger.Error("설정 창 열기 실패", ex);
                _settingsWindow = null;
                _vm.NeedDeviceScan = false;
            }
        }

        private void OnOpenSettings(object sender, RoutedEventArgs e) => ShowSettings();

        /// <summary>진단 기록 폴더를 탐색기로 연다(없으면 만든다).</summary>
        public void OpenMemoryLogs()
        {
            try
            {
                string dir = _vm.MemoryService.LogDirectory;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { dir }, UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) { Logger.Error("진단 기록 폴더 열기 실패", ex); }
        }

        private void ShowMemorySettings()
        {
            ShowSettings();
            _settingsWindow?.ShowMemorySection();
        }

        /// <summary>메모리 이상 알림(백그라운드 스레드에서 호출됨). 설정에서 알림을 끈 경우 표시하지 않는다.</summary>
        private void OnMemoryAlert(IReadOnlyList<Models.MemoryAlert> alerts) =>
            Dispatcher.BeginInvoke(() =>
            {
                if (!_settings.MemAlertsEnabled || alerts.Count == 0) return;
                var (title, body) = Models.MemoryAlertText.Compose(alerts, TimeSpan.FromMinutes(_settings.MemAlertSustainMin));
                _tray.ShowNotification(title, body, warning: true);
            });

        // ───────── 프로세스 현황 창 (중복 생성 방지) ─────────

        private void ShowProcesses()
        {
            try
            {
                if (_processWindow != null)
                {
                    if (_processWindow.WindowState == WindowState.Minimized) _processWindow.WindowState = WindowState.Normal;
                    _processWindow.Activate();
                    return;
                }
                var win = new ProcessWindow(new ProcessListViewModel()) { Topmost = Topmost };
                _processWindow = win;
                win.Closed += (_, _) => _processWindow = null;
                win.Show();
                win.Activate();
            }
            catch (Exception ex)
            {
                Logger.Error("프로세스 현황 창 열기 실패", ex);
                _processWindow = null;
            }
        }

        private void OnOpenProcesses(object sender, RoutedEventArgs e) => ShowProcesses();

        // ───────── 정밀 추적 창 (중복 생성 방지) ─────────

        private void ShowPrecisionTrace()
        {
            try
            {
                if (_traceWindow != null)
                {
                    if (_traceWindow.WindowState == WindowState.Minimized) _traceWindow.WindowState = WindowState.Normal;
                    _traceWindow.Activate();
                    return;
                }
                var win = new PrecisionTraceWindow(_vm.Trace, SuggestTraceTags(_vm.MemoryService.Current.PoolTags)) { Topmost = Topmost };
                _traceWindow = win;
                win.Closed += (_, _) => _traceWindow = null;
                win.Show();
                win.Activate();
            }
            catch (Exception ex)
            {
                Logger.Error("정밀 추적 창 열기 실패", ex);
                _traceWindow = null;
            }
        }

        /// <summary>기본 추적 대상: 1시간(없으면 10분) 증가량이 큰 태그, 증가 이력이 없으면 사용량이 큰 태그 4개.</summary>
        internal static string SuggestTraceTags(Models.PoolTagReport? r)
        {
            if (r is not { Available: true }) return "";
            var growth = r.Growth1h ?? r.Growth10m;
            var keys = growth is { Count: > 0 }
                ? growth.Where(g => g.Value > 0).OrderByDescending(g => g.Value).Select(g => g.Key).Take(4).ToHashSet()
                : [];
            var tags = r.Tags.Where(t => keys.Contains(t.Key)).OrderByDescending(t => growth![t.Key]).Select(t => t.Tag).ToList();
            if (tags.Count == 0) tags = r.Tags.OrderByDescending(t => t.PagedUsed + t.NonPagedUsed).Take(4).Select(t => t.Tag).ToList();
            return string.Join(", ", tags.Where(t => Services.Memory.PrecisionTraceService.ParseTags(t, out _) is null).Select(t => t.TrimEnd()));
        }

        private void OnTraceFinished(string? path, string? error) =>
            Dispatcher.BeginInvoke(() =>
            {
                if (path is not null) _tray.ShowNotification("정밀 추적 저장됨", $"WPA로 열어 확인하세요: {path}", warning: false);
                else if (error is not null) _tray.ShowNotification("정밀 추적 실패", error, warning: true);
            });

        // ───────── 메모리 상세 분석 창 (중복 생성 방지) ─────────

        private void ShowMemoryAnalysis()
        {
            try
            {
                if (_memoryWindow != null)
                {
                    if (_memoryWindow.WindowState == WindowState.Minimized) _memoryWindow.WindowState = WindowState.Normal;
                    _memoryWindow.Activate();
                    return;
                }
                var win = new MemoryWindow(new MemoryAnalysisViewModel(_vm.MemoryService, _settings), OpenMemoryLogs) { Topmost = Topmost };
                _memoryWindow = win;
                win.Closed += (_, _) => _memoryWindow = null;
                win.Show();
                win.Activate();
            }
            catch (Exception ex)
            {
                Logger.Error("메모리 상세 분석 창 열기 실패", ex);
                _memoryWindow = null;
            }
        }

        // ───────── 위치 / 화면 ─────────

        /// <summary>저장된 위치를 복원하되, 현재 가상 화면 밖이면 기본 위치(화면 우상단)로 되돌린다.</summary>
        private void RestorePosition()
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (_settings.WindowLeft is { } l && _settings.WindowTop is { } t && IsVisibleOnScreen(l, t))
            {
                Left = l;
                Top = t;
            }
            else ResetPosition();
        }

        private void ResetPosition()
        {
            Left = SystemParameters.WorkArea.Right - Width - 20;
            Top = SystemParameters.WorkArea.Top + 20;
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
        }

        private bool IsVisibleOnScreen(double left, double top)
        {
            // 창의 상단 일부(타이틀 영역)가 가상 화면 안에 있어야 한다.
            var v = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                             SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var title = new Rect(left, top, Math.Max(Width, 100), 40);
            title.Intersect(v);
            return !title.IsEmpty && title.Width >= 60 && title.Height >= 20;
        }

        /// <summary>
        /// 창이 있는 모니터의 작업 영역에 맞춰 최대 높이를 제한하고, 높이가 늘어 화면 아래로 벗어나면 위로 올린다.
        /// (표시 항목이 바뀌어 높이가 변할 때 호출된다.)
        /// </summary>
        private void ClampToWorkArea()
        {
            try
            {
                var hwnd = Hwnd;
                if (hwnd == IntPtr.Zero) return;
                var src = PresentationSource.FromVisual(this);
                if (src?.CompositionTarget is null) return;

                var wa = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea; // 픽셀 단위
                var m = src.CompositionTarget.TransformFromDevice;
                var tl = m.Transform(new Point(wa.Left, wa.Top));
                var br = m.Transform(new Point(wa.Right, wa.Bottom));

                double maxH = Math.Max(120, br.Y - tl.Y);
                if (Math.Abs(MaxHeight - maxH) > 1) MaxHeight = maxH;

                double height = ActualHeight;
                if (Top + height > br.Y) Top = Math.Max(tl.Y, br.Y - height);
            }
            catch (Exception ex)
            {
                Logger.Error("창 위치 보정 실패", ex);
            }
        }

        /// <summary>해상도/모니터 구성이 바뀌면 창이 화면 밖에 남지 않도록 보정한다.</summary>
        private void SystemEvents_Subscribe()
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        }

        private void OnDisplayChanged(object? sender, EventArgs e) =>
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsVisibleOnScreen(Left, Top)) ResetPosition();
                ClampToWorkArea();
            });

        private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_closingTrace || _vm.Trace.State != Services.Memory.PrecisionTraceService.TraceState.Idle)
            {
                e.Cancel = true;
                if (_closingTrace) return;
                _closingTrace = true;
                try
                {
                    await _vm.Trace.StopAsync(save: false);
                    if (_vm.Trace.State == Services.Memory.PrecisionTraceService.TraceState.Idle)
                        _ = Dispatcher.BeginInvoke(Close);
                    else ShowPrecisionTrace();
                }
                finally { _closingTrace = false; }
                return;
            }
            _saveTimer.Stop();
            if (WindowState == WindowState.Normal)
            {
                _settings.WindowLeft = Left;
                _settings.WindowTop = Top;
                _settings.WindowWidth = Math.Clamp(Width, AppSettings.MinWidth, AppSettings.MaxWidth);
            }
            _settingsService.Save(_settings);
            _settingsWindow?.Close();
            _processWindow?.Close();
            _memoryWindow?.Close();
            _traceWindow?.Close();
            _vm.MemoryService.AlertRaised -= OnMemoryAlert;
            _vm.Trace.Finished -= OnTraceFinished;
            _hotKey.Dispose();
            _tray.Dispose();
            _vm.Dispose();
        }

        private void OnDrag(object sender, MouseButtonEventArgs e)
        {
            // 버튼 클릭은 드래그로 처리하지 않는다.
            if (e.OriginalSource is DependencyObject d && FindParent<System.Windows.Controls.Button>(d) != null) return;
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch (InvalidOperationException) { }
            }
        }

        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T t) return t;
                child = System.Windows.Media.VisualTreeHelper.GetParent(child);
            }
            return null;
        }

        private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void OnClose(object sender, RoutedEventArgs e) => Close();
    }
}
