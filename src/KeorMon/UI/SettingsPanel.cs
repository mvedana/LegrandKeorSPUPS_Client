using KeorMon.Monitor;
using KeorMon.Ups;

namespace KeorMon.UI;

/// <summary>
/// Settings tab: monitor thresholds (config.json) on the left, writable UPS
/// parameters (written to the device over HID) on the right.
/// </summary>
public sealed class SettingsPanel : Panel
{
    private readonly AppConfig _cfg;
    private readonly MonitorEngine _engine;

    private readonly NumericUpDown _poll = new() { Minimum = 2, Maximum = 3600 };
    private readonly NumericUpDown _warnCharge = new() { Minimum = 1, Maximum = 99 };
    private readonly NumericUpDown _warnRuntime = new() { Minimum = 60, Maximum = 86400, Increment = 60 };
    private readonly NumericUpDown _critCharge = new() { Minimum = 1, Maximum = 99 };
    private readonly NumericUpDown _critRuntime = new() { Minimum = 30, Maximum = 86400, Increment = 30 };
    private readonly NumericUpDown _grace = new() { Minimum = 0, Maximum = 600 };
    private readonly ComboBox _actionBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private static readonly string[] ActionCodes = { "simulate", "hibernate", "shutdown" };
    private readonly ComboBox _langBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly Button _saveBtn = new() { Text = L10n.T("set_save") };
    private readonly Button _testBtn = new() { Text = L10n.T("set_test") };
    private readonly Label _saveInfo = new() { AutoSize = true, ForeColor = Theme.Ok };

