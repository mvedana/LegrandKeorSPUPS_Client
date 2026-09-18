<#
.SYNOPSIS
    Talks to a USB HID UPS (HID Power Device class) on Windows.

.DESCRIPTION
    Built for and verified against a Legrand Keor SP 800VA (VID 0x0665 / PID 0x5161),
    which exposes a HID Power Device collection (usage page 0x84) where every
    measurement lives in its own single-value feature report.

    Rather than relying on HidP_GetUsageValue (whose report-descriptor metadata this
    firmware fills in inconsistently), values are read as raw feature reports and
    decoded with an explicit report map, which matches the device's actual behaviour.

    Public functions:
        Find-Ups               locate the HID Power Device interface
        Get-UpsStatus          full decoded snapshot
        Get-UpsRawReport       one raw feature report
        Get-UpsParameter       one decoded writable parameter
        Set-UpsParameter       write one whitelisted parameter (needs -Confirm/-Force)
        Get-UpsReportMap       the report map itself
#>

Set-StrictMode -Version Latest

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class UpsHidNative
{
    public const int DIGCF_PRESENT = 0x02;
    public const int DIGCF_DEVICEINTERFACE = 0x10;
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x01;
    public const uint FILE_SHARE_WRITE = 0x02;
    public const uint OPEN_EXISTING = 3;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
    public const int HIDP_STATUS_SUCCESS = 0x110000;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, int Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, int MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, int DeviceInterfaceDetailDataSize, ref int RequiredSize, IntPtr DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid HidGuid);
    [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetFeature(IntPtr h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetFeature(IntPtr h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetInputReport(IntPtr h, byte[] buf, int len);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] public static extern bool HidD_GetManufacturerString(IntPtr h, byte[] b, int len);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] public static extern bool HidD_GetProductString(IntPtr h, byte[] b, int len);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] public static extern bool HidD_GetSerialNumberString(IntPtr h, byte[] b, int len);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, ref HIDP_CAPS caps);

    public static List<string> EnumerateHidPaths()
    {
        var paths = new List<string>();
        Guid hidGuid;
        HidD_GetHidGuid(out hidGuid);
        IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == INVALID_HANDLE_VALUE) return paths;
        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA();
            iface.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref iface); i++)
            {
                int required = 0;
                SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, ref required, IntPtr.Zero);
                if (required <= 0) continue;
                IntPtr buf = Marshal.AllocHGlobal(required);
                try
                {
                    // cbSize covers only the fixed part: 8 bytes on x64, 6 on x86.
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref iface, buf, required, ref required, IntPtr.Zero))
                        paths.Add(Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4)));
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return paths;
    }
}
'@ -ErrorAction SilentlyContinue

