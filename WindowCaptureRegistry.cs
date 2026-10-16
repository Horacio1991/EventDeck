namespace EventDeck;

internal sealed class WindowCaptureRegistry : IDisposable
{
    private readonly object _gate = new();
    private readonly List<WindowCaptureBinding> _bindings = new();
    private readonly Dictionary<WindowCaptureBinding, Entry> _sessions = new();
    private bool _disposed;
    private HashSet<WindowCaptureBinding> _resourceBindings = new();
    private sealed class Entry(WindowCaptureSession session)
    {
        public WindowCaptureSession Session { get; } = session;
        public int Users;
    }
    public WindowCaptureBinding Bind(WindowChoice window)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var binding = _bindings.FirstOrDefault(b => b.Window.Handle == window.Handle && !b.IsClosed);
            if (binding is not null) return binding;
            binding = new WindowCaptureBinding(window);
            _bindings.Add(binding);
            _resourceBindings.Add(binding);
            return binding;
        }
    }
    public WindowCaptureLease Acquire(WindowCaptureBinding? binding)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (binding is null) throw new InvalidOperationException("FUENTE NO VINCULADA. Agregá una captura de la ventana actual.");
            if (binding.IsClosed) throw new InvalidOperationException("FUENTE CERRADA. Agregá nuevamente la ventana.");
            if (!_sessions.TryGetValue(binding, out var entry))
            {
                if (WindowCatalog.IsIconic(binding.Window.Handle)) throw new InvalidOperationException("Restaurá la fuente antes de iniciar la captura.");
                var session = new WindowCaptureSession();
                try { session.Start(binding); }
                catch { session.Dispose(); throw; }
                entry = new Entry(session);
                _sessions.Add(binding, entry);
            }
            entry.Users++;
            return new WindowCaptureLease(entry.Session, () => Release(binding));
        }
    }
    private void Release(WindowCaptureBinding binding)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(binding, out var entry)) return;
            if (--entry.Users != 0) return;
            // No new session for this source can start until old cleanup finishes.
            entry.Session.Dispose();
            _sessions.Remove(binding);
            PruneBindings();
        }
    }
    public void SetResourceBindings(IEnumerable<WindowCaptureBinding?> bindings)
    {
        lock (_gate)
        {
            _resourceBindings = bindings.OfType<WindowCaptureBinding>().ToHashSet();
            PruneBindings();
        }
    }
    private void PruneBindings()
    {
        foreach (var binding in _bindings.Where(b => !_resourceBindings.Contains(b) && !_sessions.ContainsKey(b)).ToList())
        {
            binding.Dispose();
            _bindings.Remove(binding);
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _sessions.Values) entry.Session.Dispose();
            _sessions.Clear();
            foreach (var binding in _bindings) binding.Dispose();
            _bindings.Clear();
            _resourceBindings.Clear();
        }
    }
}
internal sealed class WindowCaptureLease(WindowCaptureSession session, Action release) : IDisposable
{
    private Action? _release = release;
    public WindowCaptureSession Session { get; } = session;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