    private readonly ComboBox _paramBox = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 380,
        DropDownWidth = 460,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
    };
    private readonly NumericUpDown _paramValue = new() { Minimum = -1, Maximum = 65535, Width = 100 };
    private readonly Label _paramInfo = new() { AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = Theme.TextMuted };
    private readonly Button _paramRead = new() { Text = "Leggi" };
    private readonly Button _paramWrite = new() { Text = "Scrivi sul UPS" };
    private readonly Label _paramResult = new() { AutoSize = true, MaximumSize = new Size(420, 0) };
    private readonly Label _paramDesc = new()
    {
        AutoSize = true,
        MaximumSize = new Size(440, 0),
        ForeColor = Theme.TextMuted,
        Padding = new Padding(0, 2, 0, 6),
    };

    public SettingsPanel(AppConfig cfg, MonitorEngine engine)
    {
        _cfg = cfg;
        _engine = engine;
        BackColor = Theme.Background;
        ForeColor = Theme.TextPrimary;
        AutoScroll = true;

        var cols = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
        cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        Controls.Add(cols);

        cols.Controls.Add(BuildMonitorGroup(), 0, 0);
        cols.Controls.Add(BuildUpsGroup(), 1, 0);
    }

    private GroupBox BuildMonitorGroup()
    {
        var group = MakeGroup(L10n.T("set_group_monitor"));
        var grid = MakeGrid(group);

        if (_engine.ActionsDelegated)
        {
            var note = new Label
            {
                Text = L10n.T("set_service_note"),
                AutoSize = true,
                ForeColor = Theme.InfoBlue,
                Padding = new Padding(0, 0, 0, 8),
            };
            grid.Controls.Add(note);
            grid.SetColumnSpan(note, 2);
        }

        AddRow(grid, L10n.T("set_poll"), _poll, _cfg.PollSeconds, "help_poll");
        AddRow(grid, L10n.T("set_warn_charge"), _warnCharge, _cfg.WarnChargePercent, "help_warn_charge");
        AddRow(grid, L10n.T("set_warn_rt"), _warnRuntime, _cfg.WarnRuntimeSeconds, "help_warn_rt");
        AddRow(grid, L10n.T("set_crit_charge"), _critCharge, _cfg.CriticalChargePercent, "help_crit_charge");
        AddRow(grid, L10n.T("set_crit_rt"), _critRuntime, _cfg.CriticalRuntimeSeconds, "help_crit_rt");
        AddRow(grid, L10n.T("set_grace"), _grace, _cfg.CriticalGraceSeconds, "help_grace");

        // critical action: simulate / hibernate (default) / shutdown
        grid.Controls.Add(new Label { Text = L10n.T("set_action"), AutoSize = true, ForeColor = Theme.Warn, Padding = new Padding(0, 6, 8, 0) });
        _actionBox.Items.AddRange(new object[] { L10n.T("act_simulate"), L10n.T("act_hibernate"), L10n.T("act_shutdown") });
        int actIdx = Array.IndexOf(ActionCodes, _cfg.CriticalAction);
        _actionBox.SelectedIndex = actIdx >= 0 ? actIdx : 1;
        grid.Controls.Add(_actionBox);
        AddHelp(grid, "help_action");

        // language selector: Auto + the 20 supported languages by native name
        grid.Controls.Add(new Label { Text = L10n.T("lang_label"), AutoSize = true, ForeColor = Theme.TextMuted, Padding = new Padding(0, 6, 8, 0) });
        _langBox.Items.Add(L10n.T("lang_auto"));
        foreach (var name in L10n.NativeNames) _langBox.Items.Add(name);
        int langIdx = Array.IndexOf(L10n.Codes, _cfg.Language);
        _langBox.SelectedIndex = langIdx >= 0 ? langIdx + 1 : 0;
        grid.Controls.Add(_langBox);
        AddHelp(grid, "help_lang");

        _saveBtn.AutoSize = true;
        StyleButton(_saveBtn);
        _saveBtn.Click += (_, _) => SaveConfig();
        grid.Controls.Add(_saveBtn);

        _testBtn.AutoSize = true;
        StyleButton(_testBtn);
        _testBtn.Click += (_, _) => RunSimulation();
        grid.Controls.Add(_testBtn);

        grid.Controls.Add(_saveInfo);
        grid.SetColumnSpan(_saveInfo, 2);
        return group;
    }

    private static string ParamLabel(UpsReportDef def) => L10n.T("param_" + def.Name);

    private GroupBox BuildUpsGroup()
    {
        var group = MakeGroup(L10n.T("set_group_ups"));
        var grid = MakeGrid(group);

        var writable = UpsReportMap.Reports.Where(r => r.Writable).ToList();
        _paramBox.Items.AddRange(writable.Select(w => (object)$"{ParamLabel(w)} [{w.Name}]").ToArray());
        _paramBox.SelectedIndexChanged += (_, _) => OnParamSelected(writable);
        grid.Controls.Add(new Label { Text = L10n.T("set_param"), AutoSize = true, ForeColor = Theme.TextMuted, Padding = new Padding(0, 6, 0, 0) });
        grid.Controls.Add(_paramBox);

        grid.Controls.Add(new Label { Text = L10n.T("set_value"), AutoSize = true, ForeColor = Theme.TextMuted, Padding = new Padding(0, 6, 0, 0) });
        grid.Controls.Add(_paramValue);

        grid.Controls.Add(_paramInfo);
        grid.SetColumnSpan(_paramInfo, 2);

        grid.Controls.Add(_paramDesc);
        grid.SetColumnSpan(_paramDesc, 2);

        _paramRead.Text = L10n.T("set_read");
        _paramWrite.Text = L10n.T("set_write");
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        StyleButton(_paramRead); StyleButton(_paramWrite);
        _paramRead.AutoSize = true; _paramWrite.AutoSize = true;
        _paramRead.Click += (_, _) => ReadParam(writable);
        _paramWrite.Click += (_, _) => WriteParam(writable);
        buttons.Controls.Add(_paramRead);
        buttons.Controls.Add(_paramWrite);
        grid.Controls.Add(buttons);
        grid.SetColumnSpan(buttons, 2);

        grid.Controls.Add(_paramResult);
        grid.SetColumnSpan(_paramResult, 2);

        if (_paramBox.Items.Count > 0) _paramBox.SelectedIndex = 0;
        return group;
    }

    private static GroupBox MakeGroup(string title) => new()
    {
        Text = title,
        Dock = DockStyle.Fill,
        ForeColor = Theme.TextPrimary,
        Padding = new Padding(10),
        Margin = new Padding(8),
    };

    private static TableLayoutPanel MakeGrid(GroupBox group)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        group.Controls.Add(grid);
        return grid;
    }

    private static void AddRow(TableLayoutPanel grid, string label, NumericUpDown control, decimal value, string? helpKey = null)
    {
        grid.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Theme.TextMuted, Padding = new Padding(0, 6, 8, 0) });
        control.Value = Math.Clamp(value, control.Minimum, control.Maximum);
        control.Width = 100;
        grid.Controls.Add(control);
        if (helpKey != null) AddHelp(grid, helpKey);
    }

    /// <summary>Explanatory line under a settings field, spanning both grid columns.</summary>
    private static void AddHelp(TableLayoutPanel grid, string key)
    {
        var help = new Label
        {
            Text = L10n.T(key),
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            ForeColor = Theme.TextMuted,
            Font = new Font(Control.DefaultFont.FontFamily, Control.DefaultFont.Size - 0.5f),
            Padding = new Padding(0, 0, 0, 8),
        };
        grid.Controls.Add(help);
        grid.SetColumnSpan(help, 2);
    }

    private static void StyleButton(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = Theme.Panel;
        b.ForeColor = Theme.TextPrimary;
        b.FlatAppearance.BorderColor = Theme.PanelBorder;
        b.Padding = new Padding(6, 3, 6, 3);
        b.Margin = new Padding(0, 8, 8, 0);
    }

    private void SaveConfig()
    {
        var action = ActionCodes[Math.Max(0, _actionBox.SelectedIndex)];
        bool enablingReal = action != "simulate" && _cfg.CriticalAction == "simulate";
        if (enablingReal)
        {
            var answer = MessageBox.Show(
                L10n.T("set_confirm_real_body"),
                L10n.T("set_confirm_title"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
            {
                _actionBox.SelectedIndex = Array.IndexOf(ActionCodes, _cfg.CriticalAction);
                return;
            }
        }

        _cfg.PollSeconds = (int)_poll.Value;
        _cfg.WarnChargePercent = (int)_warnCharge.Value;
        _cfg.WarnRuntimeSeconds = (int)_warnRuntime.Value;
        _cfg.CriticalChargePercent = (int)_critCharge.Value;
        _cfg.CriticalRuntimeSeconds = (int)_critRuntime.Value;
        _cfg.CriticalGraceSeconds = (int)_grace.Value;
        _cfg.CriticalAction = action;
        _cfg.Language = _langBox.SelectedIndex <= 0 ? "auto" : L10n.Codes[_langBox.SelectedIndex - 1];
        _cfg.Save();
        _saveInfo.Text = L10n.F("set_saved2", DateTime.Now.ToString("HH:mm:ss"),
            (string)_actionBox.SelectedItem!);
    }

    private void RunSimulation()
    {
        _engine.SimulateOnBattery = !_engine.SimulateOnBattery;
        if (_engine.SimulateOnBattery)
        {
            _engine.SimulateChargePercent = Math.Max(1, _cfg.CriticalChargePercent - 5);
            _engine.SimulateRuntimeSeconds = Math.Max(30, _cfg.CriticalRuntimeSeconds - 60);
            _testBtn.Text = L10n.T("set_test_stop");
            _saveInfo.Text = L10n.T("set_sim_on");
        }
        else
        {
            _engine.SimulateChargePercent = null;
            _engine.SimulateRuntimeSeconds = null;
            _testBtn.Text = L10n.T("set_test");
            _saveInfo.Text = L10n.T("set_sim_off");
        }
    }

    private void OnParamSelected(List<UpsReportDef> writable)
    {
        if (_paramBox.SelectedIndex < 0) return;
        var def = writable[_paramBox.SelectedIndex];
        _paramValue.Minimum = def.Min;
        _paramValue.Maximum = def.Max;
        _paramInfo.Text = L10n.F("set_param_info", def.Id, def.Min, def.Max, def.Unit) +
                          (def.Dangerous ? L10n.T("set_param_danger") : "");
        _paramInfo.ForeColor = def.Dangerous ? Theme.Crit : Theme.TextMuted;
        _paramDesc.Text = L10n.T("desc_" + def.Name);
        _paramResult.Text = "";
        ReadParam(writable);
    }

    private void ReadParam(List<UpsReportDef> writable)
    {
        if (_paramBox.SelectedIndex < 0) return;
        var def = writable[_paramBox.SelectedIndex];
        try
        {
            var dev = UpsDevice.Find() ?? throw new IOException(L10n.T("set_ups_notfound"));
            var v = dev.ReadValue(def.Name);
            if (v.HasValue)
            {
                _paramValue.Value = Math.Clamp((decimal)v.Value, _paramValue.Minimum, _paramValue.Maximum);
                SetResult(L10n.F("set_cur_value", v, def.Unit), Theme.Ok);
            }
            else SetResult(L10n.T("set_not_set"), Theme.TextMuted);
        }
        catch (Exception ex) { SetResult(L10n.F("set_read_err", ex.Message), Theme.Crit); }
    }

    private void WriteParam(List<UpsReportDef> writable)
    {
        if (_paramBox.SelectedIndex < 0) return;
        var def = writable[_paramBox.SelectedIndex];
        int value = (int)_paramValue.Value;

        var warning = def.Dangerous ? L10n.F("set_write_danger", ParamLabel(def)) : "";
        var answer = MessageBox.Show(
            warning + L10n.F("set_write_confirm", value, def.Unit, ParamLabel(def), def.Id),
            L10n.T("set_write_confirm_title"),
            MessageBoxButtons.YesNo,
            def.Dangerous ? MessageBoxIcon.Warning : MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        try
        {
            var dev = UpsDevice.Find() ?? throw new IOException(L10n.T("set_ups_notfound"));
            var (accepted, after) = dev.WriteValue(def.Name, value, allowDangerous: def.Dangerous);
            SetResult(accepted
                ? L10n.F("set_write_ok", after, def.Unit)
                : L10n.F("set_write_rejected", after?.ToString() ?? "n/d"),
                accepted ? Theme.Ok : Theme.Warn);
        }
        catch (Exception ex) { SetResult(L10n.F("set_write_err", ex.Message), Theme.Crit); }
    }

    private void SetResult(string text, Color color)
    {
        _paramResult.Text = text;
        _paramResult.ForeColor = color;
    }
}
