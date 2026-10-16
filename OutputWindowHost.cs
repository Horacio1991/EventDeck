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

    public event EventHandler? ProgramStateChanged;
    public event EventHandler? Closed;
    public bool IsOpen => Volatile.Read(ref _isOpen) != 0;
    public OutputProgramSnapshot Snapshot { get { lock (_snapshotLock) return _snapshot; } }
    public MediaItem? CurrentProgramItem => CloneItem(Snapshot.Item);
    public bool IsProgramVideo => Snapshot.IsVideo;
    public bool IsProgramEnded => Snapshot.IsEnded;

    internal OutputWindowHost(Dispatcher operatorDispatcher, WindowCaptureRegistry captures) { _operatorDispatcher = operatorDispatcher; _captures = captures; }

    public Task OpenAsync(bool testMode, double left, double top, double width, double height)
    {
        if (_thread is not null) throw new InvalidOperationException("La salida ya fue inicializada.");
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            try
            {
                _outputDispatcher = Dispatcher.CurrentDispatcher;
                var window = new OutputWindow();
                _window = window;
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
                    _statusTimer?.Stop();
                    Volatile.Write(ref _isOpen, 0);
                    PublishSnapshot(window);
                    _outputDispatcher?.BeginInvokeShutdown(DispatcherPriority.Background);
                    _operatorDispatcher.BeginInvoke(new Action(() => Closed?.Invoke(this, EventArgs.Empty)));
                };
                window.Show();
                Volatile.Write(ref _isOpen, 1);
                _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _statusTimer.Tick += (_, _) => PublishSnapshot(window);
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

    public async Task BlackAsync(bool fade)
    {
        await _sceneCommands.WaitAsync();
        try
        {
            var dispatcher = GetDispatcher();
            var operation = dispatcher.InvokeAsync(() => _window!.BlackAsync(fade));
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
            if (_window is { IsVisible: true } window) window.Close();
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
        dispatcher.BeginInvoke(new Action(() => { if (_window is { IsVisible: true } window) action(window); }));
    }

    private void PublishSnapshot(OutputWindow window)
    {
        var session = window.ProgramPlayer;
        var duration = session?.Duration ?? TimeSpan.Zero;
        var position = session is null ? TimeSpan.Zero : session.IsEnded ? duration : session.Player.Position;
        var snapshot = new OutputProgramSnapshot(CloneItem(window.CurrentProgramItem), window.IsProgramVideo,
            session?.IsPlaying == true, session?.IsEnded == true, session?.IsStopped == true, position, duration, window.CaptureStatus);
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
    bool IsStopped, TimeSpan Position, TimeSpan Duration, string? CaptureStatus = null);
