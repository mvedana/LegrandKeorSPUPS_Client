using KeorMon.Data;
using Microsoft.Extensions.Hosting;

namespace KeorMon.Monitor;

/// <summary>
/// Hosted service wrapper: runs the MonitorEngine inside the Windows service.
/// The engine owns sampling, SQLite history, alert events and hibernation.
/// </summary>
public sealed class MonitorWorker : BackgroundService
{
    private AppConfig? _cfg;
    private UpsDatabase? _db;
    private MonitorEngine? _engine;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _cfg = AppConfig.Load();
        _db = new UpsDatabase(AppConfig.DbPath);
        _engine = new MonitorEngine(_cfg, _db);
        _engine.Start();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* service stopping */ }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _engine?.Dispose();
        _db?.Dispose();
        return base.StopAsync(cancellationToken);
    }
}

/// <summary>Small helper to detect whether the KeorMon service is installed and running.</summary>
public static class ServiceUtil
{
    public const string ServiceName = "KeorMon";

    public static bool IsServiceRunning()
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(ServiceName);
            return sc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
        }
        catch { return false; }
    }
}
