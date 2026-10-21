using System.Runtime.InteropServices;

namespace EventDeck;

// Native display identity and geometry, independent of Screen.AllScreens' cache.
internal sealed record OutputDisplayTarget(string Device, string? Identity, int Left, int Top,
    int Width, int Height, bool Primary)
{
    internal static OutputDisplayTarget? Recover(string? identity, string? operatorDevice,
        IReadOnlyList<OutputDisplayTarget> displays)
    {
        if (string.IsNullOrEmpty(identity) || operatorDevice is null || displays.Count < 2) return null;
        var matches = displays.Where(x => string.Equals(x.Identity, identity, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 && !matches[0].Primary && matches[0].Device != operatorDevice
            ? matches[0] : null;
    }

    internal static List<OutputDisplayTarget> ReadAll()
    {
        var result = new List<OutputDisplayTarget>();
        MonitorCallback callback = (IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = string.Empty };
            if (GetMonitorInfo(monitor, ref info))
                result.Add(new(info.Device, ReadIdentity(info.Device), info.Bounds.Left, info.Bounds.Top,
                    info.Bounds.Right - info.Bounds.Left, info.Bounds.Bottom - info.Bounds.Top, (info.Flags & 1) != 0));
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)) result.Clear();
        return result;
    }

    internal static string? DeviceForWindow(IntPtr window)
    {
        var monitor = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = string.Empty };
        return monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info) ? info.Device : null;
    }

    internal bool Place(IntPtr window) => SetWindowPos(window, new IntPtr(-1), Left, Top, Width, Height, 0x0010);

    private static string? ReadIdentity(string device)
    {
        var identities = new List<string>();
        for (uint i = 0; ; i++)
        {
            var entry = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>(), Name = string.Empty,
                Description = string.Empty, Id = string.Empty, Key = string.Empty };
            if (!EnumDisplayDevices(device, i, ref entry, 1)) break; // EDD_GET_DEVICE_INTERFACE_NAME
            if ((entry.Flags & 1) != 0 && !string.IsNullOrWhiteSpace(entry.Id)) identities.Add(entry.Id);
        }
        // Cloned/ambiguous targets must never be guessed on reconnection.
        return identities.Count == 1 ? identities[0] : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Bounds, WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice info, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
