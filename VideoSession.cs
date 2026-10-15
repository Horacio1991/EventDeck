using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace EventDeck;

/// <summary>One WPF media decoder and its shared VideoDrawing surface.</summary>
public sealed class VideoSession : IDisposable
{
    public const double CanvasWidth = 1920;
    public const double CanvasHeight = 1080;

    private bool _disposed;
    public MediaPlayer Player { get; } = new() { ScrubbingEnabled = true, IsMuted = true, Volume = 0 };
    public VideoDrawing Drawing { get; } = new();
    public DrawingImage? PreviewImage { get; private set; }
    public Uri Source { get; }
    public bool Loop { get; set; }
    public bool IsReady { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool IsEnded { get; private set; }
    public bool IsStopped { get; private set; }
    public TimeSpan Duration => Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero;

    public event EventHandler? StateChanged;
    public event EventHandler<ExceptionEventArgs>? MediaFailed;

    public VideoSession(Uri source, bool loop)
    {
        Source = source;
        Loop = loop;
        Player.MediaEnded += Player_MediaEnded;
        Player.MediaFailed += Player_MediaFailed;
    }

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? onOpened = null;
        EventHandler<ExceptionEventArgs>? onFailed = null;
        onOpened = (_, _) => opened.TrySetResult(true);
        onFailed = (_, args) => opened.TrySetException(args.ErrorException);
        Player.MediaOpened += onOpened;
        Player.MediaFailed += onFailed;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var registration = timeout.Token.Register(() => opened.TrySetCanceled(timeout.Token));
        try
        {
            Player.Open(Source);
            try { await opened.Task; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException("El archivo no terminó de abrirse dentro del tiempo de espera."); }
            cancellationToken.ThrowIfCancellationRequested();
            if (!Player.HasVideo || Player.NaturalVideoWidth <= 0 || Player.NaturalVideoHeight <= 0)
                throw new System.IO.InvalidDataException("El archivo no contiene un video compatible.");

            Drawing.Rect = FitRect(Player.NaturalVideoWidth, Player.NaturalVideoHeight);
            Drawing.Player = Player;
            var previewGroup = new DrawingGroup();
            previewGroup.Children.Add(new GeometryDrawing(System.Windows.Media.Brushes.Black, null, new RectangleGeometry(new System.Windows.Rect(0, 0, CanvasWidth, CanvasHeight))));
            previewGroup.Children.Add(Drawing);
            PreviewImage = new DrawingImage(previewGroup);
            IsReady = true;
            Player.Position = TimeSpan.Zero;
        }
        finally
        {
            Player.MediaOpened -= onOpened;
            Player.MediaFailed -= onFailed;
        }
    }

    /// <summary>Advances a muted frame, pauses, rewinds and waits for WPF to render the prepared surface.</summary>
    public async Task PrimeFirstFrameAsync(CancellationToken cancellationToken)
    {
        if (!IsReady) throw new InvalidOperationException("El video todavía no terminó de abrirse.");
        Player.IsMuted = true;
        Player.Volume = 0;
        Player.Position = TimeSpan.Zero;
        Player.Play();
        IsPlaying = true;
        StateChanged?.Invoke(this, EventArgs.Empty);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await WaitForDecodedSampleAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("No se pudo decodificar un cuadro inicial dentro del tiempo de espera.");
        }
        finally
        {
            Player.Pause();
            Player.Position = TimeSpan.Zero;
            IsPlaying = false;
            IsEnded = false;
            IsStopped = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        await WaitForRenderAsync(cancellationToken);
    }

    /// <summary>Starts a prepared video and waits for an advancing sample and a WPF render pass before it can be revealed.</summary>
    public async Task StartForPresentationAsync(CancellationToken cancellationToken)
    {
        if (!IsReady || _disposed) throw new InvalidOperationException("El video todavía no terminó de prepararse.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        Player.Position = TimeSpan.Zero;
        Player.Play();
        IsPlaying = true;
        IsEnded = false;
        IsStopped = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await WaitForDecodedSampleAsync(timeout.Token);
            await WaitForRenderAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Player.Pause();
            IsPlaying = false;
            throw new TimeoutException("El video no produjo un cuadro inicial listo para PROGRAM dentro del tiempo de espera.");
        }
    }

    private Task WaitForDecodedSampleAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Dispatcher.CurrentDispatcher;
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += Check;
        void Check(object? sender, EventArgs e)
        {
            if (Player.Position > TimeSpan.Zero || IsEnded)
            {
                timer.Stop(); timer.Tick -= Check; completion.TrySetResult(true);
            }
        }
        var registration = cancellationToken.Register(() =>
        {
            dispatcher.BeginInvoke(new Action(() => { timer.Stop(); timer.Tick -= Check; }), DispatcherPriority.Send);
            completion.TrySetCanceled(cancellationToken);
        });
        _ = completion.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        timer.Start();
        return completion.Task;
    }

    private static Task WaitForRenderAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Dispatcher.CurrentDispatcher;
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            dispatcher.BeginInvoke(new Action(() => CompositionTarget.Rendering -= handler), DispatcherPriority.Send);
            completion.TrySetResult(true);
        };
        CompositionTarget.Rendering += handler;
        var registration = cancellationToken.Register(() =>
        {
            CompositionTarget.Rendering -= handler;
            completion.TrySetCanceled(cancellationToken);
        });
        _ = completion.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        return completion.Task;
    }

    public void Play()
    {
        if (!IsReady || _disposed) return;
        if (IsEnded || (Duration > TimeSpan.Zero && Player.Position >= Duration))
        {
            Player.Position = TimeSpan.Zero;
            IsEnded = false;
        }
        Player.Play(); IsPlaying = true; IsEnded = false; IsStopped = false; StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        if (!IsReady || _disposed) return;
        Player.Pause(); IsPlaying = false; IsStopped = false; StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        if (!IsReady || _disposed) return;
        Player.Pause(); Player.Position = TimeSpan.Zero; IsPlaying = false; IsEnded = false; IsStopped = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Seek(TimeSpan position)
    {
        if (!IsReady || _disposed) return;
        var max = Duration > TimeSpan.Zero ? Duration : position;
        Player.Position = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, max.Ticks));
        if (Player.Position < Duration) IsEnded = false;
        IsStopped = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Player_MediaEnded(object? sender, EventArgs e)
    {
        if (Loop)
        {
            Player.Position = TimeSpan.Zero;
            Player.Play(); IsPlaying = true; IsEnded = false;
            IsStopped = false;
        }
        else
        {
            IsPlaying = false; IsEnded = true; IsStopped = false;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Player_MediaFailed(object? sender, ExceptionEventArgs e) => MediaFailed?.Invoke(this, e);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; IsReady = false; IsPlaying = false;
        Player.MediaEnded -= Player_MediaEnded; Player.MediaFailed -= Player_MediaFailed;
        Player.Stop(); Player.Close();
    }

    public static Rect FitRect(double sourceWidth, double sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return new Rect(0, 0, CanvasWidth, CanvasHeight);
        var scale = Math.Min(CanvasWidth / sourceWidth, CanvasHeight / sourceHeight);
        var width = sourceWidth * scale; var height = sourceHeight * scale;
        return new Rect((CanvasWidth - width) / 2, (CanvasHeight - height) / 2, width, height);
    }
}
