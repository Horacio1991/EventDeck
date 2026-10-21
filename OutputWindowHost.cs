using System.Windows;
using System.Windows.Threading;

namespace EventDeck;

/// <summary>Runs the projector window and its master media session on a dedicated STA dispatcher.</summary>
public sealed class OutputWindowHost
{
    private readonly Dispatcher _operatorDispatcher;
    private readonly WindowCaptureRegistry _captures;
    private readonly SemaphoreSlim _sceneCommands = new(1, 1);
    private readonly object _snapshotLock = new();
    private Thread? _thread;
    private Dispatcher? _outputDispatcher;
    private OutputWindow? _window;
    private DispatcherTimer? _statusTimer;
    private OutputProgramSnapshot _snapshot = new(null, false, false, false, false, TimeSpan.Zero, TimeSpan.Zero);
    private int _isOpen;
    private bool _testMode;
    private bool _displaySuspended;
    private OutputDisplayTarget? _currentDisplay;

    public event EventHandler? ProgramStateChanged;
    public event EventHandler? Closed;
    public event EventHandler? EmergencyClosed;
    public event EventHandler? DisplayAvailabilityChanged;
    public bool IsOpen => Volatile.Read(ref _isOpen) != 0;
    public OutputProgramSnapshot Snapshot { get { lock (_snapshotLock) return _snapshot; } }
    public MediaItem? CurrentProgramItem => CloneItem(Snapshot.Item);
    public bool IsProgramVideo => Snapshot.IsVideo;
    public bool IsProgramEnded => Snapshot.IsEnded;

    internal OutputWindowHost(Dispatcher operatorDispatcher, WindowCaptureRegistry captures) { _operatorDispatcher = operatorDispatcher; _captures = captures; }

