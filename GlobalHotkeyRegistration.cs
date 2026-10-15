using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;

namespace EventDeck;

/// <summary>Registers one application-specific hotkey without installing a keyboard hook.</summary>
public sealed class GlobalHotkeyRegistration : IDisposable
{
    private readonly int HotkeyId;

    public GlobalHotkeyRegistration(int hotkeyId = 0xED01) => HotkeyId = hotkeyId;
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private HwndSource? _source;
    private IntPtr _handle;
    private Action? _callback;
    public bool IsRegistered { get; private set; }

    public bool TryRegister(Window window, Key key, Action callback)
    {
        Dispose();
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;
        var source = HwndSource.FromHwnd(handle);
        if (source is null) return false;
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (!RegisterHotKey(handle, HotkeyId, ModNoRepeat, (uint)virtualKey)) return false;
        _source = source;
        _handle = handle;
        _callback = callback;
        _source.AddHook(WindowHook);
        IsRegistered = true;
        return true;
    }

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            _callback?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source is not null) _source.RemoveHook(WindowHook);
        if (IsRegistered && _handle != IntPtr.Zero) UnregisterHotKey(_handle, HotkeyId);
        _source = null;
        _handle = IntPtr.Zero;
        _callback = null;
        IsRegistered = false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
