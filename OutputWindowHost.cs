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
    private RecoverySnapshot? _recoverySnapshot;
    private VideoSession.PlaybackState? _lastPlayback;
    private sealed record RecoverySnapshot(MediaItem Item, VideoSession.PlaybackState Playback,
        TimeSpan Duration, bool Black);


    public event EventHandler? ProgramStateChanged;
    public event EventHandler? Closed;
    public event EventHandler? EmergencyClosed;
    public event EventHandler? DisplayAvailabilityChanged;
    public event EventHandler<string>? RecoveryFailed;
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
                var recoveryGeneration = 0;
                var recoveryRunning = false;
                var recoveryFailed = false;
                CancellationTokenSource? recoveryCancellation = null;
                OutputWindow? retiringWindow = null;
                void Retire(OutputWindow retired)
                {
                    retiringWindow = retired;
                    try { retired.Close(); }
                    finally { retiringWindow = null; }
                }
                bool MatchesOriginal(OutputDisplayTarget candidate) => candidate == OutputDisplayTarget.Recover(
                    originalIdentity, OutputDisplayTarget.DeviceForWindow(operatorHandle), OutputDisplayTarget.ReadAll());
                void QueueWindowRecovery(OutputDisplayTarget candidate)
                {
                    if (recoveryRunning || recoveryFailed || _recoverySnapshot is null) return;
                    recoveryRunning = true;
                    var generation = recoveryGeneration;
                    var cancellation = new CancellationTokenSource();
                    recoveryCancellation = cancellation;
                    bool Safe() => !emergencyClosing && IsOpen && !cancellation.IsCancellationRequested &&
                        generation == recoveryGeneration && stableSamples >= 3 && MatchesOriginal(candidate);
                    _outputDispatcher.BeginInvoke(new Action(async () =>
                    {
                        var locked = false;
                        try
                        {
                            await _sceneCommands.WaitAsync(cancellation.Token);
                            locked = true;
                            if (!Safe()) return;
                            // Close releases old decoder, visual, HWND and mirror. This is NOT operator Close.
                            var retired = window;
                            var replacement = new OutputWindow(); // No HWND/decoder yet; construction failure leaves old host intact.
                            ConfigureWindow(replacement, candidate);
                            AttachWindow(replacement);
                            window = replacement;
                            _window = replacement;
                            Retire(retired);
                            var handle = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                            if (!Safe() || !candidate.Place(handle) || !Safe())
                                throw new OperationCanceledException(cancellation.Token);
                            var saved = _recoverySnapshot!;
                            await window.PrepareRecoveredVideoAsync(CloneItem(saved.Item)!, saved.Playback,
                                saved.Black, cancellation.Token);
                            if (!Safe()) throw new OperationCanceledException(cancellation.Token);
                            // Commands during Open update the logical snapshot, never the discarded player.
                            saved = _recoverySnapshot!;
                            window.ProgramPlayer!.RestorePlaybackState(saved.Playback with
                                { IsPlaying = false, IsMuted = true, Volume = 0 });
                            window.SetProgramLoop(saved.Playback.Loop);
                            if (window.IsBlackActive != saved.Black) await window.ToggleBlackAsync();
                            // EnsureHandle creates an HWND without showing it. Place and validate it again.
                            if (!Safe() || !candidate.Place(handle) || !Safe() ||
                                OutputDisplayTarget.DeviceForWindow(handle) != candidate.Device)
                                throw new OperationCanceledException(cancellation.Token);
                            _currentDisplay = candidate;
                            _displaySuspended = false;
                            window.ShowActivated = false;
                            window.Show();
                            // WM_DISPLAYCHANGE during Show has priority over playback recovery.
                            if (!Safe() || _displaySuspended || !TargetAvailable(OutputDisplayTarget.ReadAll()) ||
                                OutputDisplayTarget.DeviceForWindow(handle) != candidate.Device)
                            { SuspendDisplay(); return; }
                            window.ProgramPlayer.RestorePlaybackState(_recoverySnapshot!.Playback);
                            window.SetProgramMuted(_recoverySnapshot.Playback.IsMuted);
                            _recoverySnapshot = null;
                            NotifyDisplayAvailability();
                        }
                        catch (OperationCanceledException)
                        {
                            if (IsOpen && !emergencyClosing) SuspendDisplay();
                        }
                        catch (Exception ex)
                        {
                            if (IsOpen && !emergencyClosing)
                            {
                                SuspendDisplay();
                                recoveryFailed = true; // No unbounded decoder retry loop; a new topology permits retry.
                                _ = _operatorDispatcher.BeginInvoke(new Action(() => RecoveryFailed?.Invoke(this, ex.Message)));
                            }
                        }
                        finally
                        {
                            if (locked) _sceneCommands.Release();
                            if (ReferenceEquals(recoveryCancellation, cancellation)) recoveryCancellation = null;
                            cancellation.Dispose();
                            recoveryRunning = false;
                        }
                    }));
                }
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
                    recoveryGeneration++;
                    recoveryCancellation?.Cancel();
                    recoveryFailed = false;
                    if (_displaySuspended) return;
                    // Use the last published logical state: never ask a possibly broken native player
                    // for its position inside WM_DISPLAYCHANGE. The timer bounds the normal error to 250 ms.
                    if (_recoverySnapshot is null && Snapshot is { IsVideo: true, Item: { } item } snapshot &&
                        _lastPlayback is { } playback)
                        _recoverySnapshot = new(CloneItem(item)!, playback, snapshot.Duration, snapshot.IsBlackActive);
                    _displaySuspended = true;
                    if (_recoverySnapshot is not null && window.ProgramPlayer is { } player)
                    {
                        // Stop the old audio while awaiting reconstruction; independent audio tracks are untouched.
                        try { player.Player.IsMuted = true; player.Pause(); }
                        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                        { _ = _operatorDispatcher.BeginInvoke(new Action(() => RecoveryFailed?.Invoke(this, ex.Message))); }
                    }
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
                    if (candidate is null)
                    {
                        if (recoveryRunning) SuspendDisplay();
                        recoveryCandidate = null; stableSamples = 0; return;
                    }
                    stableSamples = candidate == recoveryCandidate ? stableSamples + 1 : 1;
                    recoveryCandidate = candidate;
                    if (stableSamples < 3) return;
                    // Require a stable, unique external target; validate again immediately before showing.
                    if (candidate != OutputDisplayTarget.Recover(originalIdentity,
                        OutputDisplayTarget.DeviceForWindow(operatorHandle), OutputDisplayTarget.ReadAll()))
                    { stableSamples = 0; return; }
                    if (_recoverySnapshot is not null) { QueueWindowRecovery(candidate); return; }
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
                void AttachWindow(OutputWindow attached)
                {
                    System.Windows.Interop.HwndSource? source = null;
                    var emergencyHotkey = new GlobalHotkeyRegistration(0x5D03);
                    System.Windows.Interop.HwndSourceHook displayHook =
                        (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                        {
                            if (message == 0x007E && !testMode) SuspendDisplay(); // WM_DISPLAYCHANGE
                            return IntPtr.Zero;
                        };
                    attached.SourceInitialized += (_, _) =>
                    {
                        source = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(attached).Handle);
                        source?.AddHook(displayHook);
                        emergencyHotkey.TryRegister(attached, System.Windows.Input.Key.Escape, EmergencyClose);
                    };
                    attached.PreviewKeyDown += (_, e) =>
                    {
                        if (e.Key != System.Windows.Input.Key.Escape) return;
                        e.Handled = true;
                        EmergencyClose();
                    };
                    attached.ProgramStateChanged += (_, _) =>
                    {
                        if (!ReferenceEquals(retiringWindow, attached)) PublishSnapshot(attached);
                    };
                    attached.Closed += (_, _) =>
                    {
                        source?.RemoveHook(displayHook);
                        emergencyHotkey.Dispose();
                        if (ReferenceEquals(retiringWindow, attached)) return;
                        recoveryGeneration++;
                        recoveryCancellation?.Cancel();
                        _statusTimer?.Stop();
                        Volatile.Write(ref _isOpen, 0);
                        _recoverySnapshot = null;
                        PublishSnapshot(attached);
                        _outputDispatcher?.BeginInvokeShutdown(DispatcherPriority.Background);
                        _operatorDispatcher.BeginInvoke(new Action(() => Closed?.Invoke(this, EventArgs.Empty)));
                    };
                }
                void ConfigureWindow(OutputWindow configured, OutputDisplayTarget? target)
                {
                    if (testMode)
                    {
                        configured.WindowStyle = WindowStyle.SingleBorderWindow;
                        configured.ResizeMode = ResizeMode.CanResize;
                        configured.Topmost = false;
                        configured.Width = 900;
                        configured.Height = 520;
                        configured.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                    }
                    else
                    {
                        configured.WindowStyle = WindowStyle.None;
                        configured.ResizeMode = ResizeMode.NoResize;
                        configured.Topmost = true;
                        configured.WindowStartupLocation = WindowStartupLocation.Manual;
                        configured.Left = target?.Left ?? left;
                        configured.Top = target?.Top ?? top;
                        configured.Width = target?.Width ?? width;
                        configured.Height = target?.Height ?? height;
                    }
                }
                ConfigureWindow(window, _currentDisplay);
                AttachWindow(window);
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
            var operation = dispatcher.InvokeAsync(() =>
            {
                if (_recoverySnapshot is not null)
                    throw new InvalidOperationException("SALIDA SUSPENDIDA: esperá la recuperación antes de enviar otro recurso.");
                return itemCopy.Kind == MediaKind.WindowCapture
                    ? _window!.ShowCaptureItemAsync(itemCopy, _captures, transition)
                    : _window!.ShowItemAsync(itemCopy, webSnapshotPath, transition);
            });
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
            var operation = dispatcher.InvokeAsync(() =>
            {
                if (_recoverySnapshot is { } saved)
                {
                    _recoverySnapshot = saved with { Black = !saved.Black };
                    PublishSnapshot(_window!);
                    return Task.CompletedTask;
                }
                return _window!.ToggleBlackAsync();
            });
            await operation.Task.Unwrap();
        }
        finally { _sceneCommands.Release(); }
    }

    public void SetProgramMirrorBounds(IntPtr parentWindow, int x, int y, int width, int height) =>
        Post(window => window.SetProgramMirrorBounds(parentWindow, x, y, width, height));

    public void HideProgramMirror() => Post(window => window.HideProgramMirror());

    private void Transport(Action<OutputWindow> live, Func<VideoSession.PlaybackState, VideoSession.PlaybackState> suspended) =>
        Post(window =>
        {
            if (_recoverySnapshot is { } saved)
            {
                var playback = suspended(saved.Playback);
                saved.Item.Loop = playback.Loop;
                saved.Item.ProgramMuted = playback.IsMuted;
                _recoverySnapshot = saved with { Playback = playback };
                PublishSnapshot(window);
            }
            else live(window);
        });

    public void PlayProgram() => Transport(w => w.PlayProgram(), s => s with
        { Position = s.IsEnded ? TimeSpan.Zero : s.Position, IsPlaying = true, IsStopped = false, IsEnded = false });
    public void PauseProgram() => Transport(w => w.PauseProgram(), s => s with { IsPlaying = false, IsStopped = false });
    public void StopProgram() => Transport(w => w.StopProgram(), s => s with
        { Position = TimeSpan.Zero, IsPlaying = false, IsStopped = true, IsEnded = false });
    public void SeekProgram(TimeSpan position) => Transport(w => w.SeekProgram(position), s => s with
        { Position = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, _recoverySnapshot!.Duration.Ticks)),
            IsEnded = false, IsStopped = false });
    public void SetProgramLoop(bool loop) => Transport(w => w.SetProgramLoop(loop), s => s with { Loop = loop });
    public void SetProgramMuted(bool muted) => Transport(w => w.SetProgramMuted(muted), s => s with { IsMuted = muted });

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
        if (_recoverySnapshot is { } recovery)
        {
            var saved = recovery.Playback;
            var suspended = new OutputProgramSnapshot(CloneItem(recovery.Item), true, saved.IsPlaying,
                saved.IsEnded, saved.IsStopped, saved.Position, recovery.Duration, null, recovery.Black,
                _currentDisplay?.Device, _currentDisplay?.Width ?? 0, _currentDisplay?.Height ?? 0,
                _testMode, true);
            lock (_snapshotLock) _snapshot = suspended;
            _operatorDispatcher.BeginInvoke(new Action(() => ProgramStateChanged?.Invoke(this, EventArgs.Empty)));
            return;
        }
        var session = window.ProgramPlayer;
        _lastPlayback = session is { IsReady: true } ? session.CapturePlaybackState() : null;
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
