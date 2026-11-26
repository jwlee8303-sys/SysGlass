using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SysGlass.Views;

/// <summary>그래프 한 점. 값이 없으면(NaN) 선을 끊는다.</summary>
public readonly record struct ChartPoint(DateTimeOffset Time, double Paged, double NonPaged);

/// <summary>
/// 페이징·비페이징 풀 사용량(MB)을 시간축으로 그리는 경량 그래프. 외부 차트 라이브러리 없이 직접 그린다.
/// 기준선(WarnMb)이 있으면 점선으로 표시한다.
/// </summary>
public sealed class MemoryChart : FrameworkElement
{
    private static readonly Brush PagedBrush = Frozen(Color.FromRgb(0x4C, 0xC2, 0xFF));
    private static readonly Brush NonPagedBrush = Frozen(Color.FromRgb(0xFF, 0xB8, 0x6B));
    private static readonly Brush GridBrush = Frozen(Color.FromRgb(0x2B, 0x2B, 0x30));
    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0x9D, 0xA0, 0xA6));
    private static readonly Brush WarnBrush = Frozen(Color.FromRgb(0xFF, 0xD2, 0x4D));
    private static readonly Typeface Face = new("Segoe UI");

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points),
        typeof(IReadOnlyList<ChartPoint>), typeof(MemoryChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WarnMbProperty = DependencyProperty.Register(nameof(WarnMb),
        typeof(double), typeof(MemoryChart), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartPoint>? Points { get => (IReadOnlyList<ChartPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public double WarnMb { get => (double)GetValue(WarnMbProperty); set => SetValue(WarnMbProperty, value); }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        const double left = 64, right = 12, top = 24, bottom = 26;
        double pw = w - left - right, ph = h - top - bottom;
        if (pw < 40 || ph < 40) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var pts = Points;
        Legend(dc, left, dpi);
        if (pts is null || pts.Count < 2)
        {
            Text(dc, "그래프를 그리려면 수집 데이터가 더 필요합니다.", left + 8, top + ph / 2 - 8, LabelBrush, dpi);
            return;
        }

        double max = pts.Max(p => Math.Max(Safe(p.Paged), Safe(p.NonPaged)));
        if (!double.IsNaN(WarnMb) && WarnMb < max * 3) max = Math.Max(max, WarnMb);
        max = NiceCeiling(Math.Max(max, 1));
        long t0 = pts[0].Time.UtcTicks, t1 = pts[^1].Time.UtcTicks;
        double span = Math.Max(1, t1 - t0);
        double X(DateTimeOffset t) => left + (t.UtcTicks - t0) / span * pw;
        double Y(double v) => top + ph - v / max * ph;

        var gridPen = new Pen(GridBrush, 1);
        for (int i = 0; i <= 4; i++)
        {
            double v = max * i / 4, y = Y(v);
            dc.DrawLine(gridPen, new Point(left, y), new Point(left + pw, y));
            Text(dc, Mb(v), 4, y - 8, LabelBrush, dpi);
        }
        Text(dc, pts[0].Time.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture), left, top + ph + 6, LabelBrush, dpi);
        var endText = Formatted(pts[^1].Time.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture), LabelBrush, dpi);
        dc.DrawText(endText, new Point(left + pw - endText.Width, top + ph + 6));

        if (!double.IsNaN(WarnMb) && WarnMb <= max)
        {
            var dash = new Pen(WarnBrush, 1) { DashStyle = DashStyles.Dash };
            dc.DrawLine(dash, new Point(left, Y(WarnMb)), new Point(left + pw, Y(WarnMb)));
        }
        Line(dc, pts, p => p.NonPaged, NonPagedBrush, X, Y);
        Line(dc, pts, p => p.Paged, PagedBrush, X, Y);
    }

    private static void Line(DrawingContext dc, IReadOnlyList<ChartPoint> pts, Func<ChartPoint, double> value, Brush brush,
        Func<DateTimeOffset, double> x, Func<double, double> y)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            bool open = false;
            foreach (var p in pts)
            {
                double v = value(p);
                if (double.IsNaN(v)) { open = false; continue; }
                var pt = new Point(x(p.Time), y(v));
                if (!open) { ctx.BeginFigure(pt, false, false); open = true; }
                else ctx.LineTo(pt, true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(brush, 1.6), geo);
    }

    private static void Legend(DrawingContext dc, double left, double dpi)
    {
        dc.DrawRectangle(PagedBrush, null, new Rect(left, 7, 10, 10));
        Text(dc, "페이징 풀", left + 14, 3, LabelBrush, dpi);
        dc.DrawRectangle(NonPagedBrush, null, new Rect(left + 90, 7, 10, 10));
        Text(dc, "비페이징 풀", left + 104, 3, LabelBrush, dpi);
        dc.DrawLine(new Pen(WarnBrush, 1) { DashStyle = DashStyles.Dash }, new Point(left + 190, 12), new Point(left + 204, 12));
        Text(dc, "페이징 풀 주의 기준", left + 208, 3, LabelBrush, dpi);
    }

    private static double Safe(double v) => double.IsNaN(v) ? 0 : v;

    private static double NiceCeiling(double v)
    {
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (double m in new[] { 1, 2, 2.5, 5, 10 })
            if (m * mag >= v) return m * mag;
        return 10 * mag;
    }

    private static string Mb(double mb) => mb >= 1000 ? (mb / 1024).ToString("0.#", CultureInfo.InvariantCulture) + " GB" : mb.ToString("0", CultureInfo.InvariantCulture) + " MB";

    private static FormattedText Formatted(string s, Brush brush, double dpi) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 11, brush, dpi);

    private static void Text(DrawingContext dc, string s, double x, double y, Brush brush, double dpi) =>
        dc.DrawText(Formatted(s, brush, dpi), new Point(x, y));
}
