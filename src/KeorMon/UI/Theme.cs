namespace KeorMon.UI;

/// <summary>Shared dark palette for the whole UI.</summary>
public static class Theme
{
    public static readonly Color Background   = Color.FromArgb(24, 26, 32);
    public static readonly Color Panel        = Color.FromArgb(32, 35, 43);
    public static readonly Color PanelBorder  = Color.FromArgb(52, 56, 66);
    public static readonly Color TextPrimary  = Color.FromArgb(235, 238, 245);
    public static readonly Color TextMuted    = Color.FromArgb(140, 148, 164);
    public static readonly Color GridLine     = Color.FromArgb(46, 50, 60);

    public static readonly Color Ok       = Color.FromArgb(64, 199, 129);   // green
    public static readonly Color InfoBlue = Color.FromArgb(86, 156, 255);   // blue
    public static readonly Color Warn     = Color.FromArgb(245, 184, 66);   // amber
    public static readonly Color Crit     = Color.FromArgb(240, 84, 84);    // red
    public static readonly Color Accent   = Color.FromArgb(120, 130, 255);

    public static readonly Color SeriesInputV  = Color.FromArgb(86, 156, 255);
    public static readonly Color SeriesOutputV = Color.FromArgb(64, 199, 129);
    public static readonly Color SeriesLoad    = Color.FromArgb(245, 184, 66);
    public static readonly Color SeriesWatts   = Color.FromArgb(196, 130, 255);
    public static readonly Color SeriesCharge  = Color.FromArgb(64, 199, 129);
    public static readonly Color SeriesBattV   = Color.FromArgb(255, 130, 172);
    public static readonly Color SeriesRuntime = Color.FromArgb(120, 200, 255);

    public static Font TitleFont  { get; } = new("Segoe UI Semibold", 11f);
    public static Font ValueFont  { get; } = new("Segoe UI", 22f, FontStyle.Bold);
    public static Font MediumFont { get; } = new("Segoe UI", 14f, FontStyle.Bold);
    public static Font SmallFont  { get; } = new("Segoe UI", 8.5f);
    public static Font LabelFont  { get; } = new("Segoe UI", 9.5f);

    /// <summary>
    /// The custom-painted controls draw in device pixels while their fonts are in
    /// points, so on a scaled display the text grows but raw pixel constants do not:
    /// glyphs, boxes and paddings must be multiplied by the control's own DPI ratio.
    /// </summary>
    public static float ScaleOf(this Control c) => c.DeviceDpi / 96f;

    /// <summary>Scales a 96-DPI design constant to the control's current DPI.</summary>
    public static int Sc(this Control c, int px) => (int)Math.Round(px * c.DeviceDpi / 96f);

    /// <inheritdoc cref="Sc(Control,int)"/>
    public static float Sc(this Control c, float px) => px * c.DeviceDpi / 96f;

    public static Color SeverityColor(Ups.Severity s) => s switch
    {
        Ups.Severity.Critical => Crit,
        Ups.Severity.Warning => Warn,
        Ups.Severity.Info => InfoBlue,
        _ => Ok,
    };
}
