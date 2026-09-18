namespace KeorMon.Ups;

/// <summary>A decoded snapshot of the UPS state.</summary>
public sealed class UpsStatus
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public bool OnBattery { get; set; }
    public double? ChargePercent { get; set; }
    public double? RuntimeSeconds { get; set; }
    public double? LoadPercent { get; init; }
    public double? OutputActivePowerW { get; init; }
    public double? InputVoltage { get; init; }
    public double? InputFrequency { get; init; }
    public double? OutputVoltage { get; init; }
    public double? OutputFrequency { get; init; }
    public double? OutputCurrent { get; init; }
    public double? BatteryVoltage { get; init; }
    public double? NominalApparentPower { get; init; }
    public double? LowVoltageTransfer { get; init; }
    public double? HighVoltageTransfer { get; init; }
    public int? StatusFlags { get; init; }

    /// <summary>Estimated minutes to full charge while on mains; computed by the engine
    /// from the recent charge slope (the firmware exposes no AverageTimeToFull).</summary>
    public double? RechargeEtaMinutes { get; set; }

    public double? RuntimeMinutes => RuntimeSeconds.HasValue ? Math.Round(RuntimeSeconds.Value / 60, 1) : null;

    public string PowerSourceLabel => UI.L10n.T(OnBattery ? "battery" : "mains");

    public override string ToString() =>
        UI.L10n.F("st_line", PowerSourceLabel, ChargePercent, RuntimeMinutes,
            LoadPercent, OutputActivePowerW, InputVoltage, OutputVoltage, BatteryVoltage);
}

public enum Severity { Normal, Info, Warning, Critical }

public sealed record Assessment(Severity Level, IReadOnlyList<string> Reasons);