    public Task OpenAsync(bool testMode, double left, double top, double width, double height, string? displayDevice = null, string? operatorDevice = null)
    {
        if (_thread is not null) throw new InvalidOperationException("La salida ya fue inicializada.");
        _testMode = testMode;
        var operatorHandle = new System.Windows.Interop.WindowInteropHelper(System.Windows.Application.Current.MainWindow).Handle;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            try
            {
                _outputDispatcher = Dispatcher.CurrentDispatcher;
                _currentDisplay = OutputDisplayTarget.ReadAll().FirstOrDefault(x => x.Device == displayDevice);
                var originalIdentity = _currentDisplay?.Identity;
                bool TargetAvailable(IReadOnlyList<OutputDisplayTarget> displays)
                {
                    var currentOperatorDevice = OutputDisplayTarget.DeviceForWindow(operatorHandle);
                    return currentOperatorDevice is not null && _currentDisplay is not null &&
                        displays.Count >= 2 && !_currentDisplay.Primary &&
                        _currentDisplay.Device != currentOperatorDevice && displays.Contains(_currentDisplay);
                }
                if (!testMode && (!TargetAvailable(OutputDisplayTarget.ReadAll()) ||
                    _currentDisplay!.Left != (int)left || _currentDisplay.Top != (int)top ||
                    _currentDisplay.Width != (int)width || _currentDisplay.Height != (int)height))
                    throw new InvalidOperationException("La configuración de pantallas cambió. Actualizá las pantallas y seleccioná nuevamente la salida.");
                var window = new OutputWindow();
                _window = window;
                var emergencyClosing = false;
                void EmergencyClose()
                {
                    if (emergencyClosing) return;
                    emergencyClosing = true;
                    // Remove fullscreen immediately; normal Close owns media cleanup.
                    window.Hide();
                    _outputDispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                    {
                        window.Close();
                        _operatorDispatcher.BeginInvoke(new Action(() => EmergencyClosed?.Invoke(this, EventArgs.Empty)));
                    }));
                }
                OutputDisplayTarget? recoveryCandidate = null;
                var stableSamples = 0;
                void NotifyDisplayAvailability()
                {
                    PublishSnapshot(window);
                    _operatorDispatcher.BeginInvoke(new Action(() => DisplayAvailabilityChanged?.Invoke(this, EventArgs.Empty)));
                }
                void SuspendDisplay()
                {
                    if (emergencyClosing) return;
                    recoveryCandidate = null;
                    stableSamples = 0;
                    window.Hide();
                    if (_displaySuspended) return;
                    _displaySuspended = true;
                    NotifyDisplayAvailability();
                }
                void CheckDisplay()
                {
                    if (testMode || emergencyClosing) return;
                    var displays = OutputDisplayTarget.ReadAll();
                    if (!_displaySuspended)
                    {
                        if (!TargetAvailable(displays)) SuspendDisplay();
                        return;
                    }
                    var candidate = OutputDisplayTarget.Recover(originalIdentity,
                        OutputDisplayTarget.DeviceForWindow(operatorHandle), displays);
                    if (candidate is null) { recoveryCandidate = null; stableSamples = 0; return; }
                    stableSamples = candidate == recoveryCandidate ? stableSamples + 1 : 1;
                    recoveryCandidate = candidate;
                    if (stableSamples < 3) return;
                    // Require a stable, unique external target; validate again immediately before showing.
                    if (candidate != OutputDisplayTarget.Recover(originalIdentity,
                        OutputDisplayTarget.DeviceForWindow(operatorHandle), OutputDisplayTarget.ReadAll()))
                    { stableSamples = 0; return; }
                    var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    if (!candidate.Place(handle) || stableSamples < 3) return;
                    if (candidate != OutputDisplayTarget.Recover(originalIdentity,
                        OutputDisplayTarget.DeviceForWindow(operatorHandle), OutputDisplayTarget.ReadAll()))
                    { stableSamples = 0; return; }
                    _currentDisplay = candidate;
                    _displaySuspended = false;
                    window.ShowActivated = false;
                    window.Show();
                    // A topology message during Show can suspend again. Never override that decision.
                    if (_displaySuspended || !TargetAvailable(OutputDisplayTarget.ReadAll()) ||
                        OutputDisplayTarget.DeviceForWindow(handle) != candidate.Device)
                    { SuspendDisplay(); return; }
                    NotifyDisplayAvailability();
                }
                System.Windows.Interop.HwndSource? source = null;
                var emergencyHotkey = new GlobalHotkeyRegistration(0x5D03);
                System.Windows.Interop.HwndSourceHook displayHook =
                    (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                    {
                        // Hide immediately on topology change; keep the decoder and dispatcher alive.
                        if (message == 0x007E && !testMode) SuspendDisplay(); // WM_DISPLAYCHANGE
                        return IntPtr.Zero;
                    };
                window.SourceInitialized += (_, _) =>
                {
                    source = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(window).Handle);
                    source?.AddHook(displayHook);
                    emergencyHotkey.TryRegister(window, System.Windows.Input.Key.Escape, EmergencyClose);
                };
                window.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key != System.Windows.Input.Key.Escape) return;
                    e.Handled = true;
                    EmergencyClose();
                };
                if (testMode)
                {
                    window.WindowStyle = WindowStyle.SingleBorderWindow;
                    window.ResizeMode = ResizeMode.CanResize;
                    window.Topmost = false;
                    window.Width = 900;
                    window.Height = 520;
                    window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                }
                else
                {
                    window.WindowStyle = WindowStyle.None;
                    window.ResizeMode = ResizeMode.NoResize;
                    window.Topmost = true;
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = left;
                    window.Top = top;
                    window.Width = width;
                    window.Height = height;
                }

                window.ProgramStateChanged += (_, _) => PublishSnapshot(window);
                window.Closed += (_, _) =>
                {
                    source?.RemoveHook(displayHook);
                    emergencyHotkey.Dispose();
                    _statusTimer?.Stop();
                    Volatile.Write(ref _isOpen, 0);
                    PublishSnapshot(window);
                    _outputDispatcher?.BeginInvokeShutdown(DispatcherPriority.Background);
                    _operatorDispatcher.BeginInvoke(new Action(() => Closed?.Invoke(this, EventArgs.Empty)));
                };
                window.Show();
                if (!testMode && !TargetAvailable(OutputDisplayTarget.ReadAll())) SuspendDisplay();
                if (emergencyClosing || _displaySuspended) window.Hide();
                Volatile.Write(ref _isOpen, 1);
                _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _statusTimer.Tick += (_, _) =>
                {
                    // The same timer keeps publishing playback while the public window is hidden.
                    CheckDisplay();
                    PublishSnapshot(window);
                };
                _statusTimer.Start();
                PublishSnapshot(window);
                ready.TrySetResult(true);
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
                Volatile.Write(ref _isOpen, 0);
            }
        }) { IsBackground = true, Name = "EventDeck Output Dispatcher" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        return ready.Task;
    }

    public async Task ShowItemAsync(MediaItem item, string? webSnapshotPath, TransitionKind transition)
    {
        var itemCopy = CloneItem(item)!;
        await _sceneCommands.WaitAsync();
        try
        {
            var dispatcher = GetDispatcher();
            var operation = dispatcher.InvokeAsync(() => itemCopy.Kind == MediaKind.WindowCapture
                ? _window!.ShowCaptureItemAsync(itemCopy, _captures, transition)
                : _window!.ShowItemAsync(itemCopy, webSnapshotPath, transition));
            await operation.Task.Unwrap();
        }
        finally { _sceneCommands.Release(); }
    }

    public async Task ToggleBlackAsync()
    {
        await _sceneCommands.WaitAsync();
        try
        {
            var dispatcher = GetDispatcher();
            var operation = dispatcher.InvokeAsync(() => _window!.ToggleBlackAsync());
            await operation.Task.Unwrap();
        }
        finally { _sceneCommands.Release(); }
    }

    public void SetProgramMirrorBounds(IntPtr parentWindow, int x, int y, int width, int height) =>
        Post(window => window.SetProgramMirrorBounds(parentWindow, x, y, width, height));

    public void HideProgramMirror() => Post(window => window.HideProgramMirror());

    public void PlayProgram() => Post(window => window.PlayProgram());
    public void PauseProgram() => Post(window => window.PauseProgram());
    public void StopProgram() => Post(window => window.StopProgram());
    public void SeekProgram(TimeSpan position) => Post(window => window.SeekProgram(position));
    public void SetProgramLoop(bool loop) => Post(window => window.SetProgramLoop(loop));
    public void SetProgramMuted(bool muted) => Post(window => window.SetProgramMuted(muted));

    public void Close()
    {
        var dispatcher = _outputDispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.Invoke(() =>
        {
            if (_window is { } window) window.Close();
            else dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        });
    }

    private Dispatcher GetDispatcher()
    {
        var dispatcher = _outputDispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || _window is null || !IsOpen)
            throw new InvalidOperationException("La salida pública no está abierta.");
        return dispatcher;
    }

    private void Post(Action<OutputWindow> action)
    {
        var dispatcher = _outputDispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || !IsOpen) return;
        dispatcher.BeginInvoke(new Action(() => { if (IsOpen && _window is { } window) action(window); }));
    }

    private void PublishSnapshot(OutputWindow window)
    {
        var session = window.ProgramPlayer;
        var duration = session?.Duration ?? TimeSpan.Zero;
        var position = session is null ? TimeSpan.Zero : session.IsEnded ? duration : session.Player.Position;
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var display = _testMode && handle != IntPtr.Zero ? System.Windows.Forms.Screen.FromHandle(handle) : null;
        var snapshot = new OutputProgramSnapshot(CloneItem(window.CurrentProgramItem), window.IsProgramVideo,
            session?.IsPlaying == true, session?.IsEnded == true, session?.IsStopped == true, position, duration, window.CaptureStatus, window.IsBlackActive,
            _currentDisplay?.Device ?? display?.DeviceName, _currentDisplay?.Width ?? display?.Bounds.Width ?? 0,
            _currentDisplay?.Height ?? display?.Bounds.Height ?? 0, _testMode, _displaySuspended);
        lock (_snapshotLock) _snapshot = snapshot;
        _operatorDispatcher.BeginInvoke(new Action(() => ProgramStateChanged?.Invoke(this, EventArgs.Empty)));
    }

    private static MediaItem? CloneItem(MediaItem? item) => item is null ? null : new MediaItem
    {
        Name = item.Name,
        Source = item.Source,
        Kind = item.Kind,
        ProgramMuted = item.ProgramMuted,
        Loop = item.Loop,
        Capture = item.Capture,
        CaptureBinding = item.CaptureBinding
    };
}

public sealed record OutputProgramSnapshot(MediaItem? Item, bool IsVideo, bool IsPlaying, bool IsEnded,
    bool IsStopped, TimeSpan Position, TimeSpan Duration, string? CaptureStatus = null, bool IsBlackActive = false,
    string? DisplayDevice = null, int DisplayWidth = 0, int DisplayHeight = 0, bool IsTestMode = false, bool IsDisplaySuspended = false);
