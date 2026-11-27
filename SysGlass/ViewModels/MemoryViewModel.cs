using System.Windows.Media;
using SysGlass.Models;
using SysGlass.Utilities;

namespace SysGlass.ViewModels;

/// <summary>
/// 메모리 상태 색상. 0=기본(노랑/빨강), 1=고대비(주황/자홍).
/// 정상은 본문 글자 색, 측정 불가는 항목 이름 색을 따라 다른 그룹과 같은 글자 색 체계를 쓴다.
/// </summary>
internal static class MemoryBrushes
{
    public static Brush Normal => TempBrushes.Normal;
    public static Brush Unknown => TempBrushes.Label;
    private static readonly Brush[] Warn = [TempBrushes.Warn, Freeze(Color.FromRgb(0xFF, 0x9F, 0x1C))];
    private static readonly Brush[] High = [TempBrushes.High, Freeze(Color.FromRgb(0xFF, 0x4F, 0xD8))];

    private static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public static Brush For(MemoryLevel level, int scheme)
    {
        int i = Math.Clamp(scheme, 0, 1);
        return level switch
        {
            MemoryLevel.High => High[i],
            MemoryLevel.Warn => Warn[i],
            MemoryLevel.Normal => Normal,
            _ => Unknown,
        };
    }
}

/// <summary>오버레이의 커널 메모리 항목. 측정하지 못한 값은 0이 아니라 N/A·미측정으로 표시한다.</summary>
public sealed class MemoryOverlayViewModel : ViewModelBase
{
    private string _pagedText = Format.NA, _pagedTip = "", _nonPagedText = Format.NA, _nonPagedTip = "";
    private string _growthText = Format.NA, _growthTip = "", _uptimeText = Format.NA, _uptimeTip = "";
    private string _statusText = MemoryLevelText.Of(MemoryLevel.Unknown), _statusTip = "";
    private Brush _pagedBrush = MemoryBrushes.Normal, _nonPagedBrush = MemoryBrushes.Normal,
        _growthBrush = MemoryBrushes.Normal, _uptimeBrush = MemoryBrushes.Normal, _statusBrush = MemoryBrushes.Unknown;

    public string PagedPoolText { get => _pagedText; private set => Set(ref _pagedText, value); }
    public string PagedPoolTip { get => _pagedTip; private set => Set(ref _pagedTip, value); }
    public Brush PagedPoolBrush { get => _pagedBrush; private set => Set(ref _pagedBrush, value); }
    public string NonPagedPoolText { get => _nonPagedText; private set => Set(ref _nonPagedText, value); }
    public string NonPagedPoolTip { get => _nonPagedTip; private set => Set(ref _nonPagedTip, value); }
    public Brush NonPagedPoolBrush { get => _nonPagedBrush; private set => Set(ref _nonPagedBrush, value); }
    public string PoolGrowthText { get => _growthText; private set => Set(ref _growthText, value); }
    public string PoolGrowthTip { get => _growthTip; private set => Set(ref _growthTip, value); }
    public Brush PoolGrowthBrush { get => _growthBrush; private set => Set(ref _growthBrush, value); }
    public string UptimeText { get => _uptimeText; private set => Set(ref _uptimeText, value); }
    public string UptimeTip { get => _uptimeTip; private set => Set(ref _uptimeTip, value); }
    /// <summary>가동 시간 색. 판정 대상이 아니므로 정상은 본문 색, 감시 꺼짐은 다른 항목과 같은 흐린 색.</summary>
    public Brush UptimeBrush { get => _uptimeBrush; private set => Set(ref _uptimeBrush, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string StatusTip { get => _statusTip; private set => Set(ref _statusTip, value); }
    public Brush StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }

    private MemoryWatchState? _last;

    /// <summary>색상 설정만 바뀌었을 때 마지막 상태로 다시 그린다.</summary>
    public void Refresh(int colorScheme) { if (_last is { } s) Update(s, colorScheme); }

    public void Update(MemoryWatchState s, int colorScheme)
    {
        _last = s;
        if (!s.Enabled)
        {
            const string off = "감시 꺼짐";
            PagedPoolText = NonPagedPoolText = PoolGrowthText = UptimeText = StatusText = off;
            PagedPoolTip = NonPagedPoolTip = PoolGrowthTip = UptimeTip = StatusTip = "설정에서 메모리 감시를 켜면 표시됩니다.";
            PagedPoolBrush = NonPagedPoolBrush = PoolGrowthBrush = UptimeBrush = StatusBrush = MemoryBrushes.Unknown;
            return;
        }

        var a = s.Assessment;
        // 값 행은 주의·위험일 때만 색을 입힌다(측정 불가 값은 이미 N/A·미측정 문구로 구분된다).
        Brush BrushOf(string key) => a.Metrics.FirstOrDefault(m => m.Key == key)?.Level is { } l && l >= MemoryLevel.Warn
            ? MemoryBrushes.For(l, colorScheme) : MemoryBrushes.Normal;

        var k = s.Kernel;
        string resident = s.PagedResident is { } r ? $"상주 {Format.Bytes(r)}" : "상주 N/A";
        PagedPoolText = k?.PagedPool is { } paged ? $"{Format.Bytes(paged)} ({resident})" : Format.NA;
        PagedPoolTip = s.KernelError ?? (s.PagedResidentError is { } re
            ? "페이징 풀 상주량: " + re
            : $"페이징 풀 상주량은 {(s.PagedResidentTime is { } t ? t.ToString("HH:mm:ss") : "-")} 기준입니다.");
        PagedPoolBrush = BrushOf("paged");
        NonPagedPoolText = Format.Bytes(k?.NonPagedPool);
        NonPagedPoolTip = s.KernelError ?? "";
        NonPagedPoolBrush = BrushOf("nonpaged");

        PoolGrowthText = $"10분 {GrowthText(s.Growth10m)} · 1시간 {GrowthText(s.Growth1h)}";
        PoolGrowthTip = $"페이징+비페이징 풀 증가량 (절전 시간 제외)\n24시간: {GrowthText(s.Growth24h)}\n관측 시간: {Format.Duration(s.Observed)}";
        PoolGrowthBrush = BrushOf("growth");

        UptimeText = Format.Duration(s.Uptime);
        UptimeBrush = MemoryBrushes.Normal;
        UptimeTip = s.BootTime is { } boot
            ? $"커널 시작: {boot:yyyy-MM-dd HH:mm} (절전 시간 포함)\n빠른 시작이 켜져 있으면 시스템 종료 후에도 이어집니다."
            : "";

        StatusText = a.Summary;
        StatusBrush = MemoryBrushes.For(a.Overall, colorScheme);
        StatusTip = string.Join("\n", a.Metrics.Select(m =>
                $"{m.Label}: {m.ValueText} · {MemoryLevelText.Of(m.Level)}{(m.Level >= MemoryLevel.Warn && !m.Sustained ? " (지속 확인 중)" : "")}"))
            + (a.LeakSuspected ? "\n누수 의심 단계입니다. 원인은 확정되지 않았습니다." : "");
    }

    private static string GrowthText(PoolGrowth g) => g.Measured ? Format.SignedBytes(g.TotalDelta) : "미측정";
}
