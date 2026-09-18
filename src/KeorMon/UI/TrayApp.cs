using System.Drawing.Drawing2D;
using KeorMon.Data;
using KeorMon.Monitor;
using KeorMon.Ups;

namespace KeorMon.UI;

/// <summary>
/// Application context that owns the tray icon, balloon alerts and the dashboard window.
/// The dashboard opens on double-click and closes to tray, so the monitor keeps running.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly MonitorEngine _engine;
    private readonly UpsDatabase _db;
    private readonly AppConfig _cfg;
    private readonly NotifyIcon _tray;
    private DashboardForm? _dashboard;
    private Severity _iconSeverity = Severity.Normal;
    private bool _iconOnBattery;
    private int _iconCharge = 100;

    public TrayApp(MonitorEngine engine, UpsDatabase db, AppConfig cfg)
    {
        _engine = engine;
        _db = db;
        _cfg = cfg;

        _tray = new NotifyIcon
        {
            Icon = BuildIcon(),
            Visible = true,
            Text = $"Legrand Keor SP UPS v{AppVersion.Short}",
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(L10n.T("tray_open"), null, (_, _) => ShowDashboard());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L10n.T("tray_exit"), null, (_, _) => ExitThread());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowDashboard();

        _engine.SampleTaken += OnSample;
        _engine.Alert += OnAlert;

        ShowDashboard(); // show the window on first launch
    }

    private void ShowDashboard()
    {
        if (_dashboard is { IsDisposed: false })
        {
            _dashboard.Show();
            _dashboard.WindowState = FormWindowState.Normal;
            _dashboard.Activate();
            return;
        }
        _dashboard = new DashboardForm(_engine, _db, _cfg);
        // Closing the window hides it; the app lives in the tray.
        _dashboard.FormClosing += (s, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                _dashboard!.Hide();
            }
        };
        _dashboard.Show();
    }

    private void OnSample(UpsStatus status, Assessment assessment)
    {
        _iconSeverity = assessment.Level;
        _iconOnBattery = status.OnBattery;
        _iconCharge = (int)(status.ChargePercent ?? 100);

        var tip = $"KeorMon — {status.PowerSourceLabel} {status.ChargePercent:0.0}% | {status.RuntimeMinutes:0.#} min | carico {status.LoadPercent:0}%";
        try
        {
            _tray.Text = tip.Length > 127 ? tip[..127] : tip;
            var old = _tray.Icon;
            _tray.Icon = BuildIcon();
            old?.Dispose();
        }
        catch { }
    }

    private void OnAlert(string title, string message, Severity level)
    {
        try
        {
            _tray.ShowBalloonTip(10000, title,
                message.Length > 250 ? message[..250] : message,
                level switch
                {
                    Severity.Critical => ToolTipIcon.Error,
                    Severity.Warning => ToolTipIcon.Warning,
                    _ => ToolTipIcon.Info,
                });
        }
        catch { }
    }

    /// <summary>Draws a 16x16 battery glyph tinted by severity, with a lightning bolt when on mains.</summary>
    private Icon BuildIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        var color = Theme.SeverityColor(_iconSeverity);
        using var pen = new Pen(color, 1.6f);
        using var brush = new SolidBrush(color);

        // battery body
        g.DrawRectangle(pen, 1, 4, 12, 8);
        g.FillRectangle(brush, 14, 6, 1.6f, 4);
        // fill level
        int w = (int)Math.Round(10 * Math.Clamp(_iconCharge / 100.0, 0, 1));
        if (w > 0) g.FillRectangle(brush, 2.5f, 5.5f, w, 5);

        if (!_iconOnBattery)
        {
            // small dark bolt = mains present
            using var bolt = new SolidBrush(Color.FromArgb(230, 20, 22, 28));
            g.FillPolygon(bolt, new[] { new PointF(8, 3), new PointF(5.5f, 8.5f), new PointF(7.5f, 8.5f), new PointF(6.5f, 13), new PointF(10.5f, 7), new PointF(8.3f, 7) });
        }

        var handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { NativeDestroyIcon(handle); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    private static extern bool NativeDestroyIcon(IntPtr handle);

    protected override void ExitThreadCore()
    {
        _engine.SampleTaken -= OnSample;
        _engine.Alert -= OnAlert;
        _tray.Visible = false;
        _tray.Dispose();
        _dashboard?.Dispose();
        base.ExitThreadCore();
    }
}
