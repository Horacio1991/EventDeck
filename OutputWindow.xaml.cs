using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace EventDeck;

public enum TransitionKind { Cut, Fade, Merge }

public partial class OutputWindow : Window
{
    private readonly DrawingGroup _scene = new();
    private readonly DrawingImage _sharedImage;
    private DrawingGroup? _outgoingLayer;
    private DrawingGroup? _incomingLayer;
    private VideoSession? _programSession;
    private HwndSource? _programMirrorSource;
    private IntPtr _programMirrorParent;
    private bool _webReady;

    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpHideWindow = 0x0080;

    public MediaItem? CurrentProgramItem { get; private set; }
    public VideoSession? ProgramPlayer => _programSession;
    public bool IsProgramVideo => CurrentProgramItem?.Kind == MediaKind.Video && _programSession is { IsReady: true };
    public bool IsProgramPlaying => _programSession?.IsPlaying == true;
    public bool IsProgramEnded => _programSession?.IsEnded == true;
    public DrawingImage SharedImage => _sharedImage;
    public event EventHandler? ProgramStateChanged;

    public OutputWindow()
    {
        InitializeComponent();
        _scene.Children.Add(CreateBlackBackground());
        _sharedImage = new DrawingImage(_scene);
        OutputSurface.Source = _sharedImage;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try { await OutputWeb.EnsureCoreWebView2Async(); _webReady = true; }
        catch { /* El contenido local sigue disponible sin el runtime de WebView2. */ }
    }

