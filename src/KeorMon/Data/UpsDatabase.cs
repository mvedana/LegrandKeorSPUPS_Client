using KeorMon.Ups;
using Microsoft.Data.Sqlite;

namespace KeorMon.Data;

/// <summary>One historical sample as stored in SQLite.</summary>
public sealed record Sample(
    DateTime Timestamp,
    bool OnBattery,
    string Severity,
    double? ChargePercent,
    double? RuntimeSeconds,
    double? LoadPercent,
    double? Watts,
    double? InputVoltage,
    double? OutputVoltage,
    double? BatteryVoltage,
    double? InputFrequency);

public sealed record EventRow(DateTime Timestamp, string Kind, string Message);

/// <summary>
/// SQLite persistence for samples (time series driving the charts) and events
/// (mains lost/back, alerts, hibernation).
/// </summary>
public sealed class UpsDatabase : IDisposable
{
    private readonly SqliteConnection _conn;

    public string DbPath { get; }

    public UpsDatabase(string dbPath)
    {
        DbPath = dbPath;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dbPath)!);
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS samples (
                ts           INTEGER NOT NULL,   -- unix epoch seconds
                on_battery   INTEGER NOT NULL,
                severity     TEXT    NOT NULL,
                charge_pct   REAL,
                runtime_sec  REAL,
                load_pct     REAL,
                watts        REAL,
                input_v      REAL,
                output_v     REAL,
                battery_v    REAL,
                input_hz     REAL
            );
            CREATE INDEX IF NOT EXISTS idx_samples_ts ON samples(ts);
            CREATE TABLE IF NOT EXISTS events (
                ts      INTEGER NOT NULL,
                kind    TEXT    NOT NULL,
                message TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_events_ts ON events(ts);
            """;
        cmd.ExecuteNonQuery();
    }

    public void InsertSample(UpsStatus s, Severity severity)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO samples (ts, on_battery, severity, charge_pct, runtime_sec, load_pct,
                                 watts, input_v, output_v, battery_v, input_hz)
            VALUES ($ts, $ob, $sev, $charge, $runtime, $load, $watts, $inv, $outv, $battv, $inhz)
            """;
        cmd.Parameters.AddWithValue("$ts", ToEpoch(s.Timestamp));
        cmd.Parameters.AddWithValue("$ob", s.OnBattery ? 1 : 0);
        cmd.Parameters.AddWithValue("$sev", severity.ToString());
        cmd.Parameters.AddWithValue("$charge", (object?)s.ChargePercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$runtime", (object?)s.RuntimeSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$load", (object?)s.LoadPercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$watts", (object?)s.OutputActivePowerW ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$inv", (object?)s.InputVoltage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$outv", (object?)s.OutputVoltage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$battv", (object?)s.BatteryVoltage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$inhz", (object?)s.InputFrequency ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void InsertEvent(string kind, string message)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO events (ts, kind, message) VALUES ($ts, $kind, $msg)";
        cmd.Parameters.AddWithValue("$ts", ToEpoch(DateTime.Now));
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$msg", message);
        cmd.ExecuteNonQuery();
    }

    public List<Sample> GetSamples(DateTime from, DateTime to, int maxPoints = 4000)
    {
        var result = new List<Sample>();
        using var cmd = _conn.CreateCommand();
        // Decimate straight in SQL when the window holds more rows than the chart needs.
        cmd.CommandText = """
            WITH win AS (SELECT COUNT(*) AS n FROM samples WHERE ts BETWEEN $from AND $to)
            SELECT ts, on_battery, severity, charge_pct, runtime_sec, load_pct,
                   watts, input_v, output_v, battery_v, input_hz
            FROM samples, win
            WHERE ts BETWEEN $from AND $to
              AND (win.n <= $max OR rowid % MAX(1, win.n / $max) = 0)
            ORDER BY ts
            """;
        cmd.Parameters.AddWithValue("$from", ToEpoch(from));
        cmd.Parameters.AddWithValue("$to", ToEpoch(to));
        cmd.Parameters.AddWithValue("$max", maxPoints);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new Sample(
                FromEpoch(r.GetInt64(0)),
                r.GetInt64(1) != 0,
                r.GetString(2),
                GetD(r, 3), GetD(r, 4), GetD(r, 5), GetD(r, 6),
                GetD(r, 7), GetD(r, 8), GetD(r, 9), GetD(r, 10)));
        }
        return result;
    }

    public List<EventRow> GetEvents(DateTime from, DateTime to, int limit = 500)
    {
        var result = new List<EventRow>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT ts, kind, message FROM events
            WHERE ts BETWEEN $from AND $to
            ORDER BY ts DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$from", ToEpoch(from));
        cmd.Parameters.AddWithValue("$to", ToEpoch(to));
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(new EventRow(FromEpoch(r.GetInt64(0)), r.GetString(1), r.GetString(2)));
        return result;
    }

    /// <summary>Deletes samples older than the retention window (events are kept).</summary>
    public void Prune(TimeSpan retention)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM samples WHERE ts < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", ToEpoch(DateTime.Now - retention));
        cmd.ExecuteNonQuery();
    }

    private static double? GetD(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    private static long ToEpoch(DateTime dt) => ((DateTimeOffset)dt.ToUniversalTime()).ToUnixTimeSeconds();
    private static DateTime FromEpoch(long s) => DateTimeOffset.FromUnixTimeSeconds(s).ToLocalTime().DateTime;

    public void Dispose() => _conn.Dispose();
}
