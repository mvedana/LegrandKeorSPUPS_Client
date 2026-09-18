using KeorMon.Data;
using KeorMon.Monitor;
using KeorMon.Ups;

namespace KeorMon.UI;

/// <summary>
/// Main window: live stat cards on top, history charts below (fed from SQLite),
/// an events tab and a settings tab.
/// </summary>
public sealed class DashboardForm : Form
{
    private readonly MonitorEngine _engine;
    private readonly UpsDatabase _db;
    private readonly AppConfig _cfg;

    private readonly StatCard _cardSource = new(L10n.T("card_power"));
    private readonly StatCard _cardCharge = new(L10n.T("card_charge"));
    private readonly StatCard _cardRuntime = new(L10n.T("card_runtime"));
    private readonly StatCard _cardLoad = new(L10n.T("card_load"));
    private readonly StatCard _cardInput = new(L10n.T("card_input"));
    private readonly StatCard _cardOutput = new(L10n.T("card_output"));
    private readonly StatCard _cardBattV = new(L10n.T("card_battery"));
    private readonly StatCard _cardPower = new(L10n.T("card_watts"));

    private readonly SynopticControl _synoptic = new();
    private readonly ChartControl _chartVoltage = new();
    private readonly ChartControl _chartLoad = new();
    private readonly ChartControl _chartBattery = new();
    private readonly ChartControl _chartRuntime = new();

    private readonly ComboBox _rangeBox = new();
    private readonly Label _statusLabel = new();
    private readonly ListView _eventsList = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new();

    private static readonly (string Label, TimeSpan Span)[] Ranges =
    {
        (L10n.T("range_10m"), TimeSpan.FromMinutes(10)),
        (L10n.T("range_1h"), TimeSpan.FromHours(1)),
        (L10n.T("range_6h"), TimeSpan.FromHours(6)),
        (L10n.T("range_24h"), TimeSpan.FromHours(24)),
        (L10n.T("range_7d"), TimeSpan.FromDays(7)),
        (L10n.T("range_30d"), TimeSpan.FromDays(30)),
    };

