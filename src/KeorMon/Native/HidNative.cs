using System.Runtime.InteropServices;

namespace KeorMon.Native;

/// <summary>
/// P/Invoke surface for the Windows HID stack (hid.dll + setupapi.dll) and the
/// power API used to hibernate.
/// </summary>
internal static class HidNative
{
    public const int DIGCF_PRESENT = 0x02;
    public const int DIGCF_DEVICEINTERFACE = 0x10;
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x01;
    public const uint FILE_SHARE_WRITE = 0x02;
    public const uint OPEN_EXISTING = 3;
    public const int HIDP_STATUS_SUCCESS = 0x110000;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

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
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort FeatureReportByteLength_Unused; // placeholder, see note below
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
    public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize, ref int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid hidGuid);
    [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES attributes);
    [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr preparsed);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr preparsed);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetFeature(IntPtr h, byte[] buffer, int length);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_SetFeature(IntPtr h, byte[] buffer, int length);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] public static extern bool HidD_GetManufacturerString(IntPtr h, byte[] b, int len);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] public static extern bool HidD_GetProductString(IntPtr h, byte[] b, int len);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] public static extern bool HidD_GetSerialNumberString(IntPtr h, byte[] b, int len);

    // HidP_GetCaps is called through a raw buffer rather than the struct above: the
    // layout of HIDP_CAPS differs subtly between SDK versions and only three fields
    // are needed here (UsagePage, InputReportByteLength, FeatureReportByteLength).
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr preparsed, byte[] caps);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    // --- SeShutdownPrivilege: required by SetSuspendState, especially when running
    // --- as a Windows service. The privilege is present in the token but disabled.
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public int PrivilegeCount; public LUID Luid; public int Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAll,
        ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    public static bool EnableShutdownPrivilege()
    {
        const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x08;
        const int SE_PRIVILEGE_ENABLED = 0x02;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            return false;
        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) return false;
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { CloseHandle(token); }
    }

    /// <summary>Enumerates the device paths of every present HID interface.</summary>
    public static List<string> EnumerateHidPaths()
    {
        var paths = new List<string>();
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == INVALID_HANDLE_VALUE) return paths;

        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA();
            iface.cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref iface); i++)
            {
                int required = 0;
                SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, ref required, IntPtr.Zero);
                if (required <= 0) continue;

                var buffer = Marshal.AllocHGlobal(required);
                try
                {
                    // cbSize describes only the fixed part of the struct: 8 on x64, 6 on x86.
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref iface, buffer, required, ref required, IntPtr.Zero))
                    {
                        var path = Marshal.PtrToStringUni(buffer + 4);
                        if (!string.IsNullOrEmpty(path)) paths.Add(path);
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        return paths;
    }

    public static string? GetHidString(IntPtr handle, Func<IntPtr, byte[], int, bool> getter)
    {
        var buffer = new byte[512];
        if (!getter(handle, buffer, buffer.Length)) return null;
        return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
    }
}