# ---------------------------------------------------------------------------
# Report map.
#
# Each entry describes one single-value feature report of the device:
#   Name   friendly name
#   Bytes  payload width after the report ID
#   Scale  multiply the raw integer by this to get Unit
#   Unit   display unit
#   Write  $true if the report is a settable parameter (see $script:WritableSafe)
#   Min/Max  accepted range, in Unit, when writing
# ---------------------------------------------------------------------------
# NB: kept as an array, not a hashtable keyed by report ID — an [ordered] dictionary
# with integer keys indexes by POSITION, which silently returns the wrong entry.
$script:ReportMap = @(
    @{ Id=0x01; Name='NominalInputVoltage';  Bytes=1; Scale=1;    Unit='V'  }
    @{ Id=0x02; Name='NominalFrequency';     Bytes=1; Scale=1;    Unit='Hz' }
    @{ Id=0x03; Name='NominalApparentPower'; Bytes=2; Scale=1;    Unit='VA' }
    @{ Id=0x04; Name='NominalBatteryVoltage';Bytes=1; Scale=1;    Unit='V'  }
    @{ Id=0x06; Name='LowVoltageTransfer';   Bytes=1; Scale=1;    Unit='V'; Write=$true; Min=160; Max=200 }
    @{ Id=0x09; Name='HighVoltageTransfer';  Bytes=2; Scale=1;    Unit='V'; Write=$true; Min=250; Max=300 }
    @{ Id=0x10; Name='Test';                 Bytes=1; Scale=1;    Unit='code'; Write=$true; Min=0; Max=6 }
    @{ Id=0x11; Name='AudibleAlarmControl';  Bytes=1; Scale=1;    Unit='code'; Write=$true; Min=1; Max=3 }
    @{ Id=0x15; Name='DelayBeforeShutdown';  Bytes=2; Scale=1;    Unit='s'; Write=$true; Min=0; Max=65535 }
    @{ Id=0x17; Name='DelayBeforeReboot';    Bytes=2; Scale=1;    Unit='s'; Write=$true; Min=0; Max=65535 }
    @{ Id=0x18; Name='InputVoltage';         Bytes=2; Scale=0.1;  Unit='V'  }
    @{ Id=0x19; Name='InputFrequency';       Bytes=2; Scale=0.1;  Unit='Hz' }
    @{ Id=0x1B; Name='OutputVoltage';        Bytes=2; Scale=0.1;  Unit='V'  }
    @{ Id=0x1C; Name='OutputFrequency';      Bytes=2; Scale=0.1;  Unit='Hz' }
    @{ Id=0x1E; Name='LoadPercent';          Bytes=1; Scale=1;    Unit='%'  }
    @{ Id=0x20; Name='BatteryVoltage';       Bytes=1; Scale=0.1;  Unit='V'  }
    @{ Id=0x21; Name='BatteryCapacityPct';   Bytes=1; Scale=1;    Unit='%'  }
    @{ Id=0x30; Name='ConfigOutputVoltage';  Bytes=1; Scale=1;    Unit='V'  }
    @{ Id=0x31; Name='MeasuredVoltage';      Bytes=2; Scale=0.1;  Unit='V'  }
    @{ Id=0x34; Name='RemainingCapacity';    Bytes=1; Scale=1;    Unit='%'  }
    @{ Id=0x35; Name='RunTimeToEmpty';       Bytes=2; Scale=1;    Unit='s'  }
    @{ Id=0x36; Name='DesignCapacity';       Bytes=1; Scale=1;    Unit='%'  }
    @{ Id=0x37; Name='FullChargeCapacity';   Bytes=1; Scale=1;    Unit='%'  }
    @{ Id=0x38; Name='WarningCapacityLimit'; Bytes=1; Scale=1;    Unit='%'; Write=$true; Min=5; Max=50 }
    @{ Id=0x46; Name='OutputCurrent';        Bytes=1; Scale=0.1;  Unit='A'  }
    @{ Id=0x47; Name='OutputActivePower';    Bytes=1; Scale=1;    Unit='W'  }
    @{ Id=0x61; Name='DelayBeforeStartup';   Bytes=2; Scale=1;    Unit='s'; Write=$true; Min=0; Max=65535 }
    @{ Id=0x62; Name='RelativeStateOfCharge';Bytes=1; Scale=1;    Unit='%'  }
    @{ Id=0x6C; Name='StatusFlags';          Bytes=2; Scale=1;    Unit='bits' }
)

function Get-UpsReportDef {
    param([int]$Id, [string]$Name)
    foreach ($d in $script:ReportMap) {
        if ($PSBoundParameters.ContainsKey('Id')   -and $d.Id   -eq $Id)   { return $d }
        if ($PSBoundParameters.ContainsKey('Name') -and $d.Name -eq $Name) { return $d }
    }
    return $null
}

# Parameters Set-UpsParameter will touch without -Dangerous. The delay reports are
# excluded on purpose: writing them arms an actual UPS output shutdown.
$script:WritableSafe    = @('LowVoltageTransfer','HighVoltageTransfer','AudibleAlarmControl','WarningCapacityLimit','Test')
$script:WritableDanger  = @('DelayBeforeShutdown','DelayBeforeReboot','DelayBeforeStartup')

function Get-UpsReportMap { $script:ReportMap }

function Get-UpsWritableParameter {
    foreach ($d in $script:ReportMap) {
        if ($d.ContainsKey('Write') -and $d.Write) {
            [pscustomobject]@{
                Name      = $d.Name
                ReportID  = ('0x{0:X2}' -f $d.Id)
                Unit      = $d.Unit
                Min       = $d.Min
                Max       = $d.Max
                Dangerous = $d.Name -in $script:WritableDanger
            }
        }
    }
}

