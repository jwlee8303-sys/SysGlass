using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
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

            Topmost = _settings.AlwaysOnTop;
            Width = _settings.WindowWidth;
            RestorePosition();

            Loaded += (_, _) => { _vm.Start(); ClampToWorkArea(); };
            // 창 핸들이 만들어진 뒤에만 트레이/단축키/Win32 스타일을 다룬다.
            SourceInitialized += OnSourceInitialized;
            IsVisibleChanged += (_, _) => ApplyClickThrough(); // 핸들/스타일이 바뀌었을 가능성에 대비해 재적용
            SizeChanged += (_, e) => { if (e.HeightChanged) ClampToWorkArea(); };
            Closing += OnClosing;
            SystemEvents_Subscribe();
        }

        private IntPtr Hwnd => new System.Windows.Interop.WindowInteropHelper(this).Handle;

        // ───────── 설정 반영 ─────────

        private void OnSettingChanged(string name)
        {
            if (name is nameof(AppSettings.FontSizeLevel) or "Reset") ApplyFontResources();
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

        /// <summary>글자 크기 단계에 맞게 크기 리소스를 갱신한다. 레이아웃은 자동으로 다시 계산된다.</summary>
        private void ApplyFontResources()
        {
            double s = _vm.FontScale;
            Resources["FsBase"] = Math.Round(13 * s * 2) / 2;
            Resources["FsLabel"] = Math.Round(11 * s * 2) / 2;
            Resources["FsLarge"] = Math.Round(16 * s * 2) / 2;
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
                ShowSettings, () => Close());
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
                _vm.NeedDeviceScan = true; // 설정 창이 열려 있는 동안 GPU/디스크를 감지
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

        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _saveTimer.Stop();
            if (WindowState == WindowState.Normal)
            {
                _settings.WindowLeft = Left;
                _settings.WindowTop = Top;
                _settings.WindowWidth = Math.Clamp(Width, AppSettings.MinWidth, AppSettings.MaxWidth);
            }
            _settingsService.Save(_settings);
            _settingsWindow?.Close();
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
