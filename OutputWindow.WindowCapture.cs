using System.Windows;
using System.Windows.Media;
namespace EventDeck;
public partial class OutputWindow
{
    private WindowCaptureLease? _programCaptureLease;
    private WindowCaptureRenderer? _programCaptureRenderer;
    // Public ownership of the outgoing source survives until the animation completes.
    // Fields also allow synchronous cleanup when Output closes during an await.
    private WindowCaptureLease? _transitionCaptureLease;
    private WindowCaptureRenderer? _transitionCaptureRenderer;
    private VideoSession? _transitionVideo;
    private readonly TaskCompletionSource<bool> _outputClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _captureOutputClosed;
    internal string? CaptureStatus => (_programCaptureRenderer?.Error ?? _programCaptureLease?.Session.Status)?.Replace(" · solo CUT", "");

    internal async Task ShowCaptureItemAsync(MediaItem item, WindowCaptureRegistry registry, TransitionKind transition)
    {
        if (_captureOutputClosed) throw new InvalidOperationException("La salida está cerrada.");
        WindowCaptureLease? candidateLease = null;
        WindowCaptureRenderer? candidateRenderer = null;
        try
        {
            candidateLease = registry.Acquire(item.CaptureBinding);
            if (!candidateLease.Session.CanTake) throw new InvalidOperationException(candidateLease.Session.Status);
            candidateRenderer = new WindowCaptureRenderer(candidateLease.Session, Dispatcher);
            if (!candidateRenderer.PresentLatest()) throw new InvalidOperationException("Esperá el primer frame válido en PREVIEW antes de enviar.");
            var candidateLayer = CreateLayer(candidateRenderer.Drawing);
            // Select a live Drawing, never a snapshot of a capture or video.
            DrawingGroup? outgoing = transition == TransitionKind.Cut || CurrentProgramItem is null ? null
                : _programCaptureRenderer is not null ? CreateLayer(_programCaptureRenderer.Drawing)
                : _programSession is not null ? CreateLayer(_programSession.Drawing)
                : _incomingLayer;
            if (!candidateLease.Session.CanTake) throw new InvalidOperationException(candidateLease.Session.Status);

            // No await before commit: validation failure leaves the existing scene untouched.
            _transitionVideo = _programSession;
            _transitionCaptureLease = _programCaptureLease;
            _transitionCaptureRenderer = _programCaptureRenderer;
            if (_transitionVideo is not null) _transitionVideo.StateChanged -= ProgramSession_StateChanged;
            OutputWeb.Visibility = Visibility.Collapsed;
            _programSession = null;
            _programCaptureLease = candidateLease;
            _programCaptureRenderer = candidateRenderer;
            candidateLease = null; candidateRenderer = null;
            CurrentProgramItem = item;
            _incomingLayer = candidateLayer; _outgoingLayer = outgoing;
            if (transition != TransitionKind.Cut) candidateLayer.Opacity = 0;
            RebuildScene();
            try
            {
                ProgramStateChanged?.Invoke(this, EventArgs.Empty);
                if (transition != TransitionKind.Cut)
                {
                    var duration = transition == TransitionKind.Merge ? 850 : 550;
                    if (outgoing is null) await RevealFromBlackAsync(duration);
                    else await AnimateLayersAsync(transition, duration);
                }
                candidateLayer.Opacity = 1;
                _outgoingLayer = null;
                RebuildScene();
            }
            finally { ReleaseTransitionSources(); }
            ProgramStateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { candidateRenderer?.Dispose(); candidateLease?.Dispose(); }
    }
    private void ReleaseTransitionSources()
    {
        _transitionCaptureRenderer?.Dispose(); _transitionCaptureRenderer = null;
        _transitionCaptureLease?.Dispose(); _transitionCaptureLease = null;
        _transitionVideo?.Dispose(); _transitionVideo = null;
    }
    private async Task WaitForAnimationAsync(Task animation)
    {
        await Task.WhenAny(animation, _outputClosed.Task);
        if (_captureOutputClosed) throw new InvalidOperationException("La salida se cerró durante la transición.");
        await animation;
    }
    private void ReleaseProgramCapture()
    {
        _programCaptureRenderer?.Dispose(); _programCaptureRenderer = null;
        _programCaptureLease?.Dispose(); _programCaptureLease = null;
    }
}
