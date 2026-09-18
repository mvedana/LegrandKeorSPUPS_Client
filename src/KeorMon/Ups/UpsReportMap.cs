namespace KeorMon.Ups;

/// <summary>One single-value feature report of the Keor SP's HID Power Device collection.</summary>
public sealed record UpsReportDef(
    byte Id,
    string Name,
    string Label,          // human label used by the UI (Italian)
    int Bytes,             // payload width after the report ID
    double Scale,          // raw * Scale = value in Unit
    string Unit,
    bool Writable = false,
    bool Dangerous = false, // writing arms an actual output shutdown
    int Min = 0,
    int Max = 0);

/// <summary>
/// Report map verified on a Legrand Keor SP 800VA (VID 0x0665 / PID 0x5161, fw 0002).
/// The firmware's HID report descriptor carries unreliable unit/exponent metadata, so
/// values are decoded from raw feature reports using this explicit table instead.
/// </summary>
public static class UpsReportMap
{
    public static readonly IReadOnlyList<UpsReportDef> Reports = new List<UpsReportDef>
    {
        new(0x01, "NominalInputVoltage",  "Tensione nominale ingresso", 1, 1,   "V"),
        new(0x02, "NominalFrequency",     "Frequenza nominale",         1, 1,   "Hz"),
        new(0x03, "NominalApparentPower", "Potenza nominale",           2, 1,   "VA"),
        new(0x04, "NominalBatteryVoltage","Tensione nominale batteria", 1, 1,   "V"),
        new(0x06, "LowVoltageTransfer",   "Soglia trasferimento bassa", 1, 1,   "V",  Writable: true, Min: 160, Max: 200),
        new(0x09, "HighVoltageTransfer",  "Soglia trasferimento alta",  2, 1,   "V",  Writable: true, Min: 250, Max: 300),
        new(0x10, "Test",                 "Comando test batteria",      1, 1,   "code", Writable: true, Min: 0, Max: 6),
        new(0x11, "AudibleAlarmControl",  "Allarme acustico (2=on 3=off)", 1, 1,"code", Writable: true, Min: 1, Max: 3),
        new(0x15, "DelayBeforeShutdown",  "Ritardo spegnimento uscita", 2, 1,   "s",  Writable: true, Dangerous: true, Min: 0, Max: 65535),
        new(0x17, "DelayBeforeReboot",    "Ritardo riavvio uscita",     2, 1,   "s",  Writable: true, Dangerous: true, Min: 0, Max: 65535),
        new(0x18, "InputVoltage",         "Tensione ingresso",          2, 0.1, "V"),
        new(0x19, "InputFrequency",       "Frequenza ingresso",         2, 0.1, "Hz"),
        new(0x1B, "OutputVoltage",        "Tensione uscita",            2, 0.1, "V"),
        new(0x1C, "OutputFrequency",      "Frequenza uscita",           2, 0.1, "Hz"),
        new(0x1E, "LoadPercent",          "Carico",                     1, 1,   "%"),
        new(0x20, "BatteryVoltage",       "Tensione batteria",          1, 0.1, "V"),
        new(0x34, "RemainingCapacity",    "Carica batteria",            1, 1,   "%"),
        new(0x35, "RunTimeToEmpty",       "Autonomia",                  2, 1,   "s"),
        new(0x37, "FullChargeCapacity",   "Capacità a piena carica",    1, 1,   "%"),
        new(0x38, "WarningCapacityLimit", "Soglia avviso capacità",     1, 1,   "%",  Writable: true, Min: 5, Max: 50),
        new(0x46, "OutputCurrent",        "Corrente uscita",            1, 0.1, "A"),
        new(0x47, "OutputActivePower",    "Potenza attiva uscita",      1, 1,   "W"),
        new(0x61, "DelayBeforeStartup",   "Ritardo avvio uscita",       2, 1,   "s",  Writable: true, Dangerous: true, Min: 0, Max: 65535),
        new(0x6C, "StatusFlags",          "Flag di stato",              2, 1,   "bits"),
    };

    public static UpsReportDef? ByName(string name) =>
        Reports.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
}