    public async Task ShowItemAsync(MediaItem item, string? webSnapshotPath, TransitionKind transition)
    {
        if (item.Kind == MediaKind.Video)
        {
            var preparedVideo = new VideoSession(new Uri(item.Source), item.Loop);
            try
            {
                await preparedVideo.OpenAsync(CancellationToken.None);
                await ShowVideoItemAsync(item, preparedVideo, transition);
            }
            catch
            {
                preparedVideo.Dispose();
                throw;
            }
            return;
        }

        var outgoing = transition == TransitionKind.Cut ? null : CaptureCurrentFrame();
        OutputWeb.Visibility = Visibility.Collapsed;
        var previous = _programSession;
        _programSession = null;
        if (previous is not null) { previous.StateChanged -= ProgramSession_StateChanged; previous.Dispose(); }

        Drawing content;
        switch (item.Kind)
        {
            case MediaKind.Image:
                content = CreateImageDrawing(item.Source); break;
            case MediaKind.Web:
                if (!string.IsNullOrWhiteSpace(webSnapshotPath) && File.Exists(webSnapshotPath)) content = CreateImageDrawing(webSnapshotPath);
                else
                {
                    content = CreateBlackBackground();
                    if (_webReady) { OutputWeb.Source = new Uri(item.Source); OutputWeb.Visibility = Visibility.Visible; }
                }
                break;
            default:
                content = CreateBlackBackground(); break;
        }

        CurrentProgramItem = item;
        _incomingLayer = CreateLayer(content);
        _outgoingLayer = outgoing is null ? null : CreateLayer(outgoing);
        RebuildScene();
        ProgramStateChanged?.Invoke(this, EventArgs.Empty);

        if (transition == TransitionKind.Cut)
        {
            _incomingLayer.Opacity = 1;
            _programSession?.Play();
            return;
        }

        if (_outgoingLayer is null)
        {
            var duration = transition == TransitionKind.Merge ? 850 : 550;
            await RevealFromBlackAsync(duration);
        }
        else
        {
            var duration = transition == TransitionKind.Merge ? 850 : 550;
            await AnimateLayersAsync(transition, duration);
        }
        _incomingLayer.Opacity = 1;
        _outgoingLayer = null;
        RebuildScene();
        _programSession?.Play();
        ProgramStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ShowVideoItemAsync(MediaItem item, VideoSession incomingSession, TransitionKind transition)
    {
        var previousSession = _programSession;
        var previousLayer = _incomingLayer;
        DrawingGroup? outgoingLayer;
        if (previousSession is not null)
            outgoingLayer = CreateLayer(previousSession.Drawing);
        else
        {
            var currentFrame = CaptureCurrentFrame();
            outgoingLayer = currentFrame is null ? null : CreateLayer(currentFrame);
        }

        incomingSession.Loop = item.Loop;
        incomingSession.Player.IsMuted = true;
        incomingSession.Player.Volume = 1;
        incomingSession.StateChanged += ProgramSession_StateChanged;

        var incomingLayer = CreateLayer(incomingSession.Drawing);
        // Keep a nearly invisible live target attached while the output-owned decoder primes.
        // This avoids exposing a clock-advanced frame before the media surface has rendered it.
        incomingLayer.Opacity = 0.001;
        _outgoingLayer = outgoingLayer;
        _incomingLayer = incomingLayer;
        RebuildScene();

        try
        {
            // Prime on the real Output surface before starting the transition. The preview session
            // belongs to MainWindow's dispatcher and cannot safely be transferred to this dispatcher.
            await incomingSession.PrimeFirstFrameAsync(CancellationToken.None);
            // Priming mutes and silences the decoder to avoid leaking audio before the merge.
            // Restore the master output level before starting presentation; ProgramMuted remains
            // the separate gate that decides whether the public output is audible.
            incomingSession.Player.Volume = 1;
            await incomingSession.StartForPresentationAsync(CancellationToken.None);
        }
        catch
        {
            incomingSession.StateChanged -= ProgramSession_StateChanged;
            _outgoingLayer = null;
            _incomingLayer = previousLayer;
            RebuildScene();
            throw;
        }

        OutputWeb.Visibility = Visibility.Collapsed;
        _programSession = incomingSession;
        CurrentProgramItem = item;
        if (previousSession is not null)
        {
            previousSession.StateChanged -= ProgramSession_StateChanged;
        }
        ProgramStateChanged?.Invoke(this, EventArgs.Empty);

        if (transition == TransitionKind.Cut)
        {
            incomingLayer.Opacity = 1;
            _outgoingLayer = null;
            RebuildScene();
        }
        else if (outgoingLayer is null)
        {
            var duration = transition == TransitionKind.Merge ? 850 : 550;
            await RevealFromBlackAsync(duration);
            incomingLayer.Opacity = 1;
            _outgoingLayer = null;
            RebuildScene();
        }
        else
        {
            var duration = transition == TransitionKind.Merge ? 850 : 550;
            await AnimateLayersAsync(transition, duration);
            incomingLayer.Opacity = 1;
            _outgoingLayer = null;
            RebuildScene();
        }

        incomingSession.Player.IsMuted = item.ProgramMuted;

        if (previousSession is not null && !ReferenceEquals(previousSession, incomingSession))
            previousSession.Dispose();
        ProgramStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private DrawingGroup CreateLayer(Drawing drawing)
    {
        var group = new DrawingGroup { Opacity = 1, ClipGeometry = new RectangleGeometry(new Rect(0, 0, VideoSession.CanvasWidth, VideoSession.CanvasHeight)) };
        group.Children.Add(drawing);
        return group;
    }

    private void RebuildScene()
    {
        _scene.Children.Clear();
        _scene.Children.Add(CreateBlackBackground());
        if (_outgoingLayer is not null) _scene.Children.Add(_outgoingLayer);
        if (_incomingLayer is not null) _scene.Children.Add(_incomingLayer);
    }

    private static Drawing CreateBlackBackground() => new GeometryDrawing(System.Windows.Media.Brushes.Black, null, new RectangleGeometry(new System.Windows.Rect(0, 0, VideoSession.CanvasWidth, VideoSession.CanvasHeight)));

    private static ImageDrawing CreateImageDrawing(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze();
        return new ImageDrawing(bitmap, VideoSession.FitRect(bitmap.PixelWidth, bitmap.PixelHeight));
    }

    private ImageDrawing? CaptureCurrentFrame()
    {
        try
        {
            OutputSurface.UpdateLayout();
            var width = (int)Math.Ceiling(OutputSurface.ActualWidth);
            var height = (int)Math.Ceiling(OutputSurface.ActualHeight);
            if (width < 2 || height < 2) return null;
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(OutputSurface); bitmap.Freeze();
            return new ImageDrawing(bitmap, VideoSession.FitRect(bitmap.PixelWidth, bitmap.PixelHeight));
        }
        catch { return null; }
    }

    private async Task AnimateLayersAsync(TransitionKind transition, int milliseconds)
    {
        var outgoing = _outgoingLayer!; var incoming = _incomingLayer!;
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        incoming.Opacity = 0;
        IEasingFunction easing = transition == TransitionKind.Merge
            ? new SineEase { EasingMode = EasingMode.EaseInOut }
            : new CubicEase { EasingMode = EasingMode.EaseInOut };
        var complete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Animate(outgoing, DrawingGroup.OpacityProperty, 1, 0, duration, easing: easing);
        Animate(incoming, DrawingGroup.OpacityProperty, 0, 1, duration, complete, easing);
        await complete.Task;
    }

    private Task RevealFromBlackAsync(int milliseconds)
    {
        var incoming = _incomingLayer!;
        incoming.Opacity = 0;
        var complete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Animate(incoming, DrawingGroup.OpacityProperty, 0, 1, TimeSpan.FromMilliseconds(milliseconds), complete);
        return complete.Task;
    }

    private static void Animate(Animatable target, DependencyProperty property, double from, double to, TimeSpan duration, TaskCompletionSource<bool>? complete = null, IEasingFunction? easing = null)
    {
        var animation = new DoubleAnimation(from, to, duration) { EasingFunction = easing ?? new CubicEase { EasingMode = EasingMode.EaseInOut } };
        if (complete is not null) animation.Completed += (_, _) => complete.TrySetResult(true);
        target.BeginAnimation(property, animation);
    }

    private void ProgramSession_StateChanged(object? sender, EventArgs e) => ProgramStateChanged?.Invoke(this, EventArgs.Empty);

    public void PlayProgram() => _programSession?.Play();
    public void PauseProgram() => _programSession?.Pause();
    public void StopProgram() => _programSession?.Stop();
    public void SeekProgram(TimeSpan position) => _programSession?.Seek(position);
    public void SetProgramLoop(bool loop)
    {
        if (_programSession is not null) _programSession.Loop = loop;
        if (CurrentProgramItem is not null) CurrentProgramItem.Loop = loop;
    }
    public void SetProgramMuted(bool muted)
    {
        if (_programSession is not null) _programSession.Player.IsMuted = muted;
        if (CurrentProgramItem is not null) CurrentProgramItem.ProgramMuted = muted;
    }

    public void SetProgramMirrorBounds(IntPtr parentWindow, int x, int y, int width, int height)
    {
        if (parentWindow == IntPtr.Zero || width < 2 || height < 2) return;
        if (_programMirrorSource is null || _programMirrorParent != parentWindow)
        {
            DisposeProgramMirror();
            var parameters = new HwndSourceParameters("EventDeck Program Mirror")
            {
                ParentWindow = parentWindow,
                WindowStyle = WsChild | WsVisible,
                ExtendedWindowStyle = WsExTransparent | WsExNoActivate,
                PositionX = x,
                PositionY = y,
                Width = width,
                Height = height
            };
            _programMirrorSource = new HwndSource(parameters);
            var mirror = new Grid { Background = System.Windows.Media.Brushes.Black, ClipToBounds = true, IsHitTestVisible = false };
            mirror.Children.Add(new System.Windows.Controls.Image
            {
                Source = _sharedImage,
                Stretch = Stretch.Uniform,
                IsHitTestVisible = false,
                Focusable = false
            });
            _programMirrorSource.RootVisual = mirror;
            _programMirrorParent = parentWindow;
        }
        SetWindowPos(_programMirrorSource.Handle, IntPtr.Zero, x, y, width, height,
            SwpNoZOrder | SwpNoActivate | SwpShowWindow);
    }

    public void HideProgramMirror()
    {
        if (_programMirrorSource is { } source)
            SetWindowPos(source.Handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoZOrder | SwpNoActivate | SwpHideWindow);
    }

    private void DisposeProgramMirror()
    {
        if (_programMirrorSource is null) return;
        _programMirrorSource.RootVisual = null;
        _programMirrorSource.Dispose();
        _programMirrorSource = null;
        _programMirrorParent = IntPtr.Zero;
    }

    public Task BlackAsync(bool fade)
    {
        var outgoing = fade ? CaptureCurrentFrame() : null;
        OutputWeb.Visibility = Visibility.Collapsed;
        if (_programSession is not null) { _programSession.StateChanged -= ProgramSession_StateChanged; _programSession.Dispose(); _programSession = null; }
        CurrentProgramItem = null;
        _outgoingLayer = outgoing is null ? null : CreateLayer(outgoing);
        var black = CreateLayer(CreateBlackBackground()); black.Opacity = 1; _incomingLayer = black; RebuildScene();
        ProgramStateChanged?.Invoke(this, EventArgs.Empty);
        if (!fade || _outgoingLayer is null) { _outgoingLayer = null; RebuildScene(); return Task.CompletedTask; }
        var complete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Animate(_outgoingLayer, DrawingGroup.OpacityProperty, 1, 0, TimeSpan.FromMilliseconds(280), complete);
        _ = complete.Task.ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => { _outgoingLayer = null; RebuildScene(); })), TaskScheduler.Default);
        return complete.Task;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        DisposeProgramMirror();
        if (_programSession is not null) { _programSession.StateChanged -= ProgramSession_StateChanged; _programSession.Dispose(); _programSession = null; }
        OutputWeb.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
