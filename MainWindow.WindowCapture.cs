using System.Windows;
namespace EventDeck;
public partial class MainWindow
{
    private readonly WindowCaptureRegistry _captures = new();
    private WindowCaptureLease? _previewCaptureLease;
    private WindowCaptureRenderer? _previewCaptureRenderer;
    private bool _takeInProgress;

    private void AddWindowCapture_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new WindowPickerDialog { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedWindow is not { } source) return;
            var binding = _captures.Bind(source);
            var descriptor = new WindowCaptureDescriptor { Title = source.Title, Application = source.Application };
            var item = new MediaItem
            {
                Name = source.Application + " — " + source.Title, Kind = MediaKind.WindowCapture,
                Source = "capture:" + descriptor.ResourceId.ToString("N"),
                Capture = descriptor, CaptureBinding = binding
            };
            _items.Add(item); LibraryList.SelectedItem = item; UpdateCount();
        }
        catch (Exception ex) { SetStatus("No se pudo agregar la captura: " + ex.Message); }
    }
    private void PrepareWindowCapture(MediaItem item)
    {
        CancelPreviewLoad(); _previewPreparationTask = Task.CompletedTask;
        try
        {
            _previewCaptureLease = _captures.Acquire(item.CaptureBinding);
            _previewCaptureRenderer = new WindowCaptureRenderer(_previewCaptureLease.Session, Dispatcher);
            PreviewCapture.Source = _previewCaptureRenderer.Image;
            PreviewCapture.Visibility = Visibility.Visible;
            _previewCaptureRenderer.PresentLatest();
            RefreshCaptureControls();
        }
        catch
        {
            ReleasePreviewCapture();
            throw;
        }
    }
    private void ReleasePreviewCapture()
    {
        _previewCaptureRenderer?.Dispose(); _previewCaptureRenderer = null;
        _previewCaptureLease?.Dispose(); _previewCaptureLease = null;
        if (PreviewCapture is not null) { PreviewCapture.Source = null; PreviewCapture.Visibility = Visibility.Collapsed; }
        if (CaptureState is not null) CaptureState.Visibility = Visibility.Collapsed;
    }
    private void RefreshCaptureControls()
    {
        var selectedCapture = _selected?.Kind == MediaKind.WindowCapture;
        if (CaptureState is null) return;
        CaptureState.Visibility = selectedCapture ? Visibility.Visible : Visibility.Collapsed;
        if (selectedCapture)
            CaptureState.Text = _previewCaptureRenderer?.Error ?? _previewCaptureLease?.Session.Status.Replace(" · solo CUT", "")
                ?? (_selected?.CaptureBinding is { IsClosed: true } ? "FUENTE CERRADA" : "FUENTE NO VINCULADA · agregá la ventana nuevamente");
        PreviewPlayButton.IsEnabled = PreviewPauseButton.IsEnabled = PreviewStopButton.IsEnabled = !selectedCapture;
        var programCapture = _output?.Snapshot.Item?.Kind == MediaKind.WindowCapture;
        ProgramPlayButton.IsEnabled = ProgramPauseButton.IsEnabled = ProgramStopButton.IsEnabled = !programCapture;
        if (programCapture)
        {
            var snapshot = _output!.Snapshot;
            ProgramState.Text = "CAPTURA · " + snapshot.Item!.Name + " · " + snapshot.CaptureStatus;
        }
    }
    private async Task TakeAsync(TransitionKind transition)
    {
        if (_takeInProgress) { SetStatus("Esperá a que termine el envío actual."); return; }
        _takeInProgress = true;
        try { await TakeCoreAsync(transition); }
        finally { _takeInProgress = false; }
    }
}
