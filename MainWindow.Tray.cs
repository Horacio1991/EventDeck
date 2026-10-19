using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace EventDeck;

public partial class MainWindow
{
    private readonly GlobalHotkeyRegistration _visibilityHotkey = new(0x5D02);
    private HwndSource? _trayWindowSource;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;
    private WindowState _lastVisibleWindowState = WindowState.Normal;
    private bool _changingTrayVisibility;
    private bool _isClosing;

    private void InitializeTray()
    {
        _lastVisibleWindowState = WindowState == WindowState.Maximized
            ? WindowState.Maximized : WindowState.Normal;
        _trayMenu = new System.Windows.Forms.ContextMenuStrip();
        _trayMenu.Items.Add("Abrir EventDeck", null, (_, _) => QueueTrayAction(RestoreFromTray));
        _trayMenu.Items.Add("Salir", null, (_, _) => QueueTrayAction(Close));
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "EventDeck · F9 mostrar/ocultar",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)!,
            ContextMenuStrip = _trayMenu,
            Visible = false
        };
        _trayIcon.DoubleClick += (_, _) => QueueTrayAction(RestoreFromTray);
        _trayWindowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _trayWindowSource?.AddHook(TrayWindowHook);
        if (!_visibilityHotkey.TryRegister(this, Key.F9, ToggleTrayVisibility))
            SetStatus("F9 está ocupada por otra aplicación. Para volver, usá el icono de EventDeck en la bandeja.");
    }

    private void QueueTrayAction(Action action)
    {
        if (_isClosing || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() => { if (!_isClosing) action(); }));
    }

    private IntPtr TrayWindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmSysCommand = 0x0112;
        const long scMinimize = 0xF020;
        if (!_isClosing && message == wmSysCommand && (wParam.ToInt64() & 0xFFF0) == scMinimize)
        {
            // Consume the native request: DefWindowProc must never minimize this window.
            handled = true;
            HideToTray();
        }
        return IntPtr.Zero;
    }

    private void ToggleTrayVisibility()
    {
        if (_isClosing || _changingTrayVisibility || !IsEnabled) return;
        if (IsVisible) HideToTray();
        else RestoreFromTray();
    }

    private void HideToTray()
    {
        if (_isClosing || _changingTrayVisibility || _trayIcon is null) return;
        _changingTrayVisibility = true;
        try
        {
            if (WindowState != WindowState.Minimized) _lastVisibleWindowState = WindowState;
            _trayIcon.Visible = true;
            // Fallback for programmatic/native paths that bypass SC_MINIMIZE.
            // Reset synchronously, guarded against the nested StateChanged event.
            if (WindowState == WindowState.Minimized) WindowState = _lastVisibleWindowState;
            Hide();
        }
        finally { _changingTrayVisibility = false; }
    }

    private void RestoreFromTray()
    {
        if (_isClosing || _changingTrayVisibility) return;
        _changingTrayVisibility = true;
        try
        {
            WindowState = _lastVisibleWindowState;
            Show();
            Activate();
            if (_trayIcon is not null) _trayIcon.Visible = false;
        }
        finally { _changingTrayVisibility = false; }
        // Only the operator monitor's geometry is refreshed; no transport commands.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (_isClosing || !IsVisible) return;
            UpdateProgramMonitorSurface();
            RefreshProgramControls();
        }));
    }

    private void DisposeTray()
    {
        _isClosing = true;
        _visibilityHotkey.Dispose();
        _trayWindowSource?.RemoveHook(TrayWindowHook);
        _trayWindowSource = null;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _trayMenu?.Dispose();
        _trayMenu = null;
    }
}
