using System.Windows;
using System.Windows.Media;

namespace SysGlass.Views;

public enum GaugeKind { Ring, Capsule }

/// <summary>
/// 컴팩트 보기용 사용률 게이지. 워치 위젯 같은 원형 링, 또는 배터리 위젯 같은 캡슐을 직접 그린다.
/// 글자는 그리지 않으며 XAML에서 위에 겹쳐 표시한다.
/// </summary>
public sealed class UsageGauge : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(GaugeKind), typeof(UsageGauge),
        new FrameworkPropertyMetadata(GaugeKind.Ring, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(UsageGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(UsageGauge),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(UsageGauge),
        new FrameworkPropertyMetadata(Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(UsageGauge),
        new FrameworkPropertyMetadata(5.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public GaugeKind Kind { get => (GaugeKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    /// <summary>0~100 사용률. 범위를 벗어나면 잘라서 그린다.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    /// <summary>링 두께(px). 캡슐에는 쓰지 않는다.</summary>
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double v = double.IsFinite(Value) ? Math.Clamp(Value, 0, 100) / 100.0 : 0;
        if (Kind == GaugeKind.Ring) DrawRing(dc, w, h, v);
        else DrawCapsule(dc, w, h, v);
    }

    private void DrawRing(DrawingContext dc, double w, double h, double v)
    {
        double t = Math.Max(1, Thickness);
        double r = Math.Min(w, h) / 2 - t / 2;
        if (r <= 0) return;
        var c = new Point(w / 2, h / 2);
        dc.DrawEllipse(null, new Pen(Track, t), c, r, r);
        if (v <= 0) return;

        var pen = new Pen(Fill, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (v >= 0.999) { dc.DrawEllipse(null, pen, c, r, r); return; }

        // 12시 방향에서 시계 방향으로 채운다.
        double a = v * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, v > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    private void DrawCapsule(DrawingContext dc, double w, double h, double v)
    {
        double radius = h / 2;
        var rect = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(Track, null, rect, radius, radius);
        if (v <= 0) return;

        // 배터리처럼 왼쪽부터 채우고, 위에 겹친 글자가 잘 보이도록 채움을 약간 투명하게 한다.
        var clip = new RectangleGeometry(rect, radius, radius);
        clip.Freeze();
        dc.PushClip(clip);
        dc.PushOpacity(0.55);
        dc.DrawRectangle(Fill, null, new Rect(0, 0, w * v, h));
        dc.Pop();
        dc.Pop();
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
