using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace EventDeck;

internal sealed class WindowCaptureRenderer : IDisposable
{
    private readonly WindowCaptureSession _session;
    private readonly Dispatcher _dispatcher;
    private WriteableBitmap? _bitmap;
    private int _scheduled, _disposed;
    private long _lastSequence;
    public ImageDrawing Drawing { get; } = new();
    public DrawingImage Image { get; }
    public string? Error { get; private set; }
    public bool HasFrame => _bitmap is not null;
    public WindowCaptureRenderer(WindowCaptureSession session, Dispatcher dispatcher)
    {
        dispatcher.VerifyAccess();
        _session = session; _dispatcher = dispatcher;
        Image = new DrawingImage(Drawing);
        _session.FrameAvailable += Signal;
        Signal();
    }
    private void Signal()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _scheduled, 1) != 0) return;
        if (_dispatcher.HasShutdownStarted) { Interlocked.Exchange(ref _scheduled, 0); return; }
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Interlocked.Exchange(ref _scheduled, 0);
                if (_disposed != 0 || Error is not null) return;
                try { PresentLatest(); }
                catch (Exception ex) { Error = "No se pudo presentar la captura: " + ex.Message; }
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref _scheduled, 0); }
    }
    public bool PresentLatest()
    {
        _dispatcher.VerifyAccess();
        using var pixels = _session.AcquireLatest();
        if (pixels is null) return false;
        if (_lastSequence == pixels.Sequence) return HasFrame;
        if (_bitmap is null || _bitmap.PixelWidth != pixels.Width || _bitmap.PixelHeight != pixels.Height)
        {
            var replacement = new WriteableBitmap(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null);
            replacement.WritePixels(new Int32Rect(0, 0, pixels.Width, pixels.Height), pixels.Bytes, pixels.Stride, 0);
            _bitmap = replacement;
            Drawing.Rect = VideoSession.FitRect(pixels.Width, pixels.Height);
            Drawing.ImageSource = replacement;
        }
        else _bitmap.WritePixels(new Int32Rect(0, 0, pixels.Width, pixels.Height), pixels.Bytes, pixels.Stride, 0);
        _lastSequence = pixels.Sequence;
        return true;
    }
    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _session.FrameAvailable -= Signal;
    }
}
