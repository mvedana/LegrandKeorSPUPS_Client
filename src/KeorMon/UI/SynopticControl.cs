using System.Drawing.Drawing2D;
using KeorMon.Ups;

namespace KeorMon.UI;

/// <summary>
/// Animated one-line diagram: mains → UPS (with battery) → connected devices.
/// Energy flow is drawn as moving dots whose speed follows the measured load;
/// paths, colors and the battery fill react to the live UPS state.
/// </summary>
/// <remarks>
/// Everything here is painted by hand in device pixels, so every geometric constant
/// is a 96-DPI design value pushed through <see cref="Theme.Sc(Control,int)"/>: the
/// fonts are in points and grow on their own with the display scaling, and unscaled
/// boxes would end up too small for their own text. Node widths are measured from
/// the strings they must hold so a long "222,3 V · 50 Hz" can never spill out.
/// </remarks>
public sealed class SynopticControl : Control
{
    private UpsStatus? _status;
    private Assessment? _assessment;
    private float _phase;                       // animation phase 0..1
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };

    public SynopticControl()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = Theme.Background;
        _timer.Tick += (_, _) =>
        {
            // Dot speed scales with load: idle UPS ~ slow drift, heavy load ~ fast flow.
            var load = _status?.LoadPercent ?? 0;
            _phase = (_phase + 0.006f + 0.0009f * (float)load) % 1f;
            Invalidate();
        };
        _timer.Start();
    }

    public void UpdateStatus(UpsStatus status, Assessment assessment)
    {
        _status = status;
        _assessment = assessment;
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Background);

        var s = _status;
        bool onBattery = s?.OnBattery ?? false;
        bool mainsOk = s != null && !onBattery;
        double charge = s?.ChargePercent ?? 0;
        double load = s?.LoadPercent ?? 0;

        // --- layout ---------------------------------------------------------
        int midY = Height / 2;

        // Width is driven by the widest label any node has to show, so the boxes stay
        // readable at 125/150/200% where the point-sized fonts are much wider.
        float textW = Math.Max(MeasureWidest(g, Theme.LabelFont, GridText(s, mainsOk), DevicesText(s, load)),
                               MeasureWidest(g, Theme.SmallFont, UpsLine2(s, onBattery), UpsLine3(s)));
        int nodeW = (int)Math.Ceiling(textW) + this.Sc(28);
        nodeW = Math.Clamp(nodeW, this.Sc(200), Math.Max(this.Sc(120), (int)(Width / 3.4f)));

        // Heights follow the window too: a short or narrow window shrinks the boxes
        // instead of letting them run past the edges.
        int upsH = Math.Clamp(this.Sc(264), this.Sc(150), Math.Max(this.Sc(120), Height - this.Sc(56)));
        int nodeH = Math.Clamp(this.Sc(184), this.Sc(110), upsH - this.Sc(40));
        int gridX = Width / 6;
        int upsX = Width / 2;
        int devX = Width - Width / 6;

        var gridRect = new Rectangle(gridX - nodeW / 2, midY - nodeH / 2, nodeW, nodeH);
        var upsRect = new Rectangle(upsX - nodeW / 2, midY - upsH / 2, nodeW, upsH);
        var devRect = new Rectangle(devX - nodeW / 2, midY - nodeH / 2, nodeW, nodeH);

        // --- flow lines (behind the nodes) -----------------------------------
        var lineGridUps = new PointF[] { new(gridRect.Right, midY), new(upsRect.Left, midY) };
        var lineUpsDev = new PointF[] { new(upsRect.Right, midY), new(devRect.Left, midY) };

        if (mainsOk) DrawFlow(g, lineGridUps, Theme.SeriesInputV, active: true);
        else DrawFlow(g, lineGridUps, Theme.Crit, active: false); // dead feed
        // UPS → devices always powered while we can read it
        DrawFlow(g, lineUpsDev, onBattery ? Theme.Warn : Theme.SeriesOutputV, active: s != null);

        // --- nodes ------------------------------------------------------------
        DrawGridNode(g, gridRect, mainsOk, s);
        DrawUpsNode(g, upsRect, s, onBattery, charge);
        DrawDevicesNode(g, devRect, s, load);

        // --- header -----------------------------------------------------------
        string headline = s is null
            ? L10n.T("status_nodata")
            : _assessment is { Level: not Severity.Normal } a
                ? $"{s.PowerSourceLabel} — {string.Join("; ", a.Reasons)}"
                : $"{s.PowerSourceLabel} — {L10n.T("status_normal")}";
        using var headBrush = new SolidBrush(Theme.SeverityColor(_assessment?.Level ?? Severity.Normal));
        using var headFont = Fit(g, headline, Theme.TitleFont, Width - this.Sc(28));
        g.DrawString(headline, headFont, headBrush, this.Sc(14), this.Sc(10));
    }

    /// <summary>
    /// Returns <paramref name="font"/> shrunk just enough for <paramref name="text"/> to
    /// fit <paramref name="maxWidth"/>. The caller always disposes the result, so a font
    /// that already fits is handed back as a clone.
    /// </summary>
    private static Font Fit(Graphics g, string text, Font font, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0) return (Font)font.Clone();
        float w = g.MeasureString(text, font).Width;
        if (w <= maxWidth) return (Font)font.Clone();
        return new Font(font.FontFamily, Math.Max(5.5f, font.Size * maxWidth / w), font.Style);
    }

    /// <summary>Draws one centred line inside <paramref name="r"/>, shrinking it to fit; returns its height.</summary>
    private float DrawCentered(Graphics g, string text, Font font, Brush brush, Rectangle r, float y)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        using var f = Fit(g, text, font, r.Width - this.Sc(16f));
        var sz = g.MeasureString(text, f);
        g.DrawString(text, f, brush, r.X + (r.Width - sz.Width) / 2, y);
        return sz.Height;
    }

    private static float MeasureWidest(Graphics g, Font font, params string[] texts)
    {
        float w = 0;
        foreach (var t in texts)
            if (!string.IsNullOrEmpty(t)) w = Math.Max(w, g.MeasureString(t, font).Width);
        return w;
    }

    // The label strings live in one place so the layout pass can measure exactly what
    // the draw pass will later write.
    private static string GridText(UpsStatus? s, bool mainsOk) =>
        s?.InputVoltage is { } v && mainsOk
            ? $"{v:0.#} V  ·  {s.InputFrequency:0.#} Hz"
            : L10n.T("syn_absent");

    private static string DevicesText(UpsStatus? s, double load) =>
        s is null ? "—"
            : $"{s.OutputActivePowerW:0} W  ·  {L10n.T("syn_load")} {load:0}%  ·  {s.OutputVoltage:0.#} V";

    private static string UpsLine2(UpsStatus? s, bool onBattery) =>
        onBattery
            ? L10n.F("syn_runtime_big", s?.RuntimeMinutes is { } rtMin ? $"{rtMin:0.#}" : "—")
            : s?.RechargeEtaMinutes is { } eta ? L10n.F("recharge_eta", eta)
            : s?.RuntimeMinutes is { } rt ? L10n.F("syn_runtime", $"{rt:0.#}") : "";

    private static string UpsLine3(UpsStatus? s) =>
        s?.BatteryVoltage is { } bv ? L10n.F("syn_battv", $"{bv:0.#}") : "";

    // ------------------------------------------------------------------ flows
    private void DrawFlow(Graphics g, PointF[] line, Color color, bool active)
    {
        float dotSpacing = this.Sc(34f);
        float dotR = this.Sc(3.2f);
        using var track = new Pen(Color.FromArgb(70, color), this.Sc(4f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (!active) track.DashStyle = DashStyle.Dash;
        g.DrawLines(track, line);
        if (!active) return;

        using var dot = new SolidBrush(color);
        float totalLen = 0;
        for (int i = 1; i < line.Length; i++)
            totalLen += Distance(line[i - 1], line[i]);

        for (float d = _phase * dotSpacing; d < totalLen; d += dotSpacing)
        {
            var p = PointAt(line, d);
            g.FillEllipse(dot, p.X - dotR, p.Y - dotR, dotR * 2, dotR * 2);
        }
    }

    private static float Distance(PointF a, PointF b) =>
        (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static PointF PointAt(PointF[] line, float dist)
    {
        for (int i = 1; i < line.Length; i++)
        {
            float seg = Distance(line[i - 1], line[i]);
            if (dist <= seg && seg > 0)
            {
                float t = dist / seg;
                return new PointF(line[i - 1].X + (line[i].X - line[i - 1].X) * t,
                                  line[i - 1].Y + (line[i].Y - line[i - 1].Y) * t);
            }
            dist -= seg;
        }
        return line[^1];
    }

    // ------------------------------------------------------------------ nodes
    private void DrawPanel(Graphics g, Rectangle r, string title, Color accent)
    {
        using var body = new SolidBrush(Theme.Panel);
        using var border = new Pen(accent, this.Sc(1.6f));
        using var path = Rounded(r, this.Sc(10));
        g.FillPath(body, path);
        g.DrawPath(border, path);
        using var tb = new SolidBrush(Theme.TextMuted);
        g.DrawString(title.ToUpperInvariant(), Theme.SmallFont, tb, r.X + this.Sc(10), r.Y + this.Sc(8));
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void DrawGridNode(Graphics g, Rectangle r, bool mainsOk, UpsStatus? s)
    {
        var accent = mainsOk ? Theme.SeriesInputV : Theme.Crit;
        DrawPanel(g, r, L10n.T("syn_grid"), accent);

        // pylon glyph
        int cx = r.X + r.Width / 2, top = r.Y + this.Sc(36);
        int bottom = Math.Max(top + this.Sc(30), r.Bottom - this.Sc(60));
        using var pen = new Pen(mainsOk ? accent : Theme.TextMuted, this.Sc(2.2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, cx - this.Sc(16), bottom, cx, top);
        g.DrawLine(pen, cx + this.Sc(16), bottom, cx, top);
        g.DrawLine(pen, cx - this.Sc(22), top + this.Sc(16), cx + this.Sc(22), top + this.Sc(16));
        g.DrawLine(pen, cx - this.Sc(14), top + this.Sc(34), cx + this.Sc(14), top + this.Sc(34));
        g.DrawLine(pen, cx - this.Sc(20), bottom, cx + this.Sc(20), bottom);

        if (!mainsOk)
        {
            using var xPen = new Pen(Theme.Crit, this.Sc(3f));
            g.DrawLine(xPen, cx - this.Sc(22), top + this.Sc(2), cx + this.Sc(22), bottom - this.Sc(2));
            g.DrawLine(xPen, cx + this.Sc(22), top + this.Sc(2), cx - this.Sc(22), bottom - this.Sc(2));
        }

        using var val = new SolidBrush(mainsOk ? Theme.TextPrimary : Theme.Crit);
        string text = GridText(s, mainsOk);
        using var valFont = Fit(g, text, Theme.LabelFont, r.Width - this.Sc(16f));
        DrawCentered(g, text, valFont, val, r, r.Bottom - this.Sc(12) - g.MeasureString(text, valFont).Height);
    }

    private void DrawUpsNode(Graphics g, Rectangle r, UpsStatus? s, bool onBattery, double charge)
    {
        var accent = _assessment is { Level: Severity.Critical } ? Theme.Crit
                   : onBattery ? Theme.Warn
                   : Theme.Ok;
        DrawPanel(g, r, s?.Model ?? "UPS", accent);

        // Battery glyph: the three text rows underneath get their measured height
        // first, and whatever vertical space is left becomes the glyph.
        float textBlock = g.MeasureString("0", Theme.ValueFont).Height
                        + g.MeasureString("0", onBattery ? Theme.MediumFont : Theme.SmallFont).Height
                        + g.MeasureString("0", Theme.SmallFont).Height + this.Sc(14);
        int battH = Math.Clamp((int)(r.Height - this.Sc(58) - textBlock), this.Sc(40), this.Sc(108));
        int battW = Math.Min(this.Sc(68), Math.Max(this.Sc(28), r.Width / 3));
        var batt = new Rectangle(r.X + r.Width / 2 - battW / 2, r.Y + this.Sc(46), battW, battH);
        using var bPen = new Pen(accent, this.Sc(2.2f));
        g.DrawRectangle(bPen, batt);
        using (var capBrush = new SolidBrush(accent))
            g.FillRectangle(capBrush, batt.X + batt.Width / 2 - this.Sc(12), batt.Y - this.Sc(8), this.Sc(24), this.Sc(6));

        var fillColor = charge <= 20 ? Theme.Crit : charge <= 50 ? Theme.Warn : Theme.Ok;
        int inset = this.Sc(4);
        int fillH = (int)((batt.Height - inset * 2) * Math.Clamp(charge / 100.0, 0, 1));
        int innerX = batt.X + inset, innerW = batt.Width - inset * 2;
        int fillTop = batt.Bottom - inset - fillH;
        if (fillH > 0)
        {
            using var fill = new SolidBrush(Color.FromArgb(200, fillColor));
            g.FillRectangle(fill, innerX, fillTop, innerW, fillH);
        }

        // Phone-style animation of the battery content:
        //   charging    → a translucent wave grows upward from the current level, then restarts
        //   discharging → a translucent band sweeps downward through the fill
        //   stable      → static fill
        bool charging = !onBattery && s != null && charge < 100;
        if (charging)
        {
            int headroom = fillTop - (batt.Y + inset);
            if (headroom > inset)
            {
                int waveH = (int)(headroom * _phase);
                if (waveH > 0)
                {
                    // fades out as it approaches the top
                    int alpha = (int)(110 * (1 - _phase));
                    using var wave = new SolidBrush(Color.FromArgb(Math.Max(20, alpha), Theme.Ok));
                    g.FillRectangle(wave, innerX, fillTop - waveH, innerW, waveH);
                }
            }
            // lightning bolt: universally "charging"
            var bcx = batt.X + batt.Width / 2f; var bcy = batt.Y + batt.Height / 2f;
            float u = this.Sc(1f);
            var bolt = new[] {
                new PointF(bcx + 7 * u, bcy - 26 * u), new PointF(bcx - 11 * u, bcy + 5 * u), new PointF(bcx - 1 * u, bcy + 5 * u),
                new PointF(bcx - 7 * u, bcy + 26 * u), new PointF(bcx + 11 * u, bcy - 5 * u), new PointF(bcx + 1 * u, bcy - 5 * u) };
            using var boltFill = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
            using var boltOutline = new Pen(Color.FromArgb(160, 20, 22, 28), this.Sc(2f)) { LineJoin = LineJoin.Round };
            g.FillPolygon(boltFill, bolt);
            g.DrawPolygon(boltOutline, bolt);
        }
        else if (onBattery && fillH > this.Sc(8))
        {
            // draining band moving downward inside the fill
            int bandH = Math.Min(this.Sc(14), fillH / 3);
            int y = fillTop + (int)((fillH - bandH) * _phase);
            using var band = new SolidBrush(Color.FromArgb(90, 12, 13, 16));
            g.FillRectangle(band, innerX, y, innerW, bandH);
            // pulse the outline while discharging
            int alpha = (int)(90 + 80 * Math.Sin(_phase * Math.PI * 2));
            using var pulse = new Pen(Color.FromArgb(alpha, Theme.Warn), this.Sc(4f));
            g.DrawRectangle(pulse, batt.X - this.Sc(4), batt.Y - this.Sc(12), batt.Width + this.Sc(8), batt.Height + this.Sc(16));
        }

        using var big = new SolidBrush(Theme.TextPrimary);
        using var muted = new SolidBrush(Theme.TextMuted);

        // The three text rows are stacked from the measured height of the row above,
        // so they keep their spacing whatever the DPI does to the fonts.
        string pct = s?.ChargePercent is { } c ? $"{c:0.0}%" : "—";
        float y2 = batt.Bottom + this.Sc(6);
        y2 += DrawCentered(g, pct, Theme.ValueFont, big, r, y2) + this.Sc(2);

        string line2 = UpsLine2(s, onBattery);
        string line3 = UpsLine3(s);

        if (onBattery)
        {
            // On battery the remaining runtime is what matters: medium-bold,
            // severity-tinted and gently pulsing right under the percentage.
            var rtColor = _assessment?.Level == Severity.Critical ? Theme.Crit : Theme.Warn;
            int alpha = (int)(165 + 90 * Math.Sin(_phase * Math.PI * 2));
            using var rtBrush = new SolidBrush(Color.FromArgb(alpha, rtColor));
            y2 += DrawCentered(g, line2, Theme.MediumFont, rtBrush, r, y2) + this.Sc(2);
            DrawCentered(g, line3, Theme.SmallFont, muted, r, y2);
        }
        else
        {
            // charging: show the estimated time to full instead of the static runtime
            y2 += DrawCentered(g, line2, Theme.SmallFont, muted, r, y2);
            DrawCentered(g, line3, Theme.SmallFont, muted, r, y2);
        }
    }

    private void DrawDevicesNode(Graphics g, Rectangle r, UpsStatus? s, double load)
    {
        var accent = load >= 90 ? Theme.Crit : Theme.SeriesWatts;
        DrawPanel(g, r, L10n.T("syn_devices"), accent);

        // monitor + tower glyphs
        int cx = r.X + r.Width / 2, gy = r.Y + this.Sc(42);
        using var pen = new Pen(Theme.TextPrimary, this.Sc(2f));
        g.DrawRectangle(pen, cx - this.Sc(44), gy, this.Sc(52), this.Sc(36));                       // screen
        g.DrawLine(pen, cx - this.Sc(24), gy + this.Sc(36), cx - this.Sc(24), gy + this.Sc(44));    // stand
        g.DrawLine(pen, cx - this.Sc(34), gy + this.Sc(44), cx - this.Sc(6), gy + this.Sc(44));
        g.DrawRectangle(pen, cx + this.Sc(18), gy - this.Sc(2), this.Sc(22), this.Sc(48));          // tower
        g.DrawLine(pen, cx + this.Sc(23), gy + this.Sc(6), cx + this.Sc(35), gy + this.Sc(6));

        using var val = new SolidBrush(Theme.TextPrimary);
        string text = DevicesText(s, load);
        using var valFont = Fit(g, text, Theme.LabelFont, r.Width - this.Sc(16f));
        float textY = r.Bottom - this.Sc(12) - g.MeasureString(text, valFont).Height;
        DrawCentered(g, text, valFont, val, r, textY);

        // load bar, sitting just above the value line
        int barH = this.Sc(10);
        int barY = Math.Max(r.Y + this.Sc(30), (int)(textY - this.Sc(10)) - barH);
        var bar = new Rectangle(r.X + this.Sc(20), barY, Math.Max(this.Sc(20), r.Width - this.Sc(40)), barH);
        using var track = new SolidBrush(Theme.GridLine);
        g.FillRectangle(track, bar);
        int w = (int)(bar.Width * Math.Clamp(load / 100.0, 0, 1));
        if (w > 0)
        {
            using var fill = new SolidBrush(accent);
            g.FillRectangle(fill, bar.X, bar.Y, w, bar.Height);
        }
    }
}
