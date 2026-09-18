using System.Management;
using System.Windows.Forms;
using KeorMon.Native;

namespace KeorMon.Ups;

/// <summary>
/// Finds and talks to the UPS's HID Power Device interface (usage page 0x84).
/// </summary>
public sealed class UpsDevice
{
    public string Path { get; }
    public ushort VendorId { get; }
    public ushort ProductId { get; }
    public string? Manufacturer { get; }
    public string? Product { get; }
    public int FeatureReportSize { get; }

    private UpsDevice(string path, ushort vid, ushort pid, string? manufacturer, string? product, int featureSize)
    {
        Path = path; VendorId = vid; ProductId = pid;
        Manufacturer = manufacturer; Product = product;
        FeatureReportSize = featureSize;
    }

    /// <summary>Locates the first HID interface whose top-level usage page is 0x84 (Power Device).</summary>
    public static UpsDevice? Find()
    {
        foreach (var path in HidNative.EnumerateHidPaths())
        {
            // Access 0: descriptor queries only; never conflicts with exclusive holders.
            var h = HidNative.CreateFile(path, 0,
                HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
                IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == HidNative.INVALID_HANDLE_VALUE) continue;

            try
            {
                if (!HidNative.HidD_GetPreparsedData(h, out var pp)) continue;
                try
                {
                    var caps = new byte[128];
                    if (HidNative.HidP_GetCaps(pp, caps) != HidNative.HIDP_STATUS_SUCCESS) continue;
                    // HIDP_CAPS fixed layout: UsagePage @2, InputReportByteLength @4,
                    // OutputReportByteLength @6, FeatureReportByteLength @8.
                    ushort usagePage = BitConverter.ToUInt16(caps, 2);
                    ushort featureLen = BitConverter.ToUInt16(caps, 8);
                    if (usagePage != 0x84) continue;

                    var attr = new HidNative.HIDD_ATTRIBUTES { Size = System.Runtime.InteropServices.Marshal.SizeOf<HidNative.HIDD_ATTRIBUTES>() };
                    HidNative.HidD_GetAttributes(h, ref attr);

                    return new UpsDevice(path, attr.VendorID, attr.ProductID,
                        HidNative.GetHidString(h, HidNative.HidD_GetManufacturerString),
                        HidNative.GetHidString(h, HidNative.HidD_GetProductString),
                        featureLen);
                }
                finally { HidNative.HidD_FreePreparsedData(pp); }
            }
            finally { HidNative.CloseHandle(h); }
        }
        return null;
    }

    private IntPtr Open(bool forWrite = false)
    {
        uint access = HidNative.GENERIC_READ | (forWrite ? HidNative.GENERIC_WRITE : 0);
        var h = HidNative.CreateFile(Path, access,
            HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
            IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == HidNative.INVALID_HANDLE_VALUE)
            throw new IOException($"Cannot open UPS HID interface (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
        return h;
    }

    /// <summary>Reads one raw feature report; null if the device rejects the ID.</summary>
    public byte[]? ReadRawReport(byte reportId, IntPtr handle = default)
    {
        bool own = handle == default;
        var h = own ? Open() : handle;
        try
        {
            var buffer = new byte[FeatureReportSize];
            buffer[0] = reportId;
            return HidNative.HidD_GetFeature(h, buffer, buffer.Length) ? buffer : null;
        }
        finally { if (own) HidNative.CloseHandle(h); }
    }

    private static (long Raw, double? Value) Decode(byte[] buffer, UpsReportDef def)
    {
        long raw = 0;
        for (int i = 0; i < def.Bytes; i++) raw |= (long)buffer[i + 1] << (8 * i);
        bool unset = (def.Bytes == 1 && raw == 0xFF) || (def.Bytes == 2 && raw == 0xFFFF);
        return (raw, unset ? null : Math.Round(raw * def.Scale, 2));
    }

    public double? ReadValue(string name)
    {
        var def = UpsReportMap.ByName(name) ?? throw new ArgumentException($"Unknown parameter '{name}'");
        var buf = ReadRawReport(def.Id);
        return buf is null ? null : Decode(buf, def).Value;
    }

    /// <summary>
    /// Writes one writable parameter via HidD_SetFeature and reads it back.
    /// Returns (accepted, valueAfter). Dangerous parameters (output shutdown delays)
    /// must be explicitly allowed by the caller.
    /// </summary>
    public (bool Accepted, double? After) WriteValue(string name, int value, bool allowDangerous = false)
    {
        var def = UpsReportMap.ByName(name) ?? throw new ArgumentException($"Unknown parameter '{name}'");
        if (!def.Writable) throw new InvalidOperationException($"'{name}' is read-only");
        if (def.Dangerous && !allowDangerous)
            throw new InvalidOperationException($"'{name}' can cut power to the UPS output; explicit confirmation required");
        if (value < def.Min || value > def.Max)
            throw new ArgumentOutOfRangeException(nameof(value), $"{value} out of range {def.Min}..{def.Max} {def.Unit}");

        var h = Open(forWrite: true);
        try
        {
            var buffer = new byte[FeatureReportSize];
            buffer[0] = def.Id;
            int raw = (int)Math.Round(value / def.Scale);
            for (int i = 0; i < def.Bytes; i++) buffer[i + 1] = (byte)((raw >> (8 * i)) & 0xFF);
            if (!HidNative.HidD_SetFeature(h, buffer, buffer.Length))
                return (false, null);
        }
        finally { HidNative.CloseHandle(h); }

        // The Keor SP firmware acks HidD_SetFeature immediately but reflects the new
        // value in the feature report only after a while (~1.1 s measured for
        // AudibleAlarmControl, occasionally longer). Poll instead of a single check.
        double? after = null;
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(250);
            after = ReadValue(name);
            if (after.HasValue && Math.Abs(after.Value - value) < 0.001)
                return (true, after);
        }
        return (false, after);
    }

