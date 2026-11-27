using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;
using SysGlass.Utilities;
using SysGlass.ViewModels;

namespace SysGlass.Views;

/// <summary>메모리 상세 분석 창. 머리글을 누르면 해당 열 기준으로 정렬한다(▼ 내림차순, ▲ 오름차순).</summary>
public partial class MemoryWindow : Window
{
    private readonly MemoryAnalysisViewModel _vm;
    private readonly Action _openFolder;

    public MemoryWindow(MemoryAnalysisViewModel vm, Action openFolder)
    {
        _vm = vm;
        _openFolder = openFolder;
        DataContext = vm;
        InitializeComponent();
        SourceInitialized += (_, _) => WindowStyleHelper.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        _vm.SortChanged += UpdateHeaders;
        void UpdateActivity() => _vm.SetDisplayActive(IsVisible && WindowState != WindowState.Minimized);
        IsVisibleChanged += (_, _) => UpdateActivity();
        StateChanged += (_, _) => UpdateActivity();
        Loaded += (_, _) => { _vm.Start(); UpdateHeaders(); };
        Closed += (_, _) => { _vm.SortChanged -= UpdateHeaders; _vm.Dispose(); };
    }

    private static string? SortKey(RoutedEventArgs e) =>
        e.OriginalSource is GridViewColumnHeader { Column.Header: TextBlock { Tag: string key } } ? key : null;

    private void OnTagHeaderClick(object sender, RoutedEventArgs e) { if (SortKey(e) is { } k) _vm.SortTags(k); }
    private void OnProcHeaderClick(object sender, RoutedEventArgs e) { if (SortKey(e) is { } k) _vm.SortProcesses(k); }

    /// <summary>정렬 기준 열 머리글에 방향 표시를 붙인다. 원래 제목은 처음 본 텍스트로 기억한다.</summary>
    private void UpdateHeaders()
    {
        Mark(TagList, _vm.TagSortKey, _vm.TagSortAscending);
        Mark(ProcList, _vm.ProcessSortKey, _vm.ProcessSortAscending);
    }

    private static void Mark(ListView list, string key, bool asc)
    {
        if (list.View is not GridView grid) return;
        foreach (var col in grid.Columns)
        {
            if (col.Header is not TextBlock { Tag: string k } tb) continue;
            tb.Uid = tb.Uid.Length > 0 ? tb.Uid : tb.Text;
            tb.Text = k == key ? $"{tb.Uid} {(asc ? "▲" : "▼")}" : tb.Uid;
        }
    }

    private async void OnSearchDrivers(object sender, RoutedEventArgs e) => await _vm.SearchDriversAsync();
    private void OnCancelSearch(object sender, RoutedEventArgs e) => _vm.CancelSearch();
    private void OnSaveNow(object sender, RoutedEventArgs e) => _vm.SaveDiagnosticNow();
    private void OnOpenFolder(object sender, RoutedEventArgs e) => _openFolder();

    private void OnExportDiagnostic(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "진단 파일 내보내기",
            Filter = "JSON 파일 (*.json)|*.json",
            FileName = $"sysglass-memory-{DateTime.Now:yyyyMMdd-HHmmss}.json",
        };
        if (dlg.ShowDialog(this) == true) _vm.ExportDiagnostic(dlg.FileName);
    }

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        string[] names = ["graph", "pooltags", "processes", "process-events", "alerts"];
        int tab = Math.Clamp(Tabs.SelectedIndex, 0, names.Length - 1);
        var dlg = new SaveFileDialog
        {
            Title = "현재 표 CSV 내보내기",
            Filter = "CSV 파일 (*.csv)|*.csv",
            FileName = $"sysglass-{names[tab]}-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };
        if (dlg.ShowDialog(this) != true) return;
        try { _vm.ExportCsv(tab, dlg.FileName); }
        catch (Exception ex)
        {
            Logger.Error("CSV 내보내기 실패", ex);
            MessageBox.Show(this, "CSV 파일을 저장할 수 없습니다. 경로와 쓰기 권한을 확인하세요.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
