using KeorMon.Data;
using KeorMon.Native;
using KeorMon.Ups;

namespace KeorMon.Monitor;

/// <summary>
/// The monitoring loop: samples the UPS, stores history, classifies severity,
/// raises alert events and triggers (or simulates) hibernation.
/// Runs on a background thread; UI subscribes to its events.
/// </summary>
public sealed class MonitorEngine : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly UpsDatabase _db;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    private Severity _prevLevel = Severity.Normal;
    private bool _prevOnBattery;
    private DateTime? _criticalSince;
    private readonly Dictionary<Severity, DateTime> _lastNotify = new();
    private bool _hibernateDone;
    private DateTime _lastPrune = DateTime.MinValue;

    // Test hooks: force the on-battery path without touching the mains.
    public bool SimulateOnBattery { get; set; }
    public double? SimulateChargePercent { get; set; }
    public double? SimulateRuntimeSeconds { get; set; }

    /// <summary>
    /// True when the KeorMon Windows service is running and this engine only feeds
    /// the UI: no database writes, no hibernation (the service owns both).
    /// </summary>
    public bool ActionsDelegated { get; set; }

    private DateTime _cfgStamp;

    // Freeze watchdog: the UPS's measurement MCU can stop refreshing data toward the
    // USB bridge (every sample identical bit-for-bit). Detected by comparing the
    // naturally-noisy fields across consecutive ticks; recovered with UpsDevice.WakeUp().
    private (double? InV, double? W, double? BattV, double? Charge) _lastSignature;
    private int _frozenTicks;
    private DateTime _lastWakeUp = DateTime.MinValue;
    private const int FrozenTicksThreshold = 30;   // ~5 min at the default 10 s poll

    public UpsStatus? LastStatus { get; private set; }
    public Assessment? LastAssessment { get; private set; }
    public string? LastError { get; private set; }

    public event Action<UpsStatus, Assessment>? SampleTaken;
    public event Action<string, string, Severity>? Alert;      // title, message, severity
    public event Action<bool>? HibernationTriggered;           // simulated?
    public event Action<string>? Error;

    public MonitorEngine(AppConfig cfg, UpsDatabase db)
    {
        _cfg = cfg;
        _db = db;
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "UpsMonitor" };
        _thread.Start();
    }

    private void Loop()
    {
        Log($"Monitor avviato ({(ActionsDelegated ? "delegato al servizio" : "attivo")}). " +
            $"Azione critica: {_cfg.CriticalAction}. Poll {_cfg.PollSeconds}s.");
        _cfgStamp = GetConfigStamp();
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                ReloadConfigIfChanged();
                Tick();
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log($"Errore: {ex.Message}");
                Error?.Invoke(ex.Message);
            }
            _cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Max(2, _cfg.PollSeconds)));
        }
    }

    /// <summary>One sampling step. Public so tests / --once mode can call it directly.</summary>
    public void Tick()
    {
        var dev = UpsDevice.Find() ?? throw new IOException(KeorMon.UI.L10n.T("err_notfound"));
        var status = dev.ReadStatus();

        if (SimulateOnBattery)
        {
            status.OnBattery = true;
            if (SimulateChargePercent.HasValue) status.ChargePercent = SimulateChargePercent;
            if (SimulateRuntimeSeconds.HasValue) status.RuntimeSeconds = SimulateRuntimeSeconds;
        }

        CheckFrozenData(dev, status);
        status.RechargeEtaMinutes = EstimateRechargeEta(status);

        var assessment = Assess(status);
        if (!ActionsDelegated) _db.InsertSample(status, assessment.Level);
        LastStatus = status;
        LastAssessment = assessment;
        SampleTaken?.Invoke(status, assessment);

        HandleTransitions(status, assessment);
        HandleCritical(status, assessment);

        _prevLevel = assessment.Level;
        _prevOnBattery = status.OnBattery;

        if ((DateTime.Now - _lastPrune).TotalHours >= 24)
        {
            _db.Prune(TimeSpan.FromDays(_cfg.RetentionDays));
            _lastPrune = DateTime.Now;
        }
    }

    public Assessment Assess(UpsStatus s)
    {
        var reasons = new List<string>();
        var level = Severity.Normal;

        if (s.OnBattery)
        {
            level = Severity.Info;
            reasons.Add(KeorMon.UI.L10n.T("rs_no_mains"));

            if (s.ChargePercent is { } c)
            {
                if (c <= _cfg.CriticalChargePercent) { level = Severity.Critical; reasons.Add(KeorMon.UI.L10n.F("rs_charge_crit", c, _cfg.CriticalChargePercent)); }
                else if (c <= _cfg.WarnChargePercent) { level = Max(level, Severity.Warning); reasons.Add(KeorMon.UI.L10n.F("rs_charge", c, _cfg.WarnChargePercent)); }
            }
            if (s.RuntimeSeconds is { } r)
            {
                if (r <= _cfg.CriticalRuntimeSeconds) { level = Severity.Critical; reasons.Add(KeorMon.UI.L10n.F("rs_rt_crit", Math.Round(r / 60, 1), $"{_cfg.CriticalRuntimeSeconds / 60.0:0.#}")); }
                else if (r <= _cfg.WarnRuntimeSeconds) { level = Max(level, Severity.Warning); reasons.Add(KeorMon.UI.L10n.F("rs_rt", Math.Round(r / 60, 1), $"{_cfg.WarnRuntimeSeconds / 60.0:0.#}")); }
            }
        }

        if (s.LoadPercent is { } load && load >= _cfg.OverloadPercent)
        {
            level = Max(level, Severity.Warning);
            reasons.Add(KeorMon.UI.L10n.F("rs_overload", load, _cfg.OverloadPercent));
        }

        return new Assessment(level, reasons);

        static Severity Max(Severity a, Severity b) => (Severity)Math.Max((int)a, (int)b);
    }

    private void HandleTransitions(UpsStatus status, Assessment sev)
    {
        if (status.OnBattery && !_prevOnBattery && _cfg.NotifyOnMainsLost)
            RaiseAlert(KeorMon.UI.L10n.T("al_mains_lost"), KeorMon.UI.L10n.F("al_on_battery", status), Severity.Warning, "MAINS_LOST");

        if (!status.OnBattery && _prevOnBattery)
        {
            if (_cfg.NotifyOnMainsBack)
                RaiseAlert(KeorMon.UI.L10n.T("al_mains_back"), status.ToString(), Severity.Info, "MAINS_BACK");
            _criticalSince = null;
            _hibernateDone = false;
        }

        if (sev.Level != Severity.Normal)
        {
            bool due = !_lastNotify.TryGetValue(sev.Level, out var last) ||
                       (DateTime.Now - last).TotalSeconds >= _cfg.ReNotifySeconds;
            if (sev.Level != _prevLevel || due)
            {
                RaiseAlert(KeorMon.UI.L10n.F("al_ups_prefix", sev.Level), $"{string.Join("; ", sev.Reasons)}. {status}", sev.Level, "ALERT");
                _lastNotify[sev.Level] = DateTime.Now;
            }
        }
        else
        {
            _lastNotify.Clear();
        }
    }

    private void HandleCritical(UpsStatus status, Assessment sev)
    {
        // The service owns the grace timer and the hibernation action.
        if (ActionsDelegated) return;

        if (sev.Level == Severity.Critical && !_hibernateDone)
        {
            _criticalSince ??= DateTime.Now;
            var elapsed = (DateTime.Now - _criticalSince.Value).TotalSeconds;
            if (elapsed >= _cfg.CriticalGraceSeconds)
            {
                var action = _cfg.CriticalAction;
                bool simulated = action == "simulate";
                var titleKey = simulated ? "al_hib_sim" : action == "shutdown" ? "al_shut" : "al_hib";
                var kind = simulated ? "HIBERNATE_SIMULATED" : action == "shutdown" ? "SHUTDOWN" : "HIBERNATE";
                RaiseAlert(KeorMon.UI.L10n.T(titleKey),
                    KeorMon.UI.L10n.F("al_hib_body", string.Join("; ", sev.Reasons)), Severity.Critical, kind);
                HibernationTriggered?.Invoke(simulated);
                _hibernateDone = true;
                if (!simulated)
                {
                    if (action == "shutdown") Shutdown();
                    else Hibernate();
                }
            }
        }
        else if (sev.Level != Severity.Critical)
        {
            _criticalSince = null;
        }
    }

    private void Hibernate()
    {
        Log("Ibernazione in corso (SetSuspendState).");
        HidNative.EnableShutdownPrivilege();
        if (!HidNative.SetSuspendState(true, true, false))
        {
            Log("SetSuspendState fallita; fallback shutdown.exe /h");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/h") { UseShellExecute = false });
        }
    }

    private void Shutdown()
    {
        Log("Spegnimento in corso (shutdown.exe /s /f /t 10).");
        HidNative.EnableShutdownPrivilege();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "shutdown.exe", "/s /f /t 10 /c \"UPS: batteria critica\"") { UseShellExecute = false });
    }

    private void RaiseAlert(string title, string message, Severity level, string kind)
    {
        Log($"[{level}] {title} - {message}");
        if (!ActionsDelegated) _db.InsertEvent(kind, $"{title}: {message}");
        Alert?.Invoke(title, message, level);
    }

    /// <summary>
    /// Detects the frozen-measurements failure mode (identical values across many
    /// consecutive polls on fields that always jitter) and fires the wake-up write.
    /// </summary>
    private void CheckFrozenData(UpsDevice dev, UpsStatus s)
    {
        var signature = (s.InputVoltage, s.OutputActivePowerW, s.BatteryVoltage, s.ChargePercent);
        if (signature == _lastSignature && s.InputVoltage is not null)
        {
            _frozenTicks++;
            if (_frozenTicks >= FrozenTicksThreshold &&
                (DateTime.Now - _lastWakeUp).TotalMinutes >= 10)
            {
                _lastWakeUp = DateTime.Now;
                bool ok = dev.WakeUp();
                _frozenTicks = 0;
                Log($"Dati UPS congelati da ~{FrozenTicksThreshold} letture: wake-up {(ok ? "inviato" : "FALLITO")}.");
                if (!ActionsDelegated) _db.InsertEvent("FREEZE_RECOVERY", $"Frozen measurements detected; wake-up write {(ok ? "sent" : "failed")}.");
            }
        }
        else
        {
            _frozenTicks = 0;
            _lastSignature = signature;
        }
    }

    /// <summary>
    /// Minutes to full charge, estimated from the charge slope of the last hour of
    /// on-mains samples in the shared database (so it survives process restarts and
    /// smooths out the firmware's coarse, step-wise charge gauge — the device exposes
    /// no AverageTimeToFull). Only samples after the most recent on-battery interval
    /// are used. Returns null when not charging, already full, or the trend is flat.
    /// </summary>
    private double? EstimateRechargeEta(UpsStatus s)
    {
        if (s.OnBattery || s.ChargePercent is not { } charge || charge >= 100) return null;

        List<Data.Sample> window;
        try { window = _db.GetSamples(DateTime.Now.AddMinutes(-60), DateTime.Now); }
        catch { return null; }

        // Keep only the charging stretch after the last on-battery sample.
        int lastOnBattery = window.FindLastIndex(x => x.OnBattery);
        var pts = window.Skip(lastOnBattery + 1)
                        .Where(x => x.ChargePercent.HasValue)
                        .Select(x => (T: x.Timestamp, C: x.ChargePercent!.Value))
                        .ToList();
        if (pts.Count < 5 || (pts[^1].T - pts[0].T).TotalMinutes < 5) return null;

        // Least-squares slope in %/minute.
        var t0 = pts[0].T;
        double n = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (t, c) in pts)
        {
            double x = (t - t0).TotalMinutes;
            n++; sx += x; sy += c; sxx += x * x; sxy += x * c;
        }
        double denom = n * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-9) return null;
        double slope = (n * sxy - sx * sy) / denom;
        if (slope < 0.02) return null;   // flat or noisy: no usable estimate

        return Math.Round((100 - charge) / slope);
    }

    private static DateTime GetConfigStamp()
    {
        try { return File.GetLastWriteTimeUtc(AppConfig.ConfigPath); }
        catch { return DateTime.MinValue; }
    }

    /// <summary>Hot-reload: pick up settings saved from the tray app (or by hand).</summary>
    private void ReloadConfigIfChanged()
    {
        var stamp = GetConfigStamp();
        if (stamp == _cfgStamp) return;
        _cfgStamp = stamp;
        try
        {
            _cfg.CopyFrom(AppConfig.Load());
            Log($"Configurazione ricaricata. Azione critica: {_cfg.CriticalAction}.");
        }
        catch (Exception ex) { Log($"Reload config fallito: {ex.Message}"); }
    }

    private static void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        Console.WriteLine(line);
        try
        {
            Directory.CreateDirectory(AppConfig.BaseDir);
            File.AppendAllText(AppConfig.LogPath, line + Environment.NewLine);
        }
        catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(3000);
        _cts.Dispose();
    }
}