    /// <summary>
    /// Wake-up nudge for a frozen measurement processor: the Keor SP's internal MCU can
    /// stop refreshing measurements toward the USB bridge (all reports freeze bit-for-bit);
    /// an otherwise harmless HidD_SetFeature — rewriting the audible-alarm setting with its
    /// current value — kicks the internal link back to life (verified in the field).
    /// </summary>
    public bool WakeUp()
    {
        try
        {
            var current = ReadValue("AudibleAlarmControl");
            if (current is not (2 or 3)) return false;
            var def = UpsReportMap.ByName("AudibleAlarmControl")!;
            var h = Open(forWrite: true);
            try
            {
                var buffer = new byte[FeatureReportSize];
                buffer[0] = def.Id;
                buffer[1] = (byte)current.Value;
                return HidNative.HidD_SetFeature(h, buffer, buffer.Length);
            }
            finally { HidNative.CloseHandle(h); }
        }
        catch { return false; }
    }

    /// <summary>Full snapshot: every mapped report in one handle, plus mains detection.</summary>
    public UpsStatus ReadStatus()
    {
        var values = new Dictionary<string, double?>();
        var h = Open();
        try
        {
            foreach (var def in UpsReportMap.Reports)
            {
                var buf = ReadRawReport(def.Id, h);
                values[def.Name] = buf is null ? null : Decode(buf, def).Value;
            }
        }
        finally { HidNative.CloseHandle(h); }

        double? Get(string n) => values.TryGetValue(n, out var v) ? v : null;

        bool onBattery = DetectOnBattery(Get("InputVoltage"), Get("LowVoltageTransfer"));

        return new UpsStatus
        {
            Manufacturer = Manufacturer,
            Model = Product,
            OnBattery = onBattery,
            ChargePercent = Get("RemainingCapacity"),
            RuntimeSeconds = Get("RunTimeToEmpty"),
            LoadPercent = Get("LoadPercent"),
            OutputActivePowerW = Get("OutputActivePower"),
            InputVoltage = Get("InputVoltage"),
            InputFrequency = Get("InputFrequency"),
            OutputVoltage = Get("OutputVoltage"),
            OutputFrequency = Get("OutputFrequency"),
            OutputCurrent = Get("OutputCurrent"),
            BatteryVoltage = Get("BatteryVoltage"),
            NominalApparentPower = Get("NominalApparentPower"),
            LowVoltageTransfer = Get("LowVoltageTransfer"),
            HighVoltageTransfer = Get("HighVoltageTransfer"),
            StatusFlags = Get("StatusFlags") is { } f ? (int)f : null,
        };
    }

    /// <summary>
    /// Mains presence. Primary source: Windows' battery subsystem (it tracks the UPS's
    /// PresentStatus HID collection). Cross-checked against the measured input voltage.
    /// </summary>
    private static bool DetectOnBattery(double? inputVoltage, double? lowTransfer)
    {
        bool onBattery = false;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT BatteryStatus FROM Win32_Battery");
            foreach (var o in searcher.Get())
            {
                // 1 = discharging (on battery), 2 = on AC.
                onBattery = Convert.ToInt32(o["BatteryStatus"]) == 1;
                break;
            }
        }
        catch
        {
            onBattery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
        }

        // If the measured input voltage collapsed, we are on battery regardless.
        double threshold = (lowTransfer ?? 170) * 0.5;
        if (inputVoltage.HasValue && inputVoltage.Value < threshold) onBattery = true;

        return onBattery;
    }
}
