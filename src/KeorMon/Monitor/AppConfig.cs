using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeorMon.Monitor;

public sealed class AppConfig
{
    public int PollSeconds { get; set; } = 10;

    public int WarnChargePercent { get; set; } = 50;
    public int WarnRuntimeSeconds { get; set; } = 900;
    public int CriticalChargePercent { get; set; } = 20;
    public int CriticalRuntimeSeconds { get; set; } = 300;
    public int OverloadPercent { get; set; } = 90;

    /// <summary>Seconds the critical state must persist before hibernating.</summary>
    public int CriticalGraceSeconds { get; set; } = 30;

    /// <summary>
    /// What to do when the critical state persists past the grace period:
    /// "simulate" (log + notify only), "hibernate" (default) or "shutdown".
    /// </summary>
    public string CriticalAction { get; set; } = "hibernate";

    /// <summary>Legacy flag, kept for old config files; mapped onto CriticalAction at load.</summary>
    public bool? HibernateEnabled { get; set; }

    public bool NotifyOnMainsLost { get; set; } = true;
    public bool NotifyOnMainsBack { get; set; } = true;
    /// <summary>Repeat an alert for a persisting condition after this many seconds.</summary>
    public int ReNotifySeconds { get; set; } = 300;

    /// <summary>Days of sample history to keep in SQLite.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>UI language: "auto" (follow the OS) or an ISO 639-1 code from L10n.Codes.</summary>
    public string Language { get; set; } = "auto";

    // ProgramData, not LocalAppData: the service (LocalSystem) and the tray app (user)
    // must share the same config and database.
    [JsonIgnore]
    public static string BaseDir =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KeorMon");

    [JsonIgnore]
    public static string ConfigPath => System.IO.Path.Combine(BaseDir, "config.json");

    [JsonIgnore]
    public static string DbPath => System.IO.Path.Combine(BaseDir, "keormon.db");

    [JsonIgnore]
    public static string LogPath => System.IO.Path.Combine(BaseDir, "keormon.log");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg0 = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts) ?? new AppConfig();
                // Migrate the legacy boolean: an explicit false meant "simulate only".
                if (cfg0.HibernateEnabled == false && cfg0.CriticalAction == "hibernate")
                    cfg0.CriticalAction = "simulate";
                cfg0.HibernateEnabled = null;
                if (cfg0.CriticalAction is not ("simulate" or "hibernate" or "shutdown"))
                    cfg0.CriticalAction = "hibernate";
                return cfg0;
            }
        }
        catch { /* corrupt config: fall back to defaults */ }
        var cfg = new AppConfig();
        cfg.Save();
        return cfg;
    }

    public void Save()
    {
        Directory.CreateDirectory(BaseDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
    }

    /// <summary>Copies every setting from another instance (used for hot-reload).</summary>
    public void CopyFrom(AppConfig other)
    {
        PollSeconds = other.PollSeconds;
        WarnChargePercent = other.WarnChargePercent;
        WarnRuntimeSeconds = other.WarnRuntimeSeconds;
        CriticalChargePercent = other.CriticalChargePercent;
        CriticalRuntimeSeconds = other.CriticalRuntimeSeconds;
        OverloadPercent = other.OverloadPercent;
        CriticalGraceSeconds = other.CriticalGraceSeconds;
        CriticalAction = other.CriticalAction;
        NotifyOnMainsLost = other.NotifyOnMainsLost;
        NotifyOnMainsBack = other.NotifyOnMainsBack;
        ReNotifySeconds = other.ReNotifySeconds;
        RetentionDays = other.RetentionDays;
        Language = other.Language;
    }

    /// <summary>
    /// One-time migration of config/db from the old per-user location (LocalAppData)
    /// to the shared ProgramData directory. Best effort.
    /// </summary>
    public static void MigrateFromLocalAppData()
    {
        try
        {
            var oldDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeorMon");
            if (!Directory.Exists(oldDir)) return;
            Directory.CreateDirectory(BaseDir);
            foreach (var name in new[] { "config.json", "keormon.db", "keormon.db-wal", "keormon.db-shm" })
            {
                var src = System.IO.Path.Combine(oldDir, name);
                var dst = System.IO.Path.Combine(BaseDir, name);
                if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst);
            }
        }
        catch { /* migration is opportunistic */ }
    }
}
