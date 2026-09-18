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
        MinimumSize = new Size(150, 92);
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

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Theme.Panel);
        using (var border = new Pen(Theme.PanelBorder)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        using var muted = new SolidBrush(Theme.TextMuted);
        using var strong = new SolidBrush(Theme.TextPrimary);
        using var accentBrush = new SolidBrush(_accent);

        g.DrawString(Label.ToUpperInvariant(), Theme.SmallFont, muted, 12, 10);
        g.DrawString(_value, Theme.ValueFont, accentBrush, 8, 26);
        if (!string.IsNullOrEmpty(_sub))
            g.DrawString(_sub, Theme.SmallFont, muted, 12, Height - (( _fraction.HasValue) ? 34 : 22));

        if (_fraction is { } f)
        {
            var barRect = new Rectangle(12, Height - 16, Width - 24, 6);
            using var track = new SolidBrush(Theme.GridLine);
            g.FillRectangle(track, barRect);
            var w = (int)(barRect.Width * Math.Clamp(f, 0, 1));
            if (w > 0) g.FillRectangle(accentBrush, barRect.X, barRect.Y, w, barRect.Height);
        }
    }
}