function Find-Ups {
    <#
    .SYNOPSIS
        Returns the HID interface that exposes a Power Device collection (usage page 0x84).
    #>
    [CmdletBinding()]
    param([ushort]$VendorID = 0, [ushort]$ProductID = 0)

    foreach ($path in [UpsHidNative]::EnumerateHidPaths()) {
        # Open with no access rights: enough for descriptor queries, and it never
        # fails on interfaces another process holds exclusively.
        $h = [UpsHidNative]::CreateFile($path, 0,
                [UpsHidNative]::FILE_SHARE_READ -bor [UpsHidNative]::FILE_SHARE_WRITE,
                [IntPtr]::Zero, [UpsHidNative]::OPEN_EXISTING, 0, [IntPtr]::Zero)
        if ($h -eq [UpsHidNative]::INVALID_HANDLE_VALUE) { continue }
        try {
            $pp = [IntPtr]::Zero
            if (-not [UpsHidNative]::HidD_GetPreparsedData($h, [ref]$pp)) { continue }
            try {
                $caps = New-Object UpsHidNative+HIDP_CAPS
                if ([UpsHidNative]::HidP_GetCaps($pp, [ref]$caps) -ne [UpsHidNative]::HIDP_STATUS_SUCCESS) { continue }
                if ($caps.UsagePage -ne 0x84) { continue }

                $attr = New-Object UpsHidNative+HIDD_ATTRIBUTES
                $attr.Size = [Runtime.InteropServices.Marshal]::SizeOf($attr)
                [void][UpsHidNative]::HidD_GetAttributes($h, [ref]$attr)
                if ($VendorID  -and $attr.VendorID  -ne $VendorID)  { continue }
                if ($ProductID -and $attr.ProductID -ne $ProductID) { continue }

                $str = {
                    param($fn)
                    $b = New-Object byte[] 512
                    if (& $fn $h $b $b.Length) { ([Text.Encoding]::Unicode.GetString($b)).TrimEnd([char]0).Trim() } else { $null }
                }
                return [pscustomobject]@{
                    Path              = $path
                    VendorID          = $attr.VendorID
                    ProductID         = $attr.ProductID
                    Version           = $attr.VersionNumber
                    Manufacturer      = & $str { param($x,$y,$z) [UpsHidNative]::HidD_GetManufacturerString($x,$y,$z) }
                    Product           = & $str { param($x,$y,$z) [UpsHidNative]::HidD_GetProductString($x,$y,$z) }
                    SerialNumber      = & $str { param($x,$y,$z) [UpsHidNative]::HidD_GetSerialNumberString($x,$y,$z) }
                    FeatureReportSize = [int]$caps.FeatureReportByteLength
                    InputReportSize   = [int]$caps.InputReportByteLength
                }
            } finally { [void][UpsHidNative]::HidD_FreePreparsedData($pp) }
        } finally { [void][UpsHidNative]::CloseHandle($h) }
    }
    return $null
}

function Open-UpsHandle {
    param([Parameter(Mandatory)][string]$Path, [switch]$ForWrite)
    $access = [UpsHidNative]::GENERIC_READ
    if ($ForWrite) { $access = $access -bor [UpsHidNative]::GENERIC_WRITE }
    $h = [UpsHidNative]::CreateFile($Path, $access,
            [UpsHidNative]::FILE_SHARE_READ -bor [UpsHidNative]::FILE_SHARE_WRITE,
            [IntPtr]::Zero, [UpsHidNative]::OPEN_EXISTING, 0, [IntPtr]::Zero)
    if ($h -eq [UpsHidNative]::INVALID_HANDLE_VALUE) {
        $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw "CreateFile failed on $Path (Win32 error $err)"
    }
    $h
}

function Get-UpsRawReport {
    <#
    .SYNOPSIS
        Reads one feature report and returns its raw bytes (report ID included).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ReportID,
        [string]$Path,
        [int]$Size,
        [IntPtr]$Handle = [IntPtr]::Zero
    )
    $ownHandle = $false
    if ($Handle -eq [IntPtr]::Zero) {
        if (-not $Path) { $dev = Find-Ups; if (-not $dev) { throw 'UPS not found' }; $Path = $dev.Path; $Size = $dev.FeatureReportSize }
        $Handle = Open-UpsHandle -Path $Path
        $ownHandle = $true
    }
    if (-not $Size) { $Size = 22 }
    try {
        $buf = New-Object byte[] $Size
        $buf[0] = [byte]$ReportID
        if (-not [UpsHidNative]::HidD_GetFeature($Handle, $buf, $Size)) { return $null }
        return $buf
    } finally { if ($ownHandle) { [void][UpsHidNative]::CloseHandle($Handle) } }
}

