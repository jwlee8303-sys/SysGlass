using System.Windows;
using System.Windows.Interop;
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
        Closed += (_, _) => _vm.Dispose();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

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