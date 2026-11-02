using System.Windows.Media;
using SysGlass.Models;
using SysGlass.Settings;
using SysGlass.Utilities;

namespace SysGlass.ViewModels;

/// <summary>온도 값에 따른 표시 색상. 참고용이며 안전 여부를 단정하지 않는다.</summary>
internal static class TempBrushes
{
    public static readonly Brush Normal = Freeze(Color.FromRgb(0xF2, 0xF2, 0xF2));
    public static readonly Brush Warn = Freeze(Color.FromRgb(0xFF, 0xD2, 0x4D));
    public static readonly Brush High = Freeze(Color.FromRgb(0xFF, 0x5C, 0x5C));

    private static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>센서값이 없으면(N/A) 경고 색상을 적용하지 않는다.</summary>
    public static Brush For(double? t, double warn, double high) =>
        t is not { } v ? Normal : v >= high ? High : v >= warn ? Warn : Normal;
}

public sealed class GpuItemViewModel : ViewModelBase
{
    private readonly AppSettings _s;
    public GpuItemViewModel(string id, AppSettings s) { Id = id; _s = s; }

    public string Id { get; }
    private string _name = "";
    private string _usageText = Format.NA, _tempText = Format.NA, _vramText = Format.NA;
    private double _usage, _vramPercent;
    private Brush _tempBrush = TempBrushes.Normal;

    public string Name { get => _name; private set => Set(ref _name, value); }
    public string UsageText { get => _usageText; private set => Set(ref _usageText, value); }
    public string TempText { get => _tempText; private set => Set(ref _tempText, value); }
    public string VramText { get => _vramText; private set => Set(ref _vramText, value); }
    public double Usage { get => _usage; private set => Set(ref _usage, value); }
    public double VramPercent { get => _vramPercent; private set => Set(ref _vramPercent, value); }
    public Brush TempBrush { get => _tempBrush; private set => Set(ref _tempBrush, value); }

    public void Update(GpuInfo g)
    {
        Name = g.Name;
        UsageText = Format.Percent(g.UsagePercent);
        Usage = g.UsagePercent ?? 0;
        TempText = Format.Temp(g.TempC);
        TempBrush = TempBrushes.For(g.TempC, _s.GpuTempWarn, _s.GpuTempHigh);
        VramText = Format.Vram(g.VramUsedMb, g.VramTotalMb);
        VramPercent = g.VramPercent ?? 0;
    }
}

public sealed class DiskItemViewModel : ViewModelBase
{
    private readonly AppSettings _s;
    public DiskItemViewModel(string id, AppSettings s) { Id = id; _s = s; }

    public string Id { get; }
    private string _title = "", _model = "", _usageText = Format.NA, _tempText = Format.NA, _ioText = Format.NA;
    private double _usage;
    private Brush _tempBrush = TempBrushes.Normal;

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Model { get => _model; private set => Set(ref _model, value); }
    public string UsageText { get => _usageText; private set => Set(ref _usageText, value); }
    public string TempText { get => _tempText; private set => Set(ref _tempText, value); }
    public string IoText { get => _ioText; private set => Set(ref _ioText, value); }
    public double Usage { get => _usage; private set => Set(ref _usage, value); }
    public Brush TempBrush { get => _tempBrush; private set => Set(ref _tempBrush, value); }

    public void Update(DiskInfo d)
    {
        // 디스크 번호는 모델명이 유일하게 매핑될 때만 표시한다.
        string num = d.Number is { } n ? $"DISK {n}" : "DISK";
        Title = d.Kind.Length > 0 ? $"{num} ({d.Kind})" : num;
        Model = d.Name;
        UsageText = Format.Percent(d.ActivityPercent);
        Usage = d.ActivityPercent ?? 0;
        TempText = Format.Temp(d.TempC);
        TempBrush = TempBrushes.For(d.TempC, _s.DiskTempWarn, _s.DiskTempHigh);
        var parts = new List<string>();
        if (_s.ShowDiskRead) parts.Add($"R {Format.Rate(d.ReadBytesPerSec)}");
        if (_s.ShowDiskWrite) parts.Add($"W {Format.Rate(d.WriteBytesPerSec)}");
        IoText = string.Join("  ", parts);
    }
}
