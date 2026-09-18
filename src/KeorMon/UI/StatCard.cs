namespace KeorMon.UI;

/// <summary>A dashboard tile: big value, label, optional progress bar.</summary>
public sealed class StatCard : Control
{
    private string _value = "—";
    private string _sub = "";
    private double? _fraction;          // 0..1 fills the bar
    private Color _accent = Theme.Ok;

    public StatCard(string label)
    {
        Label = label;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = Theme.Panel;
        MinimumSize = new Size(this.Sc(150), this.Sc(92));
    }

    public string Label { get; }

    public void SetValue(string value, string sub = "", double? fraction = null, Color? accent = null)
    {
        _value = value;
        _sub = sub;
        _fraction = fraction;
        _accent = accent ?? Theme.Ok;
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        MinimumSize = new Size(this.Sc(150), this.Sc(92));
        Invalidate();
    }

    // Painted by hand in device pixels, so every 96-DPI constant below goes through
    // Theme.Sc to keep its proportions next to the point-sized fonts.
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Panel);
        using (var border = new Pen(Theme.PanelBorder)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        using var muted = new SolidBrush(Theme.TextMuted);
        using var strong = new SolidBrush(Theme.TextPrimary);
        using var accentBrush = new SolidBrush(_accent);

        g.DrawString(Label.ToUpperInvariant(), Theme.SmallFont, muted, this.Sc(12), this.Sc(10));
        g.DrawString(_value, Theme.ValueFont, accentBrush, this.Sc(8), this.Sc(26));
        if (!string.IsNullOrEmpty(_sub))
            g.DrawString(_sub, Theme.SmallFont, muted, this.Sc(12),
                Height - g.MeasureString(_sub, Theme.SmallFont).Height - (_fraction.HasValue ? this.Sc(20) : this.Sc(6)));

        if (_fraction is { } f)
        {
            var barRect = new Rectangle(this.Sc(12), Height - this.Sc(16), Width - this.Sc(24), this.Sc(6));
            using var track = new SolidBrush(Theme.GridLine);
            g.FillRectangle(track, barRect);
            var w = (int)(barRect.Width * Math.Clamp(f, 0, 1));
            if (w > 0) g.FillRectangle(accentBrush, barRect.X, barRect.Y, w, barRect.Height);
        }
    }
}