    public DashboardForm(MonitorEngine engine, UpsDatabase db, AppConfig cfg)
    {
        _engine = engine;
        _db = db;
        _cfg = cfg;

        Text = $"Legrand Keor SP UPS — v{AppVersion.Short}";
        BackColor = Theme.Background;
        ForeColor = Theme.TextPrimary;
        ClientSize = new Size(1180, 780);
        MinimumSize = new Size(900, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Theme.LabelFont;

        BuildLayout();

        _engine.SampleTaken += OnSample;
        FormClosed += (_, _) => _engine.SampleTaken -= OnSample;

        _refreshTimer.Interval = 5000;
        _refreshTimer.Tick += (_, _) => RefreshCharts();
        _refreshTimer.Start();

        Shown += (_, _) =>
        {
            UpdateCards(_engine.LastStatus, _engine.LastAssessment);
            if (_engine.LastStatus is { } ls && _engine.LastAssessment is { } la) _synoptic.UpdateStatus(ls, la);
            RefreshCharts();
            RefreshEvents();
        };
    }

    private void BuildLayout()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var pageSynoptic = new TabPage(L10n.T("tab_synoptic")) { BackColor = Theme.Background };
        var pageDash = new TabPage(L10n.T("tab_dashboard")) { BackColor = Theme.Background };
        var pageEvents = new TabPage(L10n.T("tab_events")) { BackColor = Theme.Background };
        var pageSettings = new TabPage(L10n.T("tab_settings")) { BackColor = Theme.Background };
        tabs.TabPages.AddRange(new[] { pageSynoptic, pageDash, pageEvents, pageSettings });
        tabs.SelectedTab = pageSynoptic;
        Controls.Add(tabs);

        // ---- synoptic page ----
        _synoptic.Dock = DockStyle.Fill;
        pageSynoptic.Controls.Add(_synoptic);

        // ---- dashboard page ----
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Theme.Background, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));   // toolbar
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));  // cards
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // charts
        pageDash.Controls.Add(root);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _statusLabel.AutoSize = true;
        _statusLabel.Padding = new Padding(4, 8, 20, 0);
        _statusLabel.Font = Theme.TitleFont;
        toolbar.Controls.Add(_statusLabel);

        var rangeLabel = new Label { Text = L10n.T("period"), AutoSize = true, Padding = new Padding(0, 10, 4, 0), ForeColor = Theme.TextMuted };
        _rangeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _rangeBox.Items.AddRange(Ranges.Select(r => (object)r.Label).ToArray());
        _rangeBox.SelectedIndex = 3; // 24h
        _rangeBox.SelectedIndexChanged += (_, _) => RefreshCharts();
        _rangeBox.Width = 140;
        toolbar.Controls.Add(rangeLabel);
        toolbar.Controls.Add(_rangeBox);
        root.Controls.Add(toolbar, 0, 0);

        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 8, RowCount = 1 };
        foreach (var c in new[] { _cardSource, _cardCharge, _cardRuntime, _cardLoad, _cardInput, _cardOutput, _cardBattV, _cardPower })
        {
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12.5f));
            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(4);
            cards.Controls.Add(c);
        }
        root.Controls.Add(cards, 0, 1);

        var charts = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        charts.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        charts.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        foreach (var ch in new[] { _chartVoltage, _chartLoad, _chartBattery, _chartRuntime })
        {
            ch.Dock = DockStyle.Fill;
            ch.Margin = new Padding(4);
        }
        charts.Controls.Add(_chartVoltage, 0, 0);
        charts.Controls.Add(_chartLoad, 1, 0);
        charts.Controls.Add(_chartBattery, 0, 1);
        charts.Controls.Add(_chartRuntime, 1, 1);
        root.Controls.Add(charts, 0, 2);

        // ---- events page ----
        _eventsList.Dock = DockStyle.Fill;
        _eventsList.View = View.Details;
        _eventsList.FullRowSelect = true;
        _eventsList.BackColor = Theme.Panel;
        _eventsList.ForeColor = Theme.TextPrimary;
        _eventsList.BorderStyle = BorderStyle.None;
        _eventsList.Columns.Add(L10n.T("ev_when"), 150);
        _eventsList.Columns.Add(L10n.T("ev_kind"), 160);
        _eventsList.Columns.Add(L10n.T("ev_message"), 760);
        pageEvents.Controls.Add(_eventsList);
        pageEvents.Enter += (_, _) => RefreshEvents();

        // ---- settings page ----
        pageSettings.Controls.Add(new SettingsPanel(_cfg, _engine) { Dock = DockStyle.Fill });
    }

    private void OnSample(UpsStatus status, Assessment assessment)
    {
        if (IsDisposed) return;
        try { BeginInvoke(() => { UpdateCards(status, assessment); _synoptic.UpdateStatus(status, assessment); }); } catch { }
    }

    private void UpdateCards(UpsStatus? s, Assessment? a)
    {
        if (s is null)
        {
            _statusLabel.Text = _engine.LastError is { } err ? $"⚠ {err}" : L10n.T("status_waiting");
            _statusLabel.ForeColor = Theme.Warn;
            return;
        }

        var sevColor = Theme.SeverityColor(a?.Level ?? Severity.Normal);
        var serviceTag = _engine.ActionsDelegated ? "  " + L10n.T("status_service") : "";
        _statusLabel.Text = (a is { Level: not Severity.Normal }
            ? $"● {s.PowerSourceLabel} — {string.Join("; ", a.Reasons)}"
            : $"● {s.PowerSourceLabel} — {L10n.T("status_normal")}") + serviceTag;
        _statusLabel.ForeColor = sevColor;

        _cardSource.SetValue(s.PowerSourceLabel,
            s.Model ?? "", null, s.OnBattery ? Theme.Crit : Theme.Ok);

        var charge = s.ChargePercent;
        // While recharging show the estimated time to full under the charge value.
        var chargeSub = s.RechargeEtaMinutes is { } eta ? L10n.F("recharge_eta", eta) : L10n.T("sub_charge");
        _cardCharge.SetValue(charge.HasValue ? $"{charge:0.0}%" : "—", chargeSub,
            charge / 100.0,
            charge <= _cfg.CriticalChargePercent ? Theme.Crit : charge <= _cfg.WarnChargePercent ? Theme.Warn : Theme.Ok);

        var rtMin = s.RuntimeMinutes;
        _cardRuntime.SetValue(rtMin.HasValue ? $"{rtMin:0.#} min" : "—", L10n.T("sub_runtime"), null,
            s.RuntimeSeconds <= _cfg.CriticalRuntimeSeconds ? Theme.Crit : s.RuntimeSeconds <= _cfg.WarnRuntimeSeconds ? Theme.Warn : Theme.Ok);

        _cardLoad.SetValue(s.LoadPercent.HasValue ? $"{s.LoadPercent:0}%" : "—",
            L10n.F("sub_of_va", $"{s.NominalApparentPower:0}"), s.LoadPercent / 100.0,
            s.LoadPercent >= _cfg.OverloadPercent ? Theme.Crit : Theme.InfoBlue);

        _cardInput.SetValue(s.InputVoltage.HasValue ? $"{s.InputVoltage:0.#} V" : "—",
            s.InputFrequency.HasValue ? $"{s.InputFrequency:0.#} Hz" : "", null, Theme.InfoBlue);
        _cardOutput.SetValue(s.OutputVoltage.HasValue ? $"{s.OutputVoltage:0.#} V" : "—",
            s.OutputFrequency.HasValue ? $"{s.OutputFrequency:0.#} Hz" : "", null, Theme.Ok);
        _cardBattV.SetValue(s.BatteryVoltage.HasValue ? $"{s.BatteryVoltage:0.#} V" : "—", L10n.T("sub_battv"), null, Theme.SeriesBattV);
        _cardPower.SetValue(s.OutputActivePowerW.HasValue ? $"{s.OutputActivePowerW:0} W" : "—",
            s.OutputCurrent.HasValue ? $"{s.OutputCurrent:0.#} A" : "", null, Theme.SeriesWatts);
    }

    private void RefreshCharts()
    {
        var span = Ranges[Math.Max(0, _rangeBox.SelectedIndex)].Span;
        var to = DateTime.Now;
        var from = to - span;

        List<Sample> samples;
        try { samples = _db.GetSamples(from, to); }
        catch { return; }

        var bands = new List<(DateTime, DateTime)>();
        DateTime? bandStart = null;
        foreach (var smp in samples)
        {
            if (smp.OnBattery && bandStart is null) bandStart = smp.Timestamp;
            if (!smp.OnBattery && bandStart is { } bs) { bands.Add((bs, smp.Timestamp)); bandStart = null; }
        }
        if (bandStart is { } open) bands.Add((open, to));

        List<(DateTime, double)> Pick(Func<Sample, double?> f) =>
            samples.Where(x => f(x).HasValue).Select(x => (x.Timestamp, f(x)!.Value)).ToList();

        _chartVoltage.SetData(L10n.T("chart_voltage"), new List<ChartSeries>
        {
            new(L10n.T("series_input"), Theme.SeriesInputV, Pick(x => x.InputVoltage), "V"),
            new(L10n.T("series_output"), Theme.SeriesOutputV, Pick(x => x.OutputVoltage), "V"),
        }, bands);

        _chartLoad.SetData(L10n.T("chart_load"), new List<ChartSeries>
        {
            new(L10n.T("series_load"), Theme.SeriesLoad, Pick(x => x.LoadPercent), "%"),
            new(L10n.T("series_watts"), Theme.SeriesWatts, Pick(x => x.Watts), "W"),
        }, bands, fixedMin: 0);

        _chartBattery.SetData(L10n.T("chart_battery"), new List<ChartSeries>
        {
            new(L10n.T("series_charge"), Theme.SeriesCharge, Pick(x => x.ChargePercent), "%"),
            new(L10n.T("series_battv"), Theme.SeriesBattV, Pick(x => x.BatteryVoltage * 10), "V/10"),
        }, bands, fixedMin: 0, fixedMax: 140);

        _chartRuntime.SetData(L10n.T("chart_runtime"), new List<ChartSeries>
        {
            new(L10n.T("series_runtime"), Theme.SeriesRuntime, Pick(x => x.RuntimeSeconds / 60.0), "min"),
        }, bands, fixedMin: 0);
    }

    private void RefreshEvents()
    {
        try
        {
            var events = _db.GetEvents(DateTime.Now.AddDays(-30), DateTime.Now);
            _eventsList.BeginUpdate();
            _eventsList.Items.Clear();
            foreach (var ev in events)
            {
                var item = new ListViewItem(ev.Timestamp.ToString("dd/MM/yyyy HH:mm:ss"));
                item.SubItems.Add(ev.Kind);
                item.SubItems.Add(ev.Message);
                item.ForeColor = ev.Kind switch
                {
                    "HIBERNATE" or "HIBERNATE_SIMULATED" => Theme.Crit,
                    "MAINS_LOST" => Theme.Warn,
                    "MAINS_BACK" => Theme.Ok,
                    _ => Theme.TextPrimary,
                };
                _eventsList.Items.Add(item);
            }
            _eventsList.EndUpdate();
        }
        catch { }
    }
}
