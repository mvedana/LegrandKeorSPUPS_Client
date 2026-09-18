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

    // 96-DPI design margins around the plot area, scaled per control in OnPaint:
    // the axis labels are point-sized and grow with the display scaling.
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

    /// <summary>
    /// How long a gap between two samples counts as "the monitor was not running":
    /// three times the usual spacing of this series (its median step), never below
    /// 20 s so a single late sample does not chop the line.
    /// </summary>
    private static TimeSpan GapThreshold(IReadOnlyList<(DateTime T, double V)> points)
    {
        if (points.Count < 3) return TimeSpan.MaxValue;
        var steps = new double[points.Count - 1];
        for (int i = 1; i < points.Count; i++) steps[i - 1] = (points[i].T - points[i - 1].T).TotalSeconds;
        Array.Sort(steps);
        double median = steps[steps.Length / 2];
        return TimeSpan.FromSeconds(Math.Max(20, median * 3));
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Invalidate(); }

    protected override void OnMouseMove(MouseEventArgs e) { _hover = e.Location; Invalidate(); base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = null; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Panel);

        using (var border = new Pen(Theme.PanelBorder)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        using (var titleBrush = new SolidBrush(Theme.TextPrimary))
            g.DrawString(_title, Theme.TitleFont, titleBrush, this.Sc(10), this.Sc(6));

        int padL = this.Sc(Plot.Left), padT = this.Sc(Plot.Top), padR = this.Sc(Plot.Right), padB = this.Sc(Plot.Bottom);
        var rect = new Rectangle(padL, padT, Width - padL - padR, Height - padT - padB);
        if (rect.Width < this.Sc(40) || rect.Height < this.Sc(30)) return;

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
                g.DrawString(label, Theme.SmallFont, labelBrush, rect.Left - sz.Width - this.Sc(4), y - sz.Height / 2);
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
                    Math.Min(x - sz.Width / 2, rect.Right - sz.Width), rect.Bottom + this.Sc(3));
            }
        }

        // Series: straight segments between measured samples — a spline would invent
        // curvature and overshoot values the UPS never reported. Where the monitor was
        // not running the samples stop, so the line stops too: consecutive points more
        // than a gap apart start a new segment instead of being bridged.
        var clip = g.Clip;
        g.SetClip(rect);
        foreach (var s in _series)
        {
            if (s.Points.Count == 0) continue;
            var gap = GapThreshold(s.Points);

            using var pen = new Pen(s.Color, this.Sc(1.8f));
            using var fill = new SolidBrush(s.Color);
            using var ring = new Pen(Theme.Panel, this.Sc(1.4f));

            // Every reading gets a dot; the dot shrinks with the spacing so a dense
            // range shows the samples without the line turning into a solid blob,
            // and only below ~3 px do they drop out entirely.
            float span = X(s.Points[^1].T) - X(s.Points[0].T);
            float step = s.Points.Count > 1 ? span / (s.Points.Count - 1) : float.MaxValue;
            float mr = Math.Clamp(step / 2.4f, this.Sc(1.5f), this.Sc(2.8f));
            bool markers = step >= this.Sc(3f);
            bool ringed = step >= this.Sc(7f);   // the panel-colored ring needs room

            var segment = new List<PointF>();
            void Flush()
            {
                if (segment.Count >= 2) g.DrawLines(pen, segment.ToArray());
                else if (segment.Count == 1 && !markers)
                {
                    // a lone sample would otherwise be invisible
                    var p = segment[0];
                    g.FillEllipse(fill, p.X - mr, p.Y - mr, mr * 2, mr * 2);
                }
                if (markers)
                    foreach (var p in segment)
                    {
                        g.FillEllipse(fill, p.X - mr, p.Y - mr, mr * 2, mr * 2);
                        if (ringed) g.DrawEllipse(ring, p.X - mr, p.Y - mr, mr * 2, mr * 2);
                    }
                segment.Clear();
            }

            for (int i = 0; i < s.Points.Count; i++)
            {
                if (i > 0 && s.Points[i].T - s.Points[i - 1].T > gap) Flush();
                segment.Add(new PointF(X(s.Points[i].T), Y(s.Points[i].V)));
            }
            Flush();
        }
        g.Clip = clip;

        // Legend.
        float lx = rect.Left + this.Sc(4f), ly = rect.Top + this.Sc(2f), dotD = this.Sc(7f);
        foreach (var s in _series)
        {
            using var b = new SolidBrush(s.Color);
            using var tb = new SolidBrush(Theme.TextPrimary);
            g.FillEllipse(b, lx, ly + this.Sc(4f), dotD, dotD);
            g.DrawString(s.Name, Theme.SmallFont, tb, lx + this.Sc(10f), ly);
            lx += this.Sc(16f) + g.MeasureString(s.Name, Theme.SmallFont).Width;
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
                if (s.Points.Count == 0) continue;
                var gap = GapThreshold(s.Points);
                var nearest = s.Points.OrderBy(p => Math.Abs((p.T - tAt).TotalSeconds)).First();
                // inside a hole in the history there is nothing to report
                if ((nearest.T - tAt).Duration() <= gap) lines.Add($"{s.Name}: {nearest.V:0.#} {s.Unit}");
            }
            var text = string.Join("\n", lines);
            var size = g.MeasureString(text, Theme.SmallFont);
            var bx = Math.Min(h.X + this.Sc(12f), rect.Right - size.Width - this.Sc(8f));
            var by = Math.Min(h.Y, rect.Bottom - size.Height - this.Sc(8f));
            using var bg = new SolidBrush(Color.FromArgb(230, 18, 20, 25));
            using var fg = new SolidBrush(Theme.TextPrimary);
            g.FillRectangle(bg, bx, by, size.Width + this.Sc(8f), size.Height + this.Sc(6f));
            g.DrawString(text, Theme.SmallFont, fg, bx + this.Sc(4f), by + this.Sc(3f));
        }
    }
}
