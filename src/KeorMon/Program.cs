using KeorMon.Data;
using KeorMon.Monitor;
using KeorMon.UI;
using KeorMon.Ups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace KeorMon;

/// <summary>
/// Entry point. Modes:
///   (default)   tray app + dashboard; if the KeorMon service is running the tray
///               only displays and notifies (the service owns history + hibernation)
///   --service   Windows service host (install with Install-KeorMon.ps1)
///   --worker    headless monitor in a console (debugging / scheduled task)
///   --once      one sample printed to stdout, then exit
///   --status    same as --once (alias)
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool service = args.Contains("--service", StringComparer.OrdinalIgnoreCase);
        bool worker = args.Contains("--worker", StringComparer.OrdinalIgnoreCase);
        bool once = args.Contains("--once", StringComparer.OrdinalIgnoreCase) ||
                    args.Contains("--status", StringComparer.OrdinalIgnoreCase);

        AppConfig.MigrateFromLocalAppData();
        L10n.Initialize(AppConfig.Load().Language);

        if (once) return RunOnce();
        if (service) return RunService();

        // Single instance per mode: a second GUI launch just exits (the tray icon
        // already exists); the worker keeps its own mutex so GUI + worker can coexist.
        using var mutex = new Mutex(true, worker ? "KeorMon.Worker" : "KeorMon.Gui", out bool isNew);
        if (!isNew)
        {
            Console.WriteLine("KeorMon è già in esecuzione.");
            return 0;
        }

        var cfg = AppConfig.Load();
        using var db = new UpsDatabase(AppConfig.DbPath);
        using var engine = new MonitorEngine(cfg, db);

        if (worker)
        {
            engine.Start();
            Console.WriteLine("KeorMon worker attivo. Ctrl+C per uscire.");
            var quit = new ManualResetEvent(false);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
            quit.WaitOne();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        // With the service running, the tray engine only feeds the UI and balloons:
        // history and hibernation belong to the service.
        engine.ActionsDelegated = ServiceUtil.IsServiceRunning();
        engine.Start();
        Application.Run(new TrayApp(engine, db, cfg));
        return 0;
    }

    private static int RunService()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowsService(o => o.ServiceName = ServiceUtil.ServiceName);
        builder.Services.AddHostedService<MonitorWorker>();
        builder.Build().Run();
        return 0;
    }

    private static int RunOnce()
    {
        var dev = UpsDevice.Find();
        if (dev is null)
        {
            Console.Error.WriteLine("UPS non trovato (nessuna interfaccia HID Power Device).");
            return 1;
        }
        var status = dev.ReadStatus();
        Console.WriteLine($"Legrand Keor SP UPS v{AppVersion.Short}");
        Console.WriteLine($"{dev.Manufacturer} {dev.Product}  (VID {dev.VendorId:X4} PID {dev.ProductId:X4})");
        Console.WriteLine(status.ToString());
        Console.WriteLine($"Soglie trasferimento: {status.LowVoltageTransfer}-{status.HighVoltageTransfer} V | " +
                          $"flags 0x{status.StatusFlags:X4} | freq in/out {status.InputFrequency}/{status.OutputFrequency} Hz");
        return 0;
    }
}
