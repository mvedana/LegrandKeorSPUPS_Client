using System.Drawing.Drawing2D;

namespace KeorMon.UI;

public sealed record ChartSeries(string Name, Color Color, IReadOnlyList<(DateTime T, double V)> Points, string Unit = "");

/// <summary>
/// Lightweight GDI+ time-series chart: multiple series, auto-scaled Y, time X axis,
/// optional shaded "on battery" bands, hover cursor with values.
/// </summary>
public sealed class ChartControl : Control
{
    private List<ChartSeries> _series = new();
    private List<(DateTime From, DateTime To)> _bands = new();
    private string _title = "";
    private double? _fixedMin, _fixedMax;
    private Point? _hover;

    private static readonly Padding Plot = new(52, 30, 14, 24);

    public ChartControl()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = Theme.Panel;
    }

    public void SetData(string title, List<ChartSeries> series,
        List<(DateTime, DateTime)>? onBatteryBands = null,
        double? fixedMin = null, double? fixedMax = null)
    {
        _title = title;
        _series = series;
        _bands = onBatteryBands ?? new();
        _fixedMin = fixedMin;
        _fixedMax = fixedMax;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e) { _hover = e.Location; Invalidate(); base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = null; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Panel);

        using (var border = new Pen(Theme.PanelBorder)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        using (var titleBrush = new SolidBrush(Theme.TextPrimary))
            g.DrawString(_title, Theme.TitleFont, titleBrush, 10, 6);

        var rect = new Rectangle(Plot.Left, Plot.Top, Width - Plot.Left - Plot.Right, Height - Plot.Top - Plot.Bottom);
        if (rect.Width < 40 || rect.Height < 30) return;

        var allPoints = _series.SelectMany(s => s.Points).ToList();
        if (allPoints.Count == 0)
        {
            using var muted = new SolidBrush(Theme.TextMuted);
            var msg = "nessun dato nel periodo";
            var size = g.MeasureString(msg, Theme.LabelFont);
            g.DrawString(msg, Theme.LabelFont, muted, rect.Left + (rect.Width - size.Width) / 2, rect.Top + (rect.Height - size.Height) / 2);
            return;
        }

        var tMin = allPoints.Min(p => p.T);
        var tMax = allPoints.Max(p => p.T);
        if (tMax <= tMin) tMax = tMin.AddSeconds(1);

        double vMin = _fixedMin ?? allPoints.Min(p => p.V);
        double vMax = _fixedMax ?? allPoints.Max(p => p.V);
        if (_fixedMin is null && _fixedMax is null)
        {
            var pad = Math.Max((vMax - vMin) * 0.1, 0.5);
            vMin -= pad; vMax += pad;
        }
        if (vMax <= vMin) vMax = vMin + 1;

        float X(DateTime t) => rect.Left + (float)((t - tMin).TotalSeconds / (tMax - tMin).TotalSeconds) * rect.Width;
        float Y(double v) => rect.Bottom - (float)((v - vMin) / (vMax - vMin)) * rect.Height;

        // On-battery shading.
        using (var bandBrush = new SolidBrush(Color.FromArgb(36, Theme.Crit)))
            foreach (var (from, to) in _bands)
            {
                float x1 = Math.Max(rect.Left, X(from < tMin ? tMin : from));
                float x2 = Math.Min(rect.Right, X(to > tMax ? tMax : to));
                if (x2 > x1) g.FillRectangle(bandBrush, x1, rect.Top, x2 - x1, rect.Height);
            }

        // Grid + Y labels.
        using (var gridPen = new Pen(Theme.GridLine))
        using (var labelBrush = new SolidBrush(Theme.TextMuted))
        {
            const int hLines = 4;
            for (int i = 0; i <= hLines; i++)
            {
                double v = vMin + (vMax - vMin) * i / hLines;
                float y = Y(v);
                g.DrawLine(gridPen, rect.Left, y, rect.Right, y);
                var label = Math.Abs(vMax - vMin) < 5 ? v.ToString("0.0") : v.ToString("0");
                var sz = g.MeasureString(label, Theme.SmallFont);
                g.DrawString(label, Theme.SmallFont, labelBrush, rect.Left - sz.Width - 4, y - sz.Height / 2);
            }

            const int vLines = 5;
            for (int i = 0; i <= vLines; i++)
            {
                var t = tMin + TimeSpan.FromSeconds((tMax - tMin).TotalSeconds * i / vLines);
                float x = X(t);
                g.DrawLine(gridPen, x, rect.Top, x, rect.Bottom);
                var label = (tMax - tMin).TotalHours > 24 ? t.ToString("dd/MM HH:mm") : t.ToString("HH:mm");
                var sz = g.MeasureString(label, Theme.SmallFont);
                g.DrawString(label, Theme.SmallFont, labelBrush,
                    Math.Min(x - sz.Width / 2, rect.Right - sz.Width), rect.Bottom + 3);
            }
        }

        // Series: smooth cardinal spline through the samples, clipped to the plot
        // area (splines can overshoot), plus a marker on every measured point as
        // long as they are not too dense to read.
        var clip = g.Clip;
        g.SetClip(rect);
        foreach (var s in _series)
        {
            if (s.Points.Count == 0) continue;
            var pts = s.Points.Select(p => new PointF(X(p.T), Y(p.V))).ToArray();

            using var pen = new Pen(s.Color, 1.8f);
            if (pts.Length >= 3) g.DrawCurve(pen, pts, 0.45f);
            else if (pts.Length == 2) g.DrawLines(pen, pts);

            // markers only when average spacing leaves them distinguishable
            float avgSpacing = pts.Length > 1 ? (pts[^1].X - pts[0].X) / (pts.Length - 1) : float.MaxValue;
            if (avgSpacing >= 7f)
            {
                using var fill = new SolidBrush(s.Color);
                using var ring = new Pen(Theme.Panel, 1.4f);
                foreach (var p in pts)
                {
                    g.FillEllipse(fill, p.X - 2.6f, p.Y - 2.6f, 5.2f, 5.2f);
                    g.DrawEllipse(ring, p.X - 2.6f, p.Y - 2.6f, 5.2f, 5.2f);
                }
            }
        }
        g.Clip = clip;

        // Legend.
        float lx = rect.Left + 4, ly = rect.Top + 2;
        foreach (var s in _series)
        {
            using var b = new SolidBrush(s.Color);
            using var tb = new SolidBrush(Theme.TextPrimary);
            g.FillEllipse(b, lx, ly + 4, 7, 7);
            g.DrawString(s.Name, Theme.SmallFont, tb, lx + 10, ly);
            lx += 16 + g.MeasureString(s.Name, Theme.SmallFont).Width;
        }

        // Hover cursor.
        if (_hover is { } h && rect.Contains(h))
        {
            using var cursorPen = new Pen(Color.FromArgb(120, Theme.TextMuted)) { DashStyle = DashStyle.Dash };
            g.DrawLine(cursorPen, h.X, rect.Top, h.X, rect.Bottom);

            var tAt = tMin + TimeSpan.FromSeconds((tMax - tMin).TotalSeconds * (h.X - rect.Left) / rect.Width);
            var lines = new List<string> { tAt.ToString("dd/MM HH:mm:ss") };
            foreach (var s in _series)
            {
                var nearest = s.Points.OrderBy(p => Math.Abs((p.T - tAt).TotalSeconds)).FirstOrDefault();
                if (nearest.T != default) lines.Add($"{s.Name}: {nearest.V:0.#} {s.Unit}");
            }
            var text = string.Join("\n", lines);
            var size = g.MeasureString(text, Theme.SmallFont);
            var bx = Math.Min(h.X + 12, rect.Right - size.Width - 8);
            var by = Math.Min(h.Y, rect.Bottom - size.Height - 8);
            using var bg = new SolidBrush(Color.FromArgb(230, 18, 20, 25));
            using var fg = new SolidBrush(Theme.TextPrimary);
            g.FillRectangle(bg, bx, by, size.Width + 8, size.Height + 6);
            g.DrawString(text, Theme.SmallFont, fg, bx + 4, by + 3);
        }
    }
}
