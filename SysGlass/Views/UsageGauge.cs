using System.Windows;
using System.Windows.Media;

namespace SysGlass.Views;

/// <summary>Capsule = 배터리 캡슐, History = 추이 그래프, Segments = LED 미터, Arc = 아크 게이지</summary>
public enum GaugeKind { Capsule, History, Segments, Arc }

/// <summary>
/// 컴팩트 보기용 사용률 게이지. 배터리 캡슐, 추이 그래프, LED 미터, 아크 게이지를 직접 그린다.
/// 글자는 그리지 않으며 XAML에서 위에 겹쳐 표시한다.
/// </summary>
public sealed class UsageGauge : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(GaugeKind), typeof(UsageGauge),
        new FrameworkPropertyMetadata(GaugeKind.Capsule, FrameworkPropertyMetadataOptions.AffectsRender));

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

    public static readonly DependencyProperty HistoryProperty = DependencyProperty.Register(
        nameof(History), typeof(IReadOnlyList<double>), typeof(UsageGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HistoryLengthProperty = DependencyProperty.Register(
        nameof(HistoryLength), typeof(int), typeof(UsageGauge),
        new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SegmentCountProperty = DependencyProperty.Register(
        nameof(SegmentCount), typeof(int), typeof(UsageGauge),
        new FrameworkPropertyMetadata(20, FrameworkPropertyMetadataOptions.AffectsRender));

    public GaugeKind Kind { get => (GaugeKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    /// <summary>0~100 사용률. 범위를 벗어나면 잘라서 그린다.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    /// <summary>링·아크 두께(px). 다른 모양에는 쓰지 않는다.</summary>
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    /// <summary>추이 그래프의 최근 값(0~100, 오래된 것부터). 새 목록 인스턴스를 넣어야 다시 그린다.</summary>
    public IReadOnlyList<double>? History { get => (IReadOnlyList<double>?)GetValue(HistoryProperty); set => SetValue(HistoryProperty, value); }
    /// <summary>추이 그래프 가로축의 표본 수. 아직 덜 쌓였으면 오른쪽부터 채운다.</summary>
    public int HistoryLength { get => (int)GetValue(HistoryLengthProperty); set => SetValue(HistoryLengthProperty, value); }
    /// <summary>LED 미터의 칸 수.</summary>
    public int SegmentCount { get => (int)GetValue(SegmentCountProperty); set => SetValue(SegmentCountProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double v = double.IsFinite(Value) ? Math.Clamp(Value, 0, 100) / 100.0 : 0;
        switch (Kind)
        {
            case GaugeKind.History: DrawHistory(dc, w, h); break;
            case GaugeKind.Segments: DrawSegments(dc, w, h, v); break;
            case GaugeKind.Arc: DrawArc(dc, w, h, v); break;
            default: DrawCapsule(dc, w, h, v); break;
        }
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

    /// <summary>
    /// 추이 그래프: 최근 값을 위가 진하고 아래로 옅어지는 면과 선으로 그린다.
    /// 가장 최근 값이 오른쪽 끝이며, 덜 쌓인 구간은 비워 둔다.
    /// </summary>
    private void DrawHistory(DrawingContext dc, double w, double h)
    {
        const double radius = 4;
        var rect = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(GraphBackground, null, rect, radius, radius);

        // 50% 기준선(옅은 점선)
        var guide = new Pen(Track, 1) { DashStyle = new DashStyle([2, 3], 0) };
        guide.Freeze();
        double mid = Math.Round(h / 2) + 0.5;
        dc.DrawLine(guide, new Point(2, mid), new Point(w - 2, mid));

        var data = History;
        if (data is not { Count: > 0 }) return;
        int len = Math.Max(2, HistoryLength);
        int n = Math.Min(data.Count, len);
        int offset = data.Count - n;
        double step = w / (len - 1);
        const double pad = 1.5; // 선 두께만큼 위아래 여유
        double Y(double p) => pad + (h - 2 * pad) * (1 - (double.IsFinite(p) ? Math.Clamp(p, 0, 100) : 0) / 100.0);

        double x0 = n == 1 ? w - step : w - (n - 1) * step;
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            var first = new Point(x0, Y(data[offset]));
            lc.BeginFigure(first, false, false);
            ac.BeginFigure(new Point(x0, h), true, true);
            ac.LineTo(first, false, false);
            if (n == 1)
            {
                var last = new Point(w, first.Y);
                lc.LineTo(last, true, false);
                ac.LineTo(last, false, false);
            }
            for (int i = 1; i < n; i++)
            {
                var pt = new Point(x0 + i * step, Y(data[offset + i]));
                lc.LineTo(pt, true, true);
                ac.LineTo(pt, false, false);
            }
            ac.LineTo(new Point(w, h), false, false);
        }
        line.Freeze();
        area.Freeze();

        var color = Fill is SolidColorBrush sb ? sb.Color : Colors.DeepSkyBlue;
        var gradient = new LinearGradientBrush(Color.FromArgb(0x90, color.R, color.G, color.B),
            Color.FromArgb(0x08, color.R, color.G, color.B), 90);
        gradient.Freeze();
        var pen = new Pen(Fill, 1.5) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        var clip = new RectangleGeometry(rect, radius, radius);
        clip.Freeze();
        dc.PushClip(clip);
        dc.DrawGeometry(gradient, null, area);
        dc.DrawGeometry(null, pen, line);
        dc.Pop();

        // 최근 값 위치 점
        dc.DrawEllipse(Fill, null, new Point(w - 2, Math.Clamp(Y(data[^1]), 2, h - 2)), 2, 2);
    }

    /// <summary>LED 미터: 칸을 왼쪽부터 켜며, 위치에 따라 기본색 → 노랑(70% 초과) → 빨강(90% 초과)으로 바뀐다.</summary>
    private void DrawSegments(DrawingContext dc, double w, double h, double v)
    {
        int count = Math.Max(4, SegmentCount);
        const double gap = 2;
        double segW = (w - gap * (count - 1)) / count;
        if (segW <= 0.5) return;
        int lit = (int)Math.Round(v * count);
        for (int i = 0; i < count; i++)
        {
            double pos = (i + 1) / (double)count;
            Brush brush = i < lit ? (pos > 0.9 ? LedHot : pos > 0.7 ? LedWarm : Fill) : Track;
            dc.DrawRoundedRectangle(brush, null, new Rect(i * (segW + gap), 0, segW, h), 1.5, 1.5);
        }
    }

    /// <summary>아크 게이지: 계기판처럼 아래가 트인 270° 호를 7시 반 방향부터 시계 방향으로 채우고 0·50·100% 눈금을 표시한다.</summary>
    private void DrawArc(DrawingContext dc, double w, double h, double v)
    {
        double t = Math.Max(1, Thickness);
        double r = Math.Min(w, h) / 2 - t / 2 - 3; // 바깥 눈금 자리
        if (r <= 0) return;
        var c = new Point(w / 2, h / 2);
        const double startDeg = -135, sweepDeg = 270;

        Point At(double deg, double radius)
        {
            double a = deg * Math.PI / 180;
            return new Point(c.X + radius * Math.Sin(a), c.Y - radius * Math.Cos(a));
        }
        StreamGeometry Arc(double fromDeg, double toDeg)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(At(fromDeg, r), false, false);
                ctx.ArcTo(At(toDeg, r), new Size(r, r), 0, toDeg - fromDeg > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }

        var trackPen = new Pen(Track, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        trackPen.Freeze();
        dc.DrawGeometry(null, trackPen, Arc(startDeg, startDeg + sweepDeg));

        var tick = new Pen(Track, 1);
        tick.Freeze();
        foreach (double f in new[] { 0.0, 0.5, 1.0 })
        {
            double deg = startDeg + sweepDeg * f;
            dc.DrawLine(tick, At(deg, r + t / 2 + 1), At(deg, r + t / 2 + 3));
        }

        if (v <= 0) return;
        var pen = new Pen(Fill, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, Arc(startDeg, startDeg + sweepDeg * Math.Max(v, 0.005)));
    }

    private static readonly Brush GraphBackground = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly Brush LedWarm = Frozen(Color.FromRgb(0xF5, 0xC5, 0x42));
    private static readonly Brush LedHot = Frozen(Color.FromRgb(0xF0, 0x5A, 0x4F));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
