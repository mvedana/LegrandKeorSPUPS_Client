<#
.SYNOPSIS
    Reads live data from a USB HID UPS (HID Power Device, usage page 0x84/0x85).
    Tested against a Legrand Keor SP (VID_0665 / PID_5161).

.DESCRIPTION
    Enumerates HID interfaces, finds the collection whose top-level usage page is
    0x84 (Power Device), parses its report descriptor via HidP_GetValueCaps /
    HidP_GetButtonCaps, then reads every feature and input report and decodes the
    values by usage.

.PARAMETER Watch
    Poll continuously instead of printing a single snapshot.

.PARAMETER IntervalSeconds
    Poll interval when -Watch is used. Default 5.

.PARAMETER Raw
    Also print the raw bytes of every report that was read.
#>
[CmdletBinding()]
param(
    [switch]$Watch,
    [int]$IntervalSeconds = 5,
    [switch]$Raw
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class HidNative
{
    public const int DIGCF_PRESENT = 0x02;
    public const int DIGCF_DEVICEINTERFACE = 0x10;
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x01;
    public const uint FILE_SHARE_WRITE = 0x02;
    public const uint OPEN_EXISTING = 3;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

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

    // HIDP_VALUE_CAPS is a union-heavy struct; laid out explicitly.
    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        [MarshalAs(UnmanagedType.U1)] public bool IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        [MarshalAs(UnmanagedType.U1)] public bool IsRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsStringRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsDesignatorRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsAbsolute;
        [MarshalAs(UnmanagedType.U1)] public bool HasNull;
        public byte Reserved;
        public ushort BitSize;
        public ushort ReportCount;
        public ushort Reserved2a, Reserved2b, Reserved2c, Reserved2d, Reserved2e;
        public int UnitsExp;
        public int Units;
        public int LogicalMin;
        public int LogicalMax;
        public int PhysicalMin;
        public int PhysicalMax;
        // Range/NotRange union: widest member is the range form.
        public ushort UsageMin;        // NotRange: Usage
        public ushort UsageMax;        // NotRange: Reserved1
        public ushort StringMin;
        public ushort StringMax;
        public ushort DesignatorMin;
        public ushort DesignatorMax;
        public ushort DataIndexMin;
        public ushort DataIndexMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_BUTTON_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        [MarshalAs(UnmanagedType.U1)] public bool IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        [MarshalAs(UnmanagedType.U1)] public bool IsRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsStringRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsDesignatorRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsAbsolute;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
        public uint[] Reserved;
        public ushort UsageMin;
        public ushort UsageMax;
        public ushort StringMin;
        public ushort StringMax;
        public ushort DesignatorMin;
        public ushort DesignatorMax;
        public ushort DataIndexMin;
        public ushort DataIndexMax;
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
    [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(IntPtr HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);
    [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(IntPtr HidDeviceObject, out IntPtr PreparsedData);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);
    [DllImport("hid.dll")] public static extern bool HidD_GetFeature(IntPtr HidDeviceObject, byte[] ReportBuffer, int ReportBufferLength);
    [DllImport("hid.dll")] public static extern bool HidD_GetInputReport(IntPtr HidDeviceObject, byte[] ReportBuffer, int ReportBufferLength);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] public static extern bool HidD_GetManufacturerString(IntPtr HidDeviceObject, byte[] Buffer, int BufferLength);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] public static extern bool HidD_GetProductString(IntPtr HidDeviceObject, byte[] Buffer, int BufferLength);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] public static extern bool HidD_GetSerialNumberString(IntPtr HidDeviceObject, byte[] Buffer, int BufferLength);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] public static extern bool HidD_GetIndexedString(IntPtr HidDeviceObject, int StringIndex, byte[] Buffer, int BufferLength);

    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr PreparsedData, ref HIDP_CAPS Capabilities);
    [DllImport("hid.dll")] public static extern int HidP_GetValueCaps(int ReportType, [In, Out] HIDP_VALUE_CAPS[] ValueCaps, ref ushort ValueCapsLength, IntPtr PreparsedData);
    [DllImport("hid.dll")] public static extern int HidP_GetButtonCaps(int ReportType, [In, Out] HIDP_BUTTON_CAPS[] ButtonCaps, ref ushort ButtonCapsLength, IntPtr PreparsedData);
    [DllImport("hid.dll")] public static extern int HidP_GetUsageValue(int ReportType, ushort UsagePage, ushort LinkCollection, ushort Usage, out uint UsageValue, IntPtr PreparsedData, byte[] Report, int ReportLength);

    public const int HidP_Input = 0;
    public const int HidP_Output = 1;
    public const int HidP_Feature = 2;

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
            iface.cbSize = Marshal.SizeOf(iface);
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref iface); i++)
            {
                int required = 0;
                SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, ref required, IntPtr.Zero);
                if (required <= 0) continue;
                IntPtr buf = Marshal.AllocHGlobal(required);
                try
                {
                    // cbSize is the size of the fixed part only: 8 on x64, 6 on x86.
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
'@

# --- HID Power Device usage tables (usage pages 0x84 and 0x85) ---------------
$UsagePower = @{
    0x01='iName'; 0x02='PresentStatus'; 0x03='ChangedStatus'; 0x04='UPS'; 0x05='PowerSupply';
    0x10='BatterySystem'; 0x11='BatterySystemID'; 0x12='Battery'; 0x13='BatteryID';
    0x14='Charger'; 0x15='ChargerID'; 0x16='PowerConverter'; 0x17='PowerConverterID';
    0x18='OutletSystem'; 0x19='OutletSystemID'; 0x1A='Input'; 0x1B='InputID';
    0x1C='Output'; 0x1D='OutputID'; 0x1E='Flow'; 0x1F='FlowID';
    0x20='Outlet'; 0x21='OutletID'; 0x22='Gang'; 0x23='GangID';
    0x24='PowerSummary'; 0x25='PowerSummaryID';
    0x30='Voltage'; 0x31='Current'; 0x32='Frequency'; 0x33='ApparentPower'; 0x34='ActivePower';
    0x35='PercentLoad'; 0x36='Temperature'; 0x37='Humidity'; 0x38='BadCount';
    0x40='ConfigVoltage'; 0x41='ConfigCurrent'; 0x42='ConfigFrequency'; 0x43='ConfigApparentPower';
    0x44='ConfigActivePower'; 0x45='ConfigPercentLoad'; 0x46='ConfigTemperature'; 0x47='ConfigHumidity';
    0x50='SwitchOnControl'; 0x51='SwitchOffControl'; 0x52='ToggleControl'; 0x53='LowVoltageTransfer';
    0x54='HighVoltageTransfer'; 0x55='DelayBeforeReboot'; 0x56='DelayBeforeStartup';
    0x57='DelayBeforeShutdown'; 0x58='Test'; 0x59='ModuleReset'; 0x5A='AudibleAlarmControl';
    0x60='Present'; 0x61='Good'; 0x62='InternalFailure'; 0x63='VoltageOutOfRange';
    0x64='FrequencyOutOfRange'; 0x65='Overload'; 0x66='OverCharged'; 0x67='OverTemperature';
    0x68='ShutdownRequested'; 0x69='ShutdownImminent'; 0x6B='SwitchOnOff'; 0x6C='Switchable';
    0x6D='Used'; 0x6E='Boost'; 0x6F='Buck'; 0x70='Initialized'; 0x71='Tested';
    0x72='AwaitingPower'; 0x73='CommunicationLost';
    0xFD='iManufacturer'; 0xFE='iProduct'; 0xFF='iSerialNumber'
}
$UsageBattery = @{
    0x01='SMBBatteryMode'; 0x02='SMBBatteryStatus'; 0x03='SMBAlarmWarning';
    0x28='ManufacturerDate'; 0x29='Rechargeable'; 0x2A='WarningCapacityLimit';
    0x2B='CapacityGranularity1'; 0x2C='CapacityGranularity2'; 0x2D='RemainingCapacityLimit';
    0x2F='CapacityMode'; 0x40='BelowRemainingCapacityLimit'; 0x41='RemainingTimeLimitExpired';
    0x42='Charging'; 0x44='Discharging'; 0x45='FullyCharged'; 0x46='FullyDischarged';
    0x4B='NeedReplacement'; 0x65='AbsoluteStateOfCharge'; 0x66='RemainingCapacity';
    0x67='FullChargeCapacity'; 0x68='RunTimeToEmpty'; 0x69='AverageTimeToEmpty';
    0x6A='AverageTimeToFull'; 0x6B='CycleCount'; 0x83='DesignCapacity';
    0x85='ManufacturerName'; 0x86='DeviceName'; 0x87='DeviceChemistry'; 0x88='ManufacturerData';
    0x89='Rechargable'; 0x8B='iDeviceChemistry'; 0x8F='RelativeStateOfCharge';
    0x8D='iOEMInformation'; 0x8E='iDeviceName'; 0x84='iManufacturerName'
}

function Get-UsageName {
    param([int]$Page, [int]$Usage)
    switch ($Page) {
        0x84 { if ($UsagePower.ContainsKey($Usage)) { return $UsagePower[$Usage] } }
        0x85 { if ($UsageBattery.ContainsKey($Usage)) { return $UsageBattery[$Usage] } }
    }
    return ('Page0x{0:X2}/Usage0x{1:X2}' -f $Page, $Usage)
}

function Get-HidString {
    param([IntPtr]$Handle, [string]$Which, [int]$Index = 0)
    $buf = New-Object byte[] 512
    $ok = switch ($Which) {
        'Manufacturer' { [HidNative]::HidD_GetManufacturerString($Handle, $buf, $buf.Length) }
        'Product'      { [HidNative]::HidD_GetProductString($Handle, $buf, $buf.Length) }
        'Serial'       { [HidNative]::HidD_GetSerialNumberString($Handle, $buf, $buf.Length) }
        'Indexed'      { [HidNative]::HidD_GetIndexedString($Handle, $Index, $buf, $buf.Length) }
    }
    if (-not $ok) { return $null }
    ([System.Text.Encoding]::Unicode.GetString($buf)).TrimEnd([char]0)
}

function Find-UpsDevice {
    foreach ($path in [HidNative]::EnumerateHidPaths()) {
        $h = [HidNative]::CreateFile($path, 0, [HidNative]::FILE_SHARE_READ -bor [HidNative]::FILE_SHARE_WRITE,
                                     [IntPtr]::Zero, [HidNative]::OPEN_EXISTING, 0, [IntPtr]::Zero)
        if ($h -eq [HidNative]::INVALID_HANDLE_VALUE) { continue }
        try {
            $pp = [IntPtr]::Zero
            if (-not [HidNative]::HidD_GetPreparsedData($h, [ref]$pp)) { continue }
            try {
                $caps = New-Object HidNative+HIDP_CAPS
                if ([HidNative]::HidP_GetCaps($pp, [ref]$caps) -ne 0x110000) { continue }  # HIDP_STATUS_SUCCESS
                if ($caps.UsagePage -ne 0x84) { continue }
                $attr = New-Object HidNative+HIDD_ATTRIBUTES
                $attr.Size = [Runtime.InteropServices.Marshal]::SizeOf($attr)
                [void][HidNative]::HidD_GetAttributes($h, [ref]$attr)
                return [pscustomobject]@{
                    Path = $path; VendorID = $attr.VendorID; ProductID = $attr.ProductID
                    Version = $attr.VersionNumber; Caps = $caps
                }
            } finally { [void][HidNative]::HidD_FreePreparsedData($pp) }
        } finally { [void][HidNative]::CloseHandle($h) }
    }
    return $null
}

function Read-UpsSnapshot {
    param([string]$Path, [switch]$ShowRaw)

    $h = [HidNative]::CreateFile($Path, [HidNative]::GENERIC_READ,
            [HidNative]::FILE_SHARE_READ -bor [HidNative]::FILE_SHARE_WRITE,
            [IntPtr]::Zero, [HidNative]::OPEN_EXISTING, 0, [IntPtr]::Zero)
    if ($h -eq [HidNative]::INVALID_HANDLE_VALUE) {
        throw "Cannot open $Path (error $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))"
    }
    try {
        $pp = [IntPtr]::Zero
        if (-not [HidNative]::HidD_GetPreparsedData($h, [ref]$pp)) { throw 'HidD_GetPreparsedData failed' }
        try {
            $caps = New-Object HidNative+HIDP_CAPS
            [void][HidNative]::HidP_GetCaps($pp, [ref]$caps)

            $identity = [ordered]@{
                Manufacturer = Get-HidString $h 'Manufacturer'
                Product      = Get-HidString $h 'Product'
                Serial       = Get-HidString $h 'Serial'
            }

            $results = New-Object System.Collections.Generic.List[object]

            # Walk value caps for Feature and Input reports.
            foreach ($rt in @(([HidNative]::HidP_Feature), ([HidNative]::HidP_Input))) {
                $typeName = if ($rt -eq [HidNative]::HidP_Feature) { 'Feature' } else { 'Input' }
                $n = if ($rt -eq [HidNative]::HidP_Feature) { $caps.NumberFeatureValueCaps } else { $caps.NumberInputValueCaps }
                if ($n -le 0) { continue }
                $vc = New-Object HidNative+HIDP_VALUE_CAPS[] $n
                $len = [ushort]$n
                if ([HidNative]::HidP_GetValueCaps($rt, $vc, [ref]$len, $pp) -ne 0x110000) { continue }

                $bufLen = if ($rt -eq [HidNative]::HidP_Feature) { $caps.FeatureReportByteLength } else { $caps.InputReportByteLength }
                $cache = @{}

                for ($i = 0; $i -lt $len; $i++) {
                    $c = $vc[$i]
                    $usage = if ($c.IsRange) { $c.UsageMin } else { $c.UsageMin }  # NotRange stores Usage in the same slot
                    $rid = $c.ReportID

                    if (-not $cache.ContainsKey($rid)) {
                        $buf = New-Object byte[] $bufLen
                        $buf[0] = $rid
                        $ok = if ($rt -eq [HidNative]::HidP_Feature) {
                            [HidNative]::HidD_GetFeature($h, $buf, $bufLen)
                        } else {
                            [HidNative]::HidD_GetInputReport($h, $buf, $bufLen)
                        }
                        $cache[$rid] = if ($ok) { $buf } else { $null }
                        if ($ok -and $ShowRaw) {
                            Write-Host ("  raw {0} report 0x{1:X2}: {2}" -f $typeName, $rid,
                                (($buf | ForEach-Object { '{0:X2}' -f $_ }) -join ' ')) -ForegroundColor DarkGray
                        }
                    }
                    $buf = $cache[$rid]
                    if ($null -eq $buf) { continue }

                    $val = [uint32]0
                    $st = [HidNative]::HidP_GetUsageValue($rt, $c.UsagePage, $c.LinkCollection, $usage, [ref]$val, $pp, $buf, $bufLen)
                    if ($st -ne 0x110000) { continue }

                    # Sign-extend if the logical range is signed.
                    $v = [double]$val
                    if ($c.LogicalMin -lt 0 -and $c.BitSize -lt 32) {
                        $signBit = [math]::Pow(2, $c.BitSize - 1)
                        if ($v -ge $signBit) { $v -= [math]::Pow(2, $c.BitSize) }
                    }
                    # Apply the HID unit exponent (4-bit signed nibble).
                    $exp = $c.UnitsExp
                    if ($exp -gt 7) { $exp -= 16 }
                    if ($exp -ne 0) { $v = $v * [math]::Pow(10, $exp) }

                    $results.Add([pscustomobject]@{
                        ReportType = $typeName
                        ReportID   = ('0x{0:X2}' -f $rid)
                        Collection = $c.LinkCollection
                        Usage      = Get-UsageName $c.UsagePage $usage
                        Value      = [math]::Round($v, 3)
                        Raw        = $val
                        Unit       = ('0x{0:X8}' -f $c.Units)
                    })
                }
            }

            # Walk button caps (flags/status bits) for Feature and Input.
            foreach ($rt in @(([HidNative]::HidP_Feature), ([HidNative]::HidP_Input))) {
                $typeName = if ($rt -eq [HidNative]::HidP_Feature) { 'Feature' } else { 'Input' }
                $n = if ($rt -eq [HidNative]::HidP_Feature) { $caps.NumberFeatureButtonCaps } else { $caps.NumberInputButtonCaps }
                if ($n -le 0) { continue }
                $bc = New-Object HidNative+HIDP_BUTTON_CAPS[] $n
                $len = [ushort]$n
                if ([HidNative]::HidP_GetButtonCaps($rt, $bc, [ref]$len, $pp) -ne 0x110000) { continue }

                $bufLen = if ($rt -eq [HidNative]::HidP_Feature) { $caps.FeatureReportByteLength } else { $caps.InputReportByteLength }
                for ($i = 0; $i -lt $len; $i++) {
                    $c = $bc[$i]
                    $buf = New-Object byte[] $bufLen
                    $buf[0] = $c.ReportID
                    $ok = if ($rt -eq [HidNative]::HidP_Feature) {
                        [HidNative]::HidD_GetFeature($h, $buf, $bufLen)
                    } else {
                        [HidNative]::HidD_GetInputReport($h, $buf, $bufLen)
                    }
                    if (-not $ok) { continue }
                    $val = [uint32]0
                    $st = [HidNative]::HidP_GetUsageValue($rt, $c.UsagePage, $c.LinkCollection, $c.UsageMin, [ref]$val, $pp, $buf, $bufLen)
                    if ($st -ne 0x110000) { continue }
                    $results.Add([pscustomobject]@{
                        ReportType = $typeName
                        ReportID   = ('0x{0:X2}' -f $c.ReportID)
                        Collection = $c.LinkCollection
                        Usage      = Get-UsageName $c.UsagePage $c.UsageMin
                        Value      = [int]$val
                        Raw        = $val
                        Unit       = 'flag'
                    })
                }
            }

            [pscustomobject]@{ Identity = $identity; Values = $results }
        } finally { [void][HidNative]::HidD_FreePreparsedData($pp) }
    } finally { [void][HidNative]::CloseHandle($h) }
}

# --- main -------------------------------------------------------------------
$dev = Find-UpsDevice
if (-not $dev) { Write-Error 'No HID Power Device (usage page 0x84) found.'; exit 1 }

Write-Host ('UPS HID found: VID_{0:X4} PID_{1:X4}  rev {2:X4}' -f $dev.VendorID, $dev.ProductID, $dev.Version) -ForegroundColor Cyan
Write-Host ("  path: " + $dev.Path) -ForegroundColor DarkGray
Write-Host ('  reports: input={0}B output={1}B feature={2}B' -f $dev.Caps.InputReportByteLength, $dev.Caps.OutputReportByteLength, $dev.Caps.FeatureReportByteLength) -ForegroundColor DarkGray

do {
    $snap = Read-UpsSnapshot -Path $dev.Path -ShowRaw:$Raw
    Write-Host ''
    Write-Host ("=== {0} ===" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')) -ForegroundColor Cyan
    $snap.Identity.GetEnumerator() | ForEach-Object { if ($_.Value) { Write-Host ("  {0,-13}: {1}" -f $_.Key, $_.Value) } }
    Write-Host ''
    $snap.Values | Sort-Object ReportType, ReportID, Collection | Format-Table ReportType, ReportID, Collection, Usage, Value, Raw -AutoSize
    if ($Watch) { Start-Sleep -Seconds $IntervalSeconds }
} while ($Watch)
