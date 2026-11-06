using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using SysGlass.Utilities;
using SysGlass.ViewModels;

namespace SysGlass.Views;

/// <summary>자원별 프로세스 점유 현황 창. 머리글을 누르면 해당 열 기준으로 정렬되고 실시간으로 유지된다.</summary>
public partial class ProcessWindow : Window
{
    private readonly ProcessListViewModel _vm;
    private readonly (GridViewColumn Column, ProcessSortKey Key, string Title)[] _sortColumns;

    public ProcessWindow(ProcessListViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        _sortColumns =
        [
            (ColName, ProcessSortKey.Name, "프로세스"),
            (ColImp, ProcessSortKey.Importance, "중요도"),
            (ColCpu, ProcessSortKey.Cpu, "CPU"),
            (ColGpu, ProcessSortKey.Gpu, "GPU"),
            (ColMem, ProcessSortKey.Memory, "메모리"),
            (ColDisk, ProcessSortKey.Disk, "디스크 I/O"),
        ];

        SourceInitialized += (_, _) => WindowStyleHelper.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        _vm.SortChanged += UpdateHeaders;
        Loaded += (_, _) => { UpdateHeaders(); _vm.Start(); };
        Closed += (_, _) => { _vm.SortChanged -= UpdateHeaders; _vm.Dispose(); };
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } col }) return;
        foreach (var (column, key, _) in _sortColumns)
        {
            if (ReferenceEquals(column, col)) { _vm.SortBy(key); return; }
        }
    }

    /// <summary>현재 정렬 기준 열의 머리글에 ▼ 표시를 한다.</summary>
    private void UpdateHeaders()
    {
        foreach (var (column, key, title) in _sortColumns)
        {
            if (column.Header is TextBlock tb)
                tb.Text = key != _vm.SortKey ? title
                        : _vm.SortAscending ? $"{title} ▲"
                        : $"{title} ▼";
        }
    }
}
