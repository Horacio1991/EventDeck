using Windows.Graphics.Capture;
namespace EventDeck;

// Runtime identity only. Holding the original item observes Closed even while no
// capture consumers exist. A title, PID or recycled HWND never rebinds a resource.
internal sealed class WindowCaptureBinding : IDisposable
{
    public WindowChoice Window { get; }
    public GraphicsCaptureItem Item { get; }
    private int _closed;
    public bool IsClosed => Volatile.Read(ref _closed) != 0 || !WindowCatalog.Exists(Window);
    public WindowCaptureBinding(WindowChoice window)
    {
        Window = window;
        Item = WindowCaptureInterop.CreateItem(window.Handle);
        Item.Closed += OnClosed;
    }
    private void OnClosed(GraphicsCaptureItem sender, object args) => Interlocked.Exchange(ref _closed, 1);
    public void Dispose()
    {
        Interlocked.Exchange(ref _closed, 1);
        Item.Closed -= OnClosed;
    }
}