function ConvertFrom-UpsReport {
    param([byte[]]$Buffer, [hashtable]$Def)
    $raw = 0
    for ($i = 0; $i -lt $Def.Bytes; $i++) { $raw = $raw -bor ([int]$Buffer[$i + 1] -shl (8 * $i)) }
    # 0xFF / 0xFFFF means "not set" on this firmware.
    $unset = ($Def.Bytes -eq 1 -and $raw -eq 0xFF) -or ($Def.Bytes -eq 2 -and $raw -eq 0xFFFF)
    [pscustomobject]@{
        Raw    = $raw
        Value  = if ($unset) { $null } else { [math]::Round($raw * $Def.Scale, 2) }
        Unit   = $Def.Unit
        IsUnset= $unset
    }
}

function Get-UpsStatus {
    <#
    .SYNOPSIS
        Full decoded snapshot: device identity, live measurements and derived state.

    .DESCRIPTION
        HID gives the measurements. Mains presence is taken from Windows' own battery
        subsystem (Win32_Battery / PowerStatus), which already tracks the UPS's
        PresentStatus collection and is the most reliable on-battery signal, with the
        measured input voltage as a cross-check.
    #>
    [CmdletBinding()]
    param([switch]$IncludeRaw)

    $dev = Find-Ups
    if (-not $dev) { throw 'No HID Power Device (usage page 0x84) found. Is the UPS USB cable connected?' }

    $h = Open-UpsHandle -Path $dev.Path
    $fields = [ordered]@{}
    $raws   = [ordered]@{}
    try {
        foreach ($def in $script:ReportMap) {
            $buf = Get-UpsRawReport -ReportID $def.Id -Handle $h -Size $dev.FeatureReportSize
            if ($null -eq $buf) { $fields[$def.Name] = $null; continue }
            $d = ConvertFrom-UpsReport -Buffer $buf -Def $def
            $fields[$def.Name] = $d.Value
            if ($IncludeRaw) { $raws[$def.Name] = ('0x{0:X2}: {1}' -f $def.Id, (($buf[0..$def.Bytes] | ForEach-Object { '{0:X2}' -f $_ }) -join ' ')) }
        }
    } finally { [void][UpsHidNative]::CloseHandle($h) }

    # --- mains presence -----------------------------------------------------
    $onBattery = $null
    $winBattery = Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($winBattery) {
        # BatteryStatus: 1 = discharging (on battery), 2 = on AC.
        $onBattery = ($winBattery.BatteryStatus -eq 1)
    }
    if ($null -eq $onBattery) {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue
        $ps = [System.Windows.Forms.SystemInformation]::PowerStatus
        $onBattery = ($ps.PowerLineStatus -eq 'Offline')
    }
    # Cross-check: a live input voltage far below the transfer point means no mains.
    $inV = $fields['InputVoltage']
    $lowTransfer = if ($fields['LowVoltageTransfer']) { $fields['LowVoltageTransfer'] } else { 170 }
    if ($null -ne $inV -and $inV -lt ($lowTransfer * 0.5)) { $onBattery = $true }

    $runtime = $fields['RunTimeToEmpty']
    $charge  = $fields['RemainingCapacity']

    [pscustomobject]@{
        Timestamp            = Get-Date
        Manufacturer         = $dev.Manufacturer
        Model                = $dev.Product
        SerialNumber         = $dev.SerialNumber
        FirmwareVersion      = ('{0:X4}' -f $dev.Version)
        OnBattery            = [bool]$onBattery
        ChargePercent        = $charge
        RuntimeSeconds       = $runtime
        RuntimeMinutes       = if ($null -ne $runtime) { [math]::Round($runtime / 60, 1) } else { $null }
        LoadPercent          = $fields['LoadPercent']
        OutputActivePowerW   = $fields['OutputActivePower']
        InputVoltage         = $fields['InputVoltage']
        InputFrequency       = $fields['InputFrequency']
        OutputVoltage        = $fields['OutputVoltage']
        OutputFrequency      = $fields['OutputFrequency']
        OutputCurrent        = $fields['OutputCurrent']
        BatteryVoltage       = $fields['BatteryVoltage']
        FullChargeCapacity   = $fields['FullChargeCapacity']
        NominalApparentPower = $fields['NominalApparentPower']
        LowVoltageTransfer   = $fields['LowVoltageTransfer']
        HighVoltageTransfer  = $fields['HighVoltageTransfer']
        WarningCapacityLimit = $fields['WarningCapacityLimit']
        AudibleAlarm         = $fields['AudibleAlarmControl']
        StatusFlags          = if ($null -ne $fields['StatusFlags']) { '0x{0:X4}' -f [int]$fields['StatusFlags'] } else { $null }
        WindowsBatteryStatus = if ($winBattery) { $winBattery.BatteryStatus } else { $null }
        RawReports           = if ($IncludeRaw) { $raws } else { $null }
        DevicePath           = $dev.Path
    }
}

