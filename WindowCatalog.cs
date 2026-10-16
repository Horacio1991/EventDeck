using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
namespace EventDeck;
internal sealed record WindowChoice(IntPtr Handle, uint ProcessId, long ProcessStarted, string Title, string Application)
{
    public string Label => $"{Application} — {Title} [PID {ProcessId}]";
}
internal static class WindowCatalog
{
    private delegate bool EnumProc(IntPtr hwnd, IntPtr parameter);
    public static List<WindowChoice> List()
    {
        var choices = new List<WindowChoice>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == Environment.ProcessId || !IsWindowVisible(hwnd) || Cloaked(hwnd) || IsIconic(hwnd)) return true;
            var text = new StringBuilder(1024); GetWindowTextW(hwnd, text, text.Capacity);
            if (text.Length == 0) return true;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                choices.Add(new(hwnd, pid, process.StartTime.ToUniversalTime().Ticks, text.ToString(), process.ProcessName));
            }
            catch { /* Inaccessible/exiting sources are not eligible. */ }
            return true;
        }, IntPtr.Zero);
        return choices.OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
    public static bool Exists(WindowChoice source)
    {
        if (!IsWindow(source.Handle)) return false;
        GetWindowThreadProcessId(source.Handle, out var pid);
        if (pid != source.ProcessId) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.StartTime.ToUniversalTime().Ticks == source.ProcessStarted;
        }
        catch { return false; }
    }
    public static bool IsHidden(IntPtr hwnd) => !IsWindowVisible(hwnd) || Cloaked(hwnd);
    private static bool Cloaked(IntPtr hwnd) => DwmGetWindowAttribute(hwnd, 14, out int value, 4) == 0 && value != 0;
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr parameter);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
