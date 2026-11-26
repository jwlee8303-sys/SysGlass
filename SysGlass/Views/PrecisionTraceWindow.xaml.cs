using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SysGlass.Services.Memory;
using SysGlass.Models;
using SysGlass.Utilities;
using SysGlass.ViewModels;
using static SysGlass.Services.Memory.PrecisionTraceService;

namespace SysGlass.Views;

/// <summary>정밀 추적 동의·실행 창. 창을 닫아도 진행 중인 기록은 계속되며, 끝나면 트레이 알림으로 알린다.</summary>
public partial class PrecisionTraceWindow : Window
{
    private readonly PrecisionTraceService _svc;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    public PrecisionTraceWindow(PrecisionTraceService svc, string suggestedTags)
    {
        _svc = svc;
        InitializeComponent();
        SourceInitialized += (_, _) => WindowStyleHelper.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        TagsBox.Text = _svc.Current is { } cur ? string.Join(", ", cur.Tags.Select(t => t.TrimEnd())) : suggestedTags;
        MinutesBox.Text = (_svc.Current?.Minutes ?? MinutesRange.Default).ToString(CultureInfo.InvariantCulture);
        BufferBox.Text = (_svc.Current?.BufferMb ?? BufferMbRange.Default).ToString(CultureInfo.InvariantCulture);
        MinutesHint.Text = $"분 ({MinutesRange.Min}~{MinutesRange.Max})";
        BufferHint.Text = $"MB ({BufferMbRange.Min}~{BufferMbRange.Max})";
        _svc.Changed += OnServiceChanged;
        _tick.Tick += (_, _) => Refresh();
        Closed += (_, _) => { _svc.Changed -= OnServiceChanged; _tick.Stop(); };
        Refresh();
    }

    private void OnServiceChanged() => Dispatcher.BeginInvoke(Refresh);
    private void OnInputChanged(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        var state = _svc.State;
        bool idle = state == TraceState.Idle;
        TagsBox.IsEnabled = MinutesBox.IsEnabled = BufferBox.IsEnabled = ConsentBox.IsEnabled = idle;
        StartButton.IsEnabled = idle && ConsentBox.IsChecked == true && IsAvailable;
        StopButton.IsEnabled = CancelButton.IsEnabled = state == TraceState.Recording;
        if (state == TraceState.Recording) _tick.Start(); else _tick.Stop();

        StatusText.Foreground = System.Windows.Media.Brushes.White;
        StatusText.Text = state switch
        {
            TraceState.Starting => "정밀 추적을 시작하는 중...",
            TraceState.Recording => $"기록 중 · 남은 시간 {Remaining()} · 태그 {Tags()}",
            TraceState.Stopping => "기록을 멈추고 파일로 저장하는 중입니다. 몇 분 걸릴 수 있습니다...",
            _ when !IsAvailable => "이 PC에서 wpr.exe를 찾을 수 없어 정밀 추적을 사용할 수 없습니다.",
            _ when _svc.LastError is { } err => err,
            _ when _svc.LastPath is { } p => $"저장됨: {p}\nWPA(Windows Performance Analyzer)로 열어 'Memory → Pool' 그래프를 확인하세요.",
            _ => "동의 후 시작할 수 있습니다.",
        };
        if (idle && _svc.LastError is not null) StatusText.Foreground = MemoryBrushes.For(MemoryLevel.Warn, 0);
    }

    private string Remaining()
    {
        var left = (_svc.EndsAt ?? DateTimeOffset.Now) - DateTimeOffset.Now;
        return left <= TimeSpan.Zero ? "0:00" : $"{(int)left.TotalMinutes}:{left.Seconds:00}";
    }

    private string Tags() => _svc.Current is { Tags.Count: > 0 } o ? string.Join(", ", o.Tags.Select(t => t.TrimEnd())) : "전체";

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (ParseTags(TagsBox.Text, out var tags) is { } tagError) { Warn(tagError); return; }
        if (!int.TryParse(MinutesBox.Text, out int minutes) || minutes < MinutesRange.Min || minutes > MinutesRange.Max)
        { Warn($"기록 시간은 {MinutesRange.Min}~{MinutesRange.Max}분 사이로 입력하세요."); return; }
        if (!int.TryParse(BufferBox.Text, out int mb) || mb < BufferMbRange.Min || mb > BufferMbRange.Max)
        { Warn($"버퍼 크기는 {BufferMbRange.Min}~{BufferMbRange.Max}MB 사이로 입력하세요."); return; }
        if (tags.Count == 0 && MessageBox.Show(this, "태그를 지정하지 않으면 모든 Pool 할당을 기록해 부담이 크고 버퍼가 빨리 찹니다. 계속할까요?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        StartButton.IsEnabled = false;
        var error = await _svc.StartAsync(new Options(tags, minutes, mb));
        Refresh();
        if (error is not null) Warn(error);
    }

    private async void OnStop(object sender, RoutedEventArgs e) => await _svc.StopAsync(save: true);

    private async void OnCancel(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "지금까지 기록한 내용을 저장하지 않고 버릴까요?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await _svc.StopAsync(save: false);
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_svc.Directory);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { _svc.Directory }, UseShellExecute = false });
        }
        catch (Exception ex) { Logger.Error("추적 폴더 열기 실패", ex); }
    }

    private void Warn(string text)
    {
        StatusText.Foreground = MemoryBrushes.For(MemoryLevel.Warn, 0);
        StatusText.Text = text;
    }
}
