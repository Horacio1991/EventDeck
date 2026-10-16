using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace EventDeck;
internal sealed class WindowCaptureSession : IDisposable
{
    private readonly object _captureGate = new(), _frameGate = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private WindowCaptureBinding? _binding;
    private System.Threading.Timer? _healthTimer;
    private long _sequence;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _staging;
    private SizeInt32 _poolSize;
    private CaptureFrameBuffer? _latest;
    private int _stopping, _closed;
    private readonly object _disposeGate = new();
    private long _nextPublication, _lastReceived;
    private string? _error;
    public event Action? FrameAvailable;
    public long LastReceived => Interlocked.Read(ref _lastReceived);
    public bool SourceClosed => Volatile.Read(ref _closed) != 0;
    public string? Error => Volatile.Read(ref _error);

    public string Status => SourceClosed ? "FUENTE CERRADA" : Error is not null ? "ERROR: " + Error
        : _binding is null ? "FUENTE NO VINCULADA"
        : WindowCatalog.IsIconic(_binding.Window.Handle) ? "FUENTE MINIMIZADA · último frame disponible"
        : WindowCatalog.IsHidden(_binding.Window.Handle) ? "FUENTE OCULTA · último frame disponible"
        : LastReceived == 0 ? "ESPERANDO PRIMER FRAME"
        : Stopwatch.GetElapsedTime(LastReceived).TotalSeconds > 3 ? "SIN FRAMES RECIENTES · puede ser contenido estático" : "EN VIVO · solo CUT";
    public bool CanTake => !SourceClosed && Error is null && _binding is { IsClosed: false }
        && !WindowCatalog.IsIconic(_binding.Window.Handle) && !WindowCatalog.IsHidden(_binding.Window.Handle);
    public void Start(WindowCaptureBinding binding)
    {
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Windows Graphics Capture no está disponible en este equipo/sesión.");
            _binding = binding;
            if (binding.IsClosed) throw new InvalidOperationException("FUENTE CERRADA. Agregá nuevamente la ventana.");
            _poolSize = binding.Item.Size;
            if ((long)_poolSize.Width * _poolSize.Height > 3840L * 2160) throw new NotSupportedException("Captura limitada a 8,3 megapíxeles (4K). Reducí manualmente la ventana fuente.");
            if (_poolSize.Width < 1 || _poolSize.Height < 1) throw new InvalidOperationException("La ventana no tiene un tamaño capturable. Restaurala y volvé a iniciar.");
            _device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            _context = _device.ImmediateContext;
            _winrtDevice = WindowCaptureInterop.CreateWinRtDevice(_device);
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
            _session = _pool.CreateCaptureSession(binding.Item);
            _session.IsCursorCaptureEnabled = false;
            _pool.FrameArrived += OnFrame;
            _session.StartCapture();
            _healthTimer = new System.Threading.Timer(_ => CheckHealth(), null, 500, 500);
        }
        catch { Dispose(); throw; }
    }
    private void CheckHealth()
    {
        try
        {
            if (_binding?.IsClosed == true) Interlocked.Exchange(ref _closed, 1);
            if (SourceClosed || Error is not null) StopAcquisition();
        }
        catch (Exception ex) { Volatile.Write(ref _error, ex.Message); }
    }
    private void OnFrame(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_captureGate)
        {
            if (_stopping != 0 || SourceClosed || Error is not null) return;
            try
            {
                SizeInt32? resize = null;
                // Drain a bounded number; consumers independently acquire the latest immutable frame.
                for (var i = 0; i < 2; i++)
                {
                    using var frame = sender.TryGetNextFrame();
                    if (frame is null) break;
                    var now = Stopwatch.GetTimestamp();
                    Interlocked.Exchange(ref _lastReceived, now);
                    var size = frame.ContentSize;
                    if ((long)size.Width * size.Height > 3840L * 2160) throw new NotSupportedException("La fuente supera el límite de captura de 8,3 megapíxeles. Reducí su tamaño y reiniciá la captura.");
                    if (size.Width < 1 || size.Height < 1) { continue; }
                    if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
                    {
                        resize = size; break;
                    }
                    if (_nextPublication != 0 && now < _nextPublication)
                    { continue; }
                    using var texture = WindowCaptureInterop.GetTexture(frame.Surface);
                    var desc = texture.Description;
                    if (size.Width > desc.Width || size.Height > desc.Height) { continue; }
                    if (_staging is null || _staging.Description.Width != desc.Width || _staging.Description.Height != desc.Height)
                    {
                        _staging?.Dispose();
                        desc.Usage = ResourceUsage.Staging; desc.BindFlags = BindFlags.None;
                        desc.CPUAccessFlags = CpuAccessFlags.Read; desc.MiscFlags = ResourceOptionFlags.None;
                        _staging = _device!.CreateTexture2D(desc);
                    }
                    _context!.CopyResource(_staging, texture);
                    var map = _context.Map(_staging, 0, MapMode.Read, MapFlags.None);
                    CaptureFrameBuffer? pixels = null;
                    try
                    {
                        pixels = new CaptureFrameBuffer(size.Width, size.Height, ++_sequence);
                        for (var row = 0; row < size.Height; row++)
                            Marshal.Copy(IntPtr.Add(map.DataPointer, checked(row * (int)map.RowPitch)), pixels.Bytes, row * pixels.Stride, pixels.Stride);
                    }
                    catch { pixels?.Dispose(); throw; }
                    finally { _context.Unmap(_staging, 0); }
                    _nextPublication = _nextPublication == 0 || now - _nextPublication > Stopwatch.Frequency / 30
                        ? now + Stopwatch.Frequency / 30 : _nextPublication + Stopwatch.Frequency / 30;
                    lock (_frameGate)
                    {
                        _latest?.Dispose();
                        _latest = pixels;
                    }
                    FrameAvailable?.Invoke();
                }
                // All frames were disposed before resizing the pool.
                if (resize is { } newSize)
                {
                    _staging?.Dispose(); _staging = null;
                    sender.Recreate(_winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, newSize);
                    _poolSize = newSize;
                    _nextPublication = 0;
                }
            }
            catch (Exception ex) { Volatile.Write(ref _error, $"{ex.GetType().Name}: {ex.Message} (0x{ex.HResult:X8})"); }
        }
    }
    public CaptureFrameBuffer? AcquireLatest()
    {
        lock (_frameGate) { return _latest?.Retain(); }
    }
    private void Release(Action action)
    {
        try { action(); }
        catch (Exception ex) { Interlocked.CompareExchange(ref _error, "Cleanup: " + ex.Message, null); }
    }
    private void StopAcquisition()
    {
        lock (_disposeGate)
        {
            if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
            _healthTimer?.Dispose();
            if (_pool is not null) Release(() => _pool.FrameArrived -= OnFrame);
            // Never close WGC from its callback, or while holding the capture lock.
            lock (_captureGate) { }
            Release(() => _session?.Dispose()); _session = null;
            Release(() => _pool?.Dispose()); _pool = null;
            lock (_captureGate)
            {
                Release(() => _staging?.Dispose()); _staging = null;
                Release(() => _winrtDevice?.Dispose()); _winrtDevice = null;
                Release(() => _context?.Dispose()); _context = null;
                Release(() => _device?.Dispose()); _device = null;
            }
        }
    }
    public void Dispose()
    {
        StopAcquisition();
        FrameAvailable = null;
        lock (_frameGate) { _latest?.Dispose(); _latest = null; }
    }
}