function Get-UpsParameter {
    <#
    .SYNOPSIS
        Reads one parameter by name (see Get-UpsWritableParameter for the settable ones).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)

    $def = Get-UpsReportDef -Name $Name
    if ($null -eq $def) { throw "Unknown parameter '$Name'. Known: $((Get-UpsReportMap).Name -join ', ')" }

    $buf = Get-UpsRawReport -ReportID $def.Id
    if ($null -eq $buf) { throw ("Report 0x{0:X2} could not be read" -f $def.Id) }
    $d = ConvertFrom-UpsReport -Buffer $buf -Def $def
    [pscustomobject]@{ Name=$Name; ReportID=('0x{0:X2}' -f $def.Id); Value=$d.Value; Raw=$d.Raw; Unit=$def.Unit }
}

function Set-UpsParameter {
    <#
    .SYNOPSIS
        Writes one parameter to the UPS via HidD_SetFeature.

    .DESCRIPTION
        Only parameters marked writable in the report map can be set, and only within
        their declared range. The value is read back afterwards to confirm the device
        accepted it (some firmware silently ignores writes).

        The DelayBefore* parameters actually arm the UPS output: writing them can cut
        power to everything plugged in. They require -Dangerous on top of confirmation.

    .EXAMPLE
        Set-UpsParameter -Name AudibleAlarmControl -Value 3   # 2 = enabled, 3 = muted
    #>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][int]$Value,
        [switch]$Dangerous
    )

    $def = Get-UpsReportDef -Name $Name
    if ($null -eq $def) { throw "Unknown parameter '$Name'" }
    $id = $def.Id
    if (-not ($def.ContainsKey('Write') -and $def.Write)) {
        throw "'$Name' is read-only. Writable: $((Get-UpsWritableParameter).Name -join ', ')"
    }
    if ($Name -in $script:WritableDanger -and -not $Dangerous) {
        throw "'$Name' controls the UPS output and can cut power to connected equipment. Re-run with -Dangerous if that is intended."
    }
    if ($Value -lt $def.Min -or $Value -gt $def.Max) {
        throw "Value $Value out of range for '$Name' ($($def.Min)..$($def.Max) $($def.Unit))"
    }

    $before = Get-UpsParameter -Name $Name
    if (-not $PSCmdlet.ShouldProcess("UPS parameter '$Name' (report $($before.ReportID))",
            "set from $($before.Value) to $Value $($def.Unit)")) {
        return
    }

    $dev = Find-Ups
    if (-not $dev) { throw 'UPS not found' }
    $h = Open-UpsHandle -Path $dev.Path -ForWrite
    try {
        $buf = New-Object byte[] $dev.FeatureReportSize
        $buf[0] = [byte]$id
        $rawValue = [int][math]::Round($Value / $def.Scale)
        for ($i = 0; $i -lt $def.Bytes; $i++) { $buf[$i + 1] = [byte](($rawValue -shr (8 * $i)) -band 0xFF) }
        if (-not [UpsHidNative]::HidD_SetFeature($h, $buf, $dev.FeatureReportSize)) {
            $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            throw "HidD_SetFeature failed on report $($before.ReportID) (Win32 error $err). This firmware may not allow writing '$Name'."
        }
    } finally { [void][UpsHidNative]::CloseHandle($h) }

    # The firmware acks the write immediately but reflects the new value in the
    # feature report only after ~1-3 s: poll the readback instead of a single check.
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $after = $null
    do {
        Start-Sleep -Milliseconds 250
        $after = Get-UpsParameter -Name $Name
        if ($after.Value -eq $Value) { break }
    } while ($sw.ElapsedMilliseconds -lt 5000)

    [pscustomobject]@{
        Name      = $Name
        ReportID  = $before.ReportID
        Before    = $before.Value
        Requested = $Value
        After     = $after.Value
        Unit      = $def.Unit
        Accepted  = ($after.Value -eq $Value)
    }
}

Export-ModuleMember -Function Find-Ups, Get-UpsStatus, Get-UpsRawReport, Get-UpsParameter,
                              Set-UpsParameter, Get-UpsReportMap, Get-UpsReportDef,
                              Get-UpsWritableParameter, Open-UpsHandle
