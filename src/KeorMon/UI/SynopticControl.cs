using System.Drawing.Drawing2D;
using KeorMon.Ups;

namespace KeorMon.UI;

/// <summary>
/// Animated one-line diagram: mains → UPS (with battery) → connected devices.
/// Energy flow is drawn as moving dots whose speed follows the measured load;
/// paths, colors and the battery fill react to the live UPS state.
/// </summary>
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

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Background);

        var s = _status;
        bool onBattery = s?.OnBattery ?? false;
        bool mainsOk = s != null && !onBattery;
        double charge = s?.ChargePercent ?? 0;
        double load = s?.LoadPercent ?? 0;

        // --- layout ---------------------------------------------------------
        int midY = Height / 2;
        int nodeW = Math.Min(240, Width / 4);
        int gridX = Width / 6;
        int upsX = Width / 2;
        int devX = Width - Width / 6;

        var gridRect = new Rectangle(gridX - nodeW / 2, midY - 90, nodeW, 180);
        var upsRect = new Rectangle(upsX - nodeW / 2, midY - 120, nodeW, 240);
        var devRect = new Rectangle(devX - nodeW / 2, midY - 90, nodeW, 180);

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
        g.DrawString(headline, Theme.TitleFont, headBrush, 14, 10);
    }

    // ------------------------------------------------------------------ flows
    private void DrawFlow(Graphics g, PointF[] line, Color color, bool active, int dotSpacing = 34)
    {
        using var track = new Pen(Color.FromArgb(70, color), 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
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
            g.FillEllipse(dot, p.X - 3.2f, p.Y - 3.2f, 6.4f, 6.4f);
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
    private static void DrawPanel(Graphics g, Rectangle r, string title, Color accent)
    {
        using var body = new SolidBrush(Theme.Panel);
        using var border = new Pen(accent, 1.6f);
        using var path = Rounded(r, 10);
        g.FillPath(body, path);
        g.DrawPath(border, path);
        using var tb = new SolidBrush(Theme.TextMuted);
        g.DrawString(title.ToUpperInvariant(), Theme.SmallFont, tb, r.X + 10, r.Y + 8);
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
        int cx = r.X + r.Width / 2, top = r.Y + 34, bottom = r.Bottom - 58;
        using var pen = new Pen(mainsOk ? accent : Theme.TextMuted, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, cx - 16, bottom, cx, top);
        g.DrawLine(pen, cx + 16, bottom, cx, top);
        g.DrawLine(pen, cx - 22, top + 16, cx + 22, top + 16);
        g.DrawLine(pen, cx - 14, top + 34, cx + 14, top + 34);
        g.DrawLine(pen, cx - 20, bottom, cx + 20, bottom);

        if (!mainsOk)
        {
            using var xPen = new Pen(Theme.Crit, 3f);
            g.DrawLine(xPen, cx - 22, top + 2, cx + 22, bottom - 2);
            g.DrawLine(xPen, cx + 22, top + 2, cx - 22, bottom - 2);
        }

        using var val = new SolidBrush(mainsOk ? Theme.TextPrimary : Theme.Crit);
        string text = s?.InputVoltage is { } v && mainsOk
            ? $"{v:0.#} V  ·  {s.InputFrequency:0.#} Hz"
            : L10n.T("syn_absent");
        var sz = g.MeasureString(text, Theme.LabelFont);
        g.DrawString(text, Theme.LabelFont, val, r.X + (r.Width - sz.Width) / 2, r.Bottom - 42);
    }

    private void DrawUpsNode(Graphics g, Rectangle r, UpsStatus? s, bool onBattery, double charge)
    {
        var accent = _assessment is { Level: Severity.Critical } ? Theme.Crit
                   : onBattery ? Theme.Warn
                   : Theme.Ok;
        DrawPanel(g, r, s?.Model ?? "UPS", accent);

        // battery glyph, vertical, filled by charge
        var batt = new Rectangle(r.X + r.Width / 2 - 34, r.Y + 46, 68, 108);
        using var bPen = new Pen(accent, 2.2f);
        g.DrawRectangle(bPen, batt);
        using (var capBrush = new SolidBrush(accent))
            g.FillRectangle(capBrush, batt.X + batt.Width / 2 - 12, batt.Y - 8, 24, 6);

        var fillColor = charge <= 20 ? Theme.Crit : charge <= 50 ? Theme.Warn : Theme.Ok;
        int fillH = (int)((batt.Height - 8) * Math.Clamp(charge / 100.0, 0, 1));
        int innerX = batt.X + 4, innerW = batt.Width - 8;
        int fillTop = batt.Bottom - 4 - fillH;
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
            int headroom = fillTop - (batt.Y + 4);
            if (headroom > 4)
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
            var bolt = new[] {
                new PointF(bcx + 7, bcy - 26), new PointF(bcx - 11, bcy + 5), new PointF(bcx - 1, bcy + 5),
                new PointF(bcx - 7, bcy + 26), new PointF(bcx + 11, bcy - 5), new PointF(bcx + 1, bcy - 5) };
            using var boltFill = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
            using var boltOutline = new Pen(Color.FromArgb(160, 20, 22, 28), 2f) { LineJoin = LineJoin.Round };
            g.FillPolygon(boltFill, bolt);
            g.DrawPolygon(boltOutline, bolt);
        }
        else if (onBattery && fillH > 8)
        {
            // draining band moving downward inside the fill
            int bandH = Math.Min(14, fillH / 3);
            int y = fillTop + (int)((fillH - bandH) * _phase);
            using var band = new SolidBrush(Color.FromArgb(90, 12, 13, 16));
            g.FillRectangle(band, innerX, y, innerW, bandH);
            // pulse the outline while discharging
            int alpha = (int)(90 + 80 * Math.Sin(_phase * Math.PI * 2));
            using var pulse = new Pen(Color.FromArgb(alpha, Theme.Warn), 4f);
            g.DrawRectangle(pulse, batt.X - 4, batt.Y - 12, batt.Width + 8, batt.Height + 16);
        }

        using var big = new SolidBrush(Theme.TextPrimary);
        using var muted = new SolidBrush(Theme.TextMuted);

        // Charge % stays the big value in every state.
        string pct = s?.ChargePercent is { } c ? $"{c:0.0}%" : "—";
        var pctSize = g.MeasureString(pct, Theme.ValueFont);
        g.DrawString(pct, Theme.ValueFont, big, r.X + (r.Width - pctSize.Width) / 2, batt.Bottom + 6);

        if (onBattery)
        {
            // On battery the remaining runtime is what matters: medium-bold,
            // severity-tinted and gently pulsing right under the percentage.
            var rtColor = _assessment?.Level == Severity.Critical ? Theme.Crit : Theme.Warn;
            int alpha = (int)(165 + 90 * Math.Sin(_phase * Math.PI * 2));
            using var rtBrush = new SolidBrush(Color.FromArgb(alpha, rtColor));
            string rtText = L10n.F("syn_runtime_big", s?.RuntimeMinutes is { } rtMin ? $"{rtMin:0.#}" : "—");
            var rtSize = g.MeasureString(rtText, Theme.MediumFont);
            g.DrawString(rtText, Theme.MediumFont, rtBrush, r.X + (r.Width - rtSize.Width) / 2, batt.Bottom + 42);

            string line3 = s?.BatteryVoltage is { } bv ? L10n.F("syn_battv", $"{bv:0.#}") : "";
            var l3 = g.MeasureString(line3, Theme.SmallFont);
            g.DrawString(line3, Theme.SmallFont, muted, r.X + (r.Width - l3.Width) / 2, batt.Bottom + 68);
        }
        else
        {
            // charging: show the estimated time to full instead of the static runtime
            string line2 = s?.RechargeEtaMinutes is { } eta
                ? L10n.F("recharge_eta", eta)
                : s?.RuntimeMinutes is { } rt ? L10n.F("syn_runtime", $"{rt:0.#}") : "";
            string line3 = s?.BatteryVoltage is { } bv ? L10n.F("syn_battv", $"{bv:0.#}") : "";
            var l2 = g.MeasureString(line2, Theme.SmallFont);
            g.DrawString(line2, Theme.SmallFont, muted, r.X + (r.Width - l2.Width) / 2, batt.Bottom + 44);
            var l3 = g.MeasureString(line3, Theme.SmallFont);
            g.DrawString(line3, Theme.SmallFont, muted, r.X + (r.Width - l3.Width) / 2, batt.Bottom + 60);
        }
    }

    private void DrawDevicesNode(Graphics g, Rectangle r, UpsStatus? s, double load)
    {
        var accent = load >= 90 ? Theme.Crit : Theme.SeriesWatts;
        DrawPanel(g, r, L10n.T("syn_devices"), accent);

        // monitor + tower glyphs
        int cx = r.X + r.Width / 2, gy = r.Y + 42;
        using var pen = new Pen(Theme.TextPrimary, 2f);
        g.DrawRectangle(pen, cx - 44, gy, 52, 36);                       // screen
        g.DrawLine(pen, cx - 24, gy + 36, cx - 24, gy + 44);             // stand
        g.DrawLine(pen, cx - 34, gy + 44, cx - 6, gy + 44);
        g.DrawRectangle(pen, cx + 18, gy - 2, 22, 48);                   // tower
        g.DrawLine(pen, cx + 23, gy + 6, cx + 35, gy + 6);

        // load bar
        var bar = new Rectangle(r.X + 20, r.Bottom - 66, r.Width - 40, 10);
        using var track = new SolidBrush(Theme.GridLine);
        g.FillRectangle(track, bar);
        int w = (int)(bar.Width * Math.Clamp(load / 100.0, 0, 1));
        if (w > 0)
        {
            using var fill = new SolidBrush(accent);
            g.FillRectangle(fill, bar.X, bar.Y, w, bar.Height);
        }

        using var val = new SolidBrush(Theme.TextPrimary);
        string text = s is null ? "—"
            : $"{s.OutputActivePowerW:0} W  ·  {L10n.T("syn_load")} {load:0}%  ·  {s.OutputVoltage:0.#} V";
        var sz = g.MeasureString(text, Theme.LabelFont);
        g.DrawString(text, Theme.LabelFont, val, r.X + (r.Width - sz.Width) / 2, r.Bottom - 46);
    }
}
