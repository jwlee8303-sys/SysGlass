using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SysGlass.Utilities;
using SysGlass.ViewModels;

namespace SysGlass.Views;

/// <summary>설정 창. 메인 창과 별개의 창이며 클릭 통과 스타일은 적용되지 않는다.</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        // 어두운 제목 표시줄
        SourceInitialized += (_, _) => WindowStyleHelper.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        RestoreExpanded();
        Closed += (_, _) => { SaveExpanded(); _vm.Dispose(); };
    }

    // 카드 펼침 상태는 앱이 실행되는 동안만 기억한다(처음에는 모두 접힘).
    private static readonly Dictionary<string, bool> s_expanded = [];

    private IEnumerable<Expander> CardExpanders() =>
        CardHost.Children.OfType<Border>().Select(b => b.Child).OfType<Expander>();

    private void RestoreExpanded()
    {
        foreach (var x in CardExpanders())
            if (x.Header is string h && s_expanded.TryGetValue(h, out bool open)) x.IsExpanded = open;
    }

    private void SaveExpanded()
    {
        foreach (var x in CardExpanders())
            if (x.Header is string h) s_expanded[h] = x.IsExpanded;
    }

    /// <summary>메모리 감시 설정을 펼치고 그 위치로 스크롤한다. 창을 막 연 경우를 위해 레이아웃이 끝난 뒤 실행한다.</summary>
    public void ShowMemorySection()
    {
        MemoryExpander.IsExpanded = true;
        Dispatcher.BeginInvoke(() => MemoryCard.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ───────── 표시 그룹 순서: ▲▼ 버튼과 드래그 ─────────

    private static readonly Brush DropMarkBrush = Frozen(Color.FromRgb(0x4C, 0xC2, 0xFF));
    private Point _dragStart;
    private GroupOrderItem? _dragItem;
    private Border? _dropMark;

    private void OnGroupUp(object sender, RoutedEventArgs e) => MoveBy(sender, -1);
    private void OnGroupDown(object sender, RoutedEventArgs e) => MoveBy(sender, +1);

    private void MoveBy(object sender, int delta)
    {
        if ((sender as FrameworkElement)?.DataContext is not GroupOrderItem item) return;
        int i = _vm.GroupOrder.IndexOf(item);
        _vm.MoveGroup(i, i + delta);
    }

    private void OnGroupMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 버튼 클릭은 드래그로 처리하지 않는다.
        _dragItem = FindAncestor<Button>(e.OriginalSource as DependencyObject) is null
            ? (e.OriginalSource as FrameworkElement)?.DataContext as GroupOrderItem
            : null;
        _dragStart = e.GetPosition(GroupOrderList);
    }

    private void OnGroupMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(GroupOrderList) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop(GroupOrderList, item, DragDropEffects.Move);
        ClearDropMark();
    }

    private void OnGroupDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(GroupOrderItem)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        var target = FindAncestor<Border>(e.OriginalSource as DependencyObject, b => b.DataContext is GroupOrderItem);
        if (target == _dropMark) return;
        ClearDropMark();
        if (target is null) return;
        _dropMark = target;
        target.BorderBrush = DropMarkBrush;
    }

    private void OnGroupDragLeave(object sender, DragEventArgs e) => ClearDropMark();

    private void OnGroupDrop(object sender, DragEventArgs e)
    {
        ClearDropMark();
        if (e.Data.GetData(typeof(GroupOrderItem)) is not GroupOrderItem item) return;
        // 항목 위에 놓으면 그 자리로, 빈 곳(목록 아래)에 놓으면 맨 끝으로 옮긴다.
        var target = (FindAncestor<Border>(e.OriginalSource as DependencyObject, b => b.DataContext is GroupOrderItem)?.DataContext) as GroupOrderItem;
        int from = _vm.GroupOrder.IndexOf(item);
        int to = target is null ? _vm.GroupOrder.Count - 1 : _vm.GroupOrder.IndexOf(target);
        _vm.MoveGroup(from, to);
    }

    private void ClearDropMark()
    {
        _dropMark?.ClearValue(Border.BorderBrushProperty);
        _dropMark = null;
    }

    private static T? FindAncestor<T>(DependencyObject? d, Func<T, bool>? match = null) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t && (match is null || match(t))) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e) => await _vm.CheckForUpdateAsync();

    private async void OnApplyUpdate(object sender, RoutedEventArgs e)
    {
        if (_vm.AvailableUpdate is not { } info) return;
        if (MessageBox.Show(this,
                $"SysGlass {info.Version}(으)로 업데이트할까요?\n\nSysGlass가 종료된 뒤 새 버전으로 다시 실행됩니다. 설정은 그대로 유지됩니다.",
                "SysGlass 업데이트", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if ((Owner as MainWindow ?? Application.Current.MainWindow as MainWindow) is not { } main) return;
        await _vm.ApplyUpdateAsync((update, progress) => main.RunUpdateAsync(update, this, progress));
    }

    private void OnOpenMemoryLogs(object sender, RoutedEventArgs e) => (Owner as MainWindow ?? Application.Current.MainWindow as MainWindow)?.OpenMemoryLogs();

    private void OnReset(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "표시 항목, 디자인, 갱신 주기, 온도 기준, 하드웨어 선택을 기본값으로 되돌립니다.\n\n" +
            "[예]  창 위치와 너비까지 함께 복원\n" +
            "[아니요]  창 위치와 너비는 현재 값 유지\n" +
            "[취소]  복원하지 않음\n\n" +
            "항상 위에 표시와 클릭 통과도 기본값(항상 위 켜짐, 클릭 통과 꺼짐)으로 돌아갑니다.",
            "기본값 복원", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (result == MessageBoxResult.Cancel) return;
        _vm.ResetToDefaults(includeWindow: result == MessageBoxResult.Yes);
    }
}
