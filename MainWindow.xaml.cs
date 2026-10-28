using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using WinForms = System.Windows.Forms;

namespace EventDeck;

public partial class MainWindow : Window
{
    private ObservableCollection<MediaItem> _items = new();
    private readonly ObservableCollection<LibraryBin> _bins = new();
    private LibraryBin? _activeBin;
    private bool _programSeekChanging;
    private VideoSession? _previewSession;
    private CancellationTokenSource? _previewLoadCancellation;
    private Task _previewPreparationTask = Task.CompletedTask;
    private int _previewGeneration;
    private bool _suppressBinSelection;
    private readonly MusicSession _music = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private OutputWindowHost? _output;
    private MediaItem? _selected;
    private bool _previewMuted = true;
    private bool _libraryNavigationActive;
    private bool _restoringProgramMirror;
    private readonly GlobalHotkeyRegistration _fadeOutHotkey = new();
    private Key _fadeOutAudioKey = Key.F8;
    private bool _hotkeySettingsReady;
    private string _projectName = "Evento sin guardar";
    private string? _projectPath;

    public MainWindow()
    {
        InitializeComponent();
        _eventManagementReady = true;
        LoadOperatorSettings();
        FadeOutHotkeyCombo.SelectedValue = _fadeOutAudioKey.ToString();
        _hotkeySettingsReady = true;
        SourceInitialized += (_, _) =>
        {
            RegisterFadeOutHotkey();
            InitializeTray();
        };
        LibraryList.ItemsSource = _items;
        _activeBin = new LibraryBin { Name = "GENERAL" }; _bins.Add(_activeBin); _items = _activeBin.Items;
        AttachResourceView(); BinTabs.ItemsSource = _bins; BinTabs.SelectedItem = _activeBin;
        AudioPlayingList.ItemsSource = _music.PlayingTracks;
        _music.PreparedChanged += (_, _) => RefreshPreparedMusic();
        _music.StateChanged += (_, _) => UpdatePlayingCount();
        _music.StatusChanged += (_, message) => SetStatus(message);
        InitializeMusicWorkspace();
        DetectScreens();
        ProgramSeek.IsEnabled = false;
        _clock.Tick += (_, _) => RefreshPlaybackClocks();
        _clock.Start();
        StateChanged += MainWindow_StateChanged;
        SizeChanged += (_, _) => UpdateProgramMonitorSurface();
        LocationChanged += (_, _) => UpdateProgramMonitorSurface();
        ProgramMonitorFrame.SizeChanged += (_, _) => UpdateProgramMonitorSurface();
        Loaded += async (_, _) =>
        {
            try { await PreviewWeb.EnsureCoreWebView2Async(); }
            catch (Exception ex) { SetStatus($"WebView2 no disponible: {ex.Message}"); }
        };
        Closing += (_, _) =>
        {
            DisposeTray();
            _clock.Stop(); _fadeOutHotkey.Dispose(); _output?.Close(); CancelPreviewLoad(); _captures.Dispose(); PreviewWeb.Dispose();
            _music.Dispose();
        };
    }

    private void DetectScreens()
    {
        var selectedDevice = (ScreenCombo.SelectedItem as ScreenChoice)?.DeviceName;
        var screens = WinForms.Screen.AllScreens.Select((screen, index) => new ScreenChoice(screen, index)).ToList();
        ScreenCombo.ItemsSource = screens;
        ScreenCombo.SelectedItem = screens.FirstOrDefault(x => x.DeviceName == selectedDevice)
            ?? screens.ElementAtOrDefault(screens.Count > 1 ? 1 : 0);
        RefreshOutputStatus();
    }

    private void DetectScreens_Click(object sender, RoutedEventArgs e)
    {
        DetectScreens();
        SetStatus($"Pantallas actualizadas: {ScreenCombo.Items.Count} detectadas · {DateTime.Now:HH:mm:ss}." +
            (_output?.IsOpen == true ? " La salida abierta conserva su ubicación." : ""));
    }

    private static string OperatorSettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EventDeck", "operator-settings.json");

    private void LoadOperatorSettings()
    {
        try
        {
            if (!File.Exists(OperatorSettingsPath)) return;
            var preferences = JsonSerializer.Deserialize<OperatorPreferences>(File.ReadAllText(OperatorSettingsPath));
            if (preferences is not null && Enum.TryParse<Key>(preferences.FadeOutAudioHotkey, true, out var key) && IsFadeOutKeyAllowed(key))
                _fadeOutAudioKey = key;
        }
        catch { _fadeOutAudioKey = Key.F8; }
    }

    private void SaveOperatorSettings()
    {
        try
        {
            var directory = Path.GetDirectoryName(OperatorSettingsPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(OperatorSettingsPath, JsonSerializer.Serialize(new OperatorPreferences { FadeOutAudioHotkey = _fadeOutAudioKey.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { SetStatus($"No se pudo guardar la configuración de teclas: {ex.Message}"); }
    }

    private static bool IsFadeOutKeyAllowed(Key key) => key is Key.F8 or Key.F10 or Key.F11 or Key.F12;

    private void FadeOutHotkeyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_hotkeySettingsReady || FadeOutHotkeyCombo.SelectedValue is not string keyName ||
            !Enum.TryParse<Key>(keyName, out var key) || !IsFadeOutKeyAllowed(key)) return;
        if (_fadeOutAudioKey == key) return;
        _fadeOutAudioKey = key;
        SaveOperatorSettings();
        if (_fadeOutHotkey.IsRegistered || IsLoaded) RegisterFadeOutHotkey();
    }

    private void RegisterFadeOutHotkey()
    {
        if (_fadeOutHotkey.TryRegister(this, _fadeOutAudioKey, () => _ = FadeOutCurrentAudioAsync()))
            FadeOutAudioButton.ToolTip = $"{_fadeOutAudioKey} global · funde y detiene todo el audio de EventDeck";
        else
        {
            FadeOutAudioButton.ToolTip = $"{_fadeOutAudioKey} está ocupada en Windows; funciona cuando EventDeck tiene foco";
            SetStatus($"{_fadeOutAudioKey} ya está ocupada por otra aplicación; el fade-out queda disponible con EventDeck enfocado.");
        }
    }

    private async void Output_Click(object sender, RoutedEventArgs e)
    {
        if (_output?.IsOpen == true) { _output.Close(); OutputButton.Content = "Abrir salida"; RefreshOutputStatus(); SetStatus("Salida cerrada."); return; }
        var selectedDevice = (ScreenCombo.SelectedItem as ScreenChoice)?.DeviceName;
        DetectScreens();
        var operatorDevice = WinForms.Screen.FromHandle(new WindowInteropHelper(this).Handle).DeviceName;
        var screens = WinForms.Screen.AllScreens;
        if (screens.Length < 2)
        {
            SetStatus("No se detectó una segunda pantalla. Conectá el proyector o monitor y presioná Actualizar pantallas antes de abrir la salida.");
            return;
        }
        var screen = screens.FirstOrDefault(x => x.DeviceName == selectedDevice);
        if (screen is null || screen.DeviceName == operatorDevice)
        {
            SetStatus("La pantalla seleccionada ya no está disponible o es la del operador. Seleccioná el proyector o monitor de salida antes de abrir PROGRAM.");
            return;
        }
        var choice = new ScreenChoice(screen, 0);
        OutputButton.IsEnabled = false;
        var output = new OutputWindowHost(Dispatcher, _captures);
        _output = output;
        output.ProgramStateChanged += (_, _) => RefreshProgramControls();
        output.RecoveryFailed += (_, message) =>
        {
            if (ReferenceEquals(_output, output) && !_isClosing)
                SetStatus("No se pudo recuperar el video tras reconectar la pantalla: " + message);
        };
        output.EmergencyClosed += (_, _) =>
        {
            RestoreFromTray();
            SetStatus("Salida cerrada por ESC.");
        };
        output.DisplayAvailabilityChanged += (_, _) =>
        {
            if (!ReferenceEquals(_output, output) || _isClosing) return;
            if (output.Snapshot.IsDisplaySuspended)
            {
                RestoreFromTray();
                SetStatus("Pantalla de salida no disponible. PROGRAM conserva la reproducción y espera el monitor original; ESC cierra la salida.");
            }
            else
            {
                DetectScreens();
                UpdateProgramMonitorSurface();
                SetStatus("Pantalla recuperada. PROGRAM continúa con la reproducción actual.");
            }
            RefreshOutputStatus();
        };
        output.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_output, output)) return;
            _output = null;
            RefreshOutputStatus();
            OutputButton.Content = "Abrir salida"; ProgramState.Text = "NEGRO";
            ProgramImage.Source = null; ProgramImage.Visibility = Visibility.Collapsed; ProgramPlaceholder.Visibility = Visibility.Visible;
            ProgramMuteButton.IsEnabled = false; ProgramSeek.IsEnabled = false; ProgramSeek.Value = 0; ProgramPosition.Text = "00:00 / 00:00";
        };
        var bounds = choice.Screen.Bounds;
        try
        {
            await output.OpenAsync(false, bounds.Left, bounds.Top, bounds.Width, bounds.Height, choice.DeviceName, operatorDevice);
            if (!ReferenceEquals(_output, output) || !output.IsOpen) return;
            OutputButton.Content = "Cerrar salida"; ProgramState.Text = "NEGRO";
            SetStatus($"Salida abierta en {choice.Label}. El público ve negro.");
            UpdateProgramMonitorSurface();
            RefreshProgramControls();
        }
        catch (Exception ex)
        {
            _output = null; OutputButton.Content = "Abrir salida";
            RefreshOutputStatus();
            SetStatus($"No se pudo abrir la salida: {ex.Message}");
        }
        finally { OutputButton.IsEnabled = true; }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        // Native minimize is intercepted before WPF; this also covers direct WindowState assignments.
        if (WindowState == WindowState.Minimized)
        {
            HideToTray();
            return;
        }
        if (_changingTrayVisibility || _isClosing) return;
        _lastVisibleWindowState = WindowState;
        // Wait for WPF to finish restoring layout before reconnecting the monitor mirror.
        // This path only updates the mirror and controls; it never commands the playback master.
        _restoringProgramMirror = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _restoringProgramMirror = false;
            UpdateProgramMonitorSurface();
            RefreshProgramControls();
        }));
    }

    private void UpdateProgramMonitorSurface()
    {
        if (ProgramImage is null || _restoringProgramMirror || !IsVisible || _isClosing) return;
        var output = _output;
        if (!IsProgramMonitorInsideWorkspace())
        {
            output?.HideProgramMirror();
            return;
        }
        if (output is not { IsOpen: true } || WindowState == WindowState.Minimized)
        {
            output?.HideProgramMirror();
            ProgramImage.Source = null;
            ProgramImage.Visibility = Visibility.Collapsed;
            ProgramPlaceholder.Visibility = Visibility.Visible;
            return;
        }
        if (!IsLoaded || ProgramMonitorFrame.ActualWidth < 2 || ProgramMonitorFrame.ActualHeight < 2) return;

        var windowHandle = new WindowInteropHelper(this).Handle;
        var monitorBounds = ProgramMonitorFrame.TransformToAncestor(this)
            .TransformBounds(new System.Windows.Rect(0, 0, ProgramMonitorFrame.ActualWidth, ProgramMonitorFrame.ActualHeight));
        var toDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
                       ?? System.Windows.Media.Matrix.Identity;
        monitorBounds.Transform(toDevice);
        const int inset = 5;
        var x = (int)Math.Round(monitorBounds.X) + inset;
        var y = (int)Math.Round(monitorBounds.Y) + inset;
        var width = Math.Max(1, (int)Math.Round(monitorBounds.Width) - inset * 2);
        var height = Math.Max(1, (int)Math.Round(monitorBounds.Height) - inset * 2);

        ProgramImage.Source = null;
        ProgramImage.Visibility = Visibility.Collapsed;
        ProgramPlaceholder.Visibility = Visibility.Collapsed;
        output.SetProgramMirrorBounds(windowHandle, x, y, width, height);
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Agregar medios", Multiselect = true, Filter = "Archivos multimedia|*.png;*.jpg;*.jpeg;*.bmp;*.mp4;*.m4v;*.wmv;*.avi;*.mp3;*.wav;*.wma;*.m4a;*.aac|Todos los archivos|*.*" };
        if (dialog.ShowDialog() == true) foreach (var path in dialog.FileNames) AddPath(path);
        UpdateCount();
    }

    private void AddPath(string path)
    {
        if (!File.Exists(path)) return;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var kind = extension switch
        {
            ".png" or ".jpg" or ".jpeg" or ".bmp" => MediaKind.Image,
            ".mp4" or ".m4v" or ".wmv" or ".avi" => MediaKind.Video,
            ".mp3" or ".wav" or ".wma" or ".m4a" or ".aac" => MediaKind.Audio,
            _ => (MediaKind?)null
        };
        if (kind is null) { SetStatus($"Formato no admitido: {Path.GetFileName(path)}"); return; }
        if (_items.Any(item => item.Source.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        _items.Add(new MediaItem { Name = Path.GetFileNameWithoutExtension(path), Source = path, Kind = kind.Value });
        UpdateCount();
    }

    private async void AddWeb_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WebResourceDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var url = dialog.Url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) { SetStatus("La URL no es válida."); return; }
        var item = new MediaItem { Name = string.IsNullOrWhiteSpace(dialog.ResourceName) ? uri.Host : dialog.ResourceName.Trim(), Source = uri.AbsoluteUri, Kind = MediaKind.Web };
        _items.Add(item); LibraryList.SelectedItem = item;
        try { await PreviewWeb.EnsureCoreWebView2Async(); PreviewWeb.Source = uri; }
        catch (Exception ex) { SetStatus($"No se pudo abrir la página: {ex.Message}"); }
        UpdateCount();
    }

    private async void LibraryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LibraryList.SelectedItem is not MediaItem item) { RefreshEventHealth(); return; }
        if (item.IsMissing)
        {
            StopAudioPreview();
            _selected = item;
            CancelPreviewLoad(); _previewPreparationTask = Task.CompletedTask;
            PreviewTitle.Text = item.Name;
            PreviewAudioCard.Visibility = Visibility.Collapsed;
            PreviewImage.Visibility = Visibility.Collapsed; PreviewVideo.Source = null; PreviewVideo.Visibility = Visibility.Collapsed;
            PreviewWeb.Visibility = Visibility.Collapsed;
            PreviewHint.Text = "ARCHIVO NO ENCONTRADO\nRevinculá este recurso para volver a utilizarlo.";
            PreviewHint.Visibility = Visibility.Visible;
            PreviewLoop.IsEnabled = false; PreviewLoop.IsChecked = false;
            PreviewMuteButton.IsEnabled = false; PreviewSeek.IsEnabled = false;
            UpdateProgramMuteButton(null); RefreshProgramControls(); RefreshEventHealth();
            return;
        }
        if (item.Kind == MediaKind.Audio)
        {
            StopAudioPreview();
            _selected = item;
            CancelPreviewLoad(); _previewPreparationTask = Task.CompletedTask;
            PreviewTitle.Text = item.Name;
            PreviewAudioName.Text = item.Name;
            PreviewAudioCard.Visibility = Visibility.Visible;
            PreviewImage.Visibility = Visibility.Collapsed; PreviewVideo.Visibility = Visibility.Collapsed; PreviewWeb.Visibility = Visibility.Collapsed;
            PreviewHint.Visibility = Visibility.Collapsed;
            PreviewLoop.IsEnabled = false; PreviewLoop.IsChecked = false;
            PreviewMuteButton.IsEnabled = true; PreviewSeek.Minimum = 0; PreviewSeek.Maximum = 1; PreviewSeek.Value = 0;
            PreviewCurrent.Text = "00:00"; PreviewDuration.Text = "00:00";
            UpdateProgramMuteButton(null);
            PrepareAudio(item);
            return;
        }

        StopAudioPreview();
        _selected = item;
        PreviewTitle.Text = item.Name;
        PreviewAudioCard.Visibility = Visibility.Collapsed;
        PreviewHint.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewVideo.Source = null; PreviewVideo.Visibility = Visibility.Collapsed;
        PreviewWeb.Visibility = Visibility.Collapsed;
        PreviewSeek.Minimum = 0; PreviewSeek.Maximum = 1; PreviewSeek.Value = 0;
        PreviewCurrent.Text = "00:00"; PreviewDuration.Text = "00:00";
        PreviewLoop.IsEnabled = item.Kind == MediaKind.Video;
        PreviewLoop.IsChecked = item.Kind == MediaKind.Video && item.Loop;
        UpdateProgramMuteButton(item);
        RefreshProgramControls();
        try
        {
            switch (item.Kind)
            {
                case MediaKind.Image:
                    CancelPreviewLoad(); _previewPreparationTask = Task.CompletedTask;
                    if (!File.Exists(item.Source)) { SetStatus("No se encuentra la imagen. Revisá la ruta."); return; }
                    PreviewImage.Source = new BitmapImage(new Uri(item.Source)); PreviewImage.Visibility = Visibility.Visible;
                    break;
                case MediaKind.Video:
                    _previewPreparationTask = PreparePreviewVideoAsync(item);
                    await _previewPreparationTask;
                    break;
                case MediaKind.WindowCapture:
                    PrepareWindowCapture(item);
                    break;
                case MediaKind.Web:
                    CancelPreviewLoad(); _previewPreparationTask = Task.CompletedTask;
                    await PreviewWeb.EnsureCoreWebView2Async();
                    if (!ReferenceEquals(_selected, item)) return;
                    PreviewWeb.Visibility = Visibility.Visible; PreviewWeb.Source = new Uri(item.Source);
                    break;
            }
        }
        catch (Exception ex) { PreviewHint.Text = $"No se pudo preparar el recurso: {ex.Message}"; PreviewHint.Visibility = Visibility.Visible; SetStatus(ex.Message); }
    }

    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null && !ReferenceEquals(current, LibraryList))
        {
            current = current is System.Windows.Media.Visual
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        _libraryNavigationActive = current is not null;
    }

    private async void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _output?.IsOpen == true)
        {
            e.Handled = true;
            _output.Close();
            RestoreFromTray();
            return;
        }
        if (e.Key == Key.F6)
        {
            e.Handled = true;
            if (!e.IsRepeat) ToggleVideoAudio();
            return;
        }
        if (e.Key == Key.F9 && !_visibilityHotkey.IsRegistered)
        {
            e.Handled = true;
            if (!e.IsRepeat) ToggleTrayVisibility();
            return;
        }
        if (e.Key == Key.F7)
        {
            e.Handled = true;
            ToggleVideoLoopFromHotkey();
            return;
        }
        if (e.Key == _fadeOutAudioKey && !_fadeOutHotkey.IsRegistered)
        {
            e.Handled = true;
            _ = FadeOutCurrentAudioAsync();
            return;
        }
        if (_musicWorkspaceVisible)
        {
            HandleMusicKeyDown(e);
            return;
        }
        if (!_libraryNavigationActive) return;
        if (e.Key is Key.Up or Key.Down)
        {
            MoveLibrarySelection(e.Key);
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter || LibraryList.SelectedItem is not MediaItem) return;
        e.Handled = true;
        await ExecuteSelectedResourceAsync();
    }

    private async void LibraryList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        _libraryNavigationActive = true;
        if (e.Key == Key.Enter && LibraryList.SelectedItem is MediaItem)
        {
            e.Handled = true;
            await ExecuteSelectedResourceAsync();
            return;
        }
        if (e.Key is not (Key.Up or Key.Down)) return;
        MoveLibrarySelection(e.Key);
        e.Handled = true;
    }

    private void MoveLibrarySelection(Key key)
    {
        if (LibraryList.Items.Count == 0) return;
        var index = LibraryList.SelectedIndex;
        index = key == Key.Down
            ? Math.Min(LibraryList.Items.Count - 1, index + 1)
            : Math.Max(0, index < 0 ? 0 : index - 1);
        LibraryList.SelectedIndex = index;
        LibraryList.ScrollIntoView(LibraryList.SelectedItem);
    }

    private async Task ExecuteSelectedResourceAsync()
    {
        if (LibraryList.SelectedItem is not MediaItem item) return;
        _selected = item;
        if (item.Kind == MediaKind.Audio)
        {
            if (_music.PreparedTrack is null || !_music.PreparedTrack.Source.Equals(item.Source, StringComparison.OrdinalIgnoreCase)) PrepareAudio(item);
            if (_music.PreparedTrack is not null) PlayPreparedAudio();
            return;
        }
        if (item.Kind == MediaKind.Video)
        {
            // Do not queue ENTER presses behind preparation or an active TAKE.
            if (_takeInProgress || !_previewPreparationTask.IsCompleted)
            { SetStatus("Esperá a que el video termine de prepararse en PREVIEW."); return; }
            if (!ReferenceEquals(LibraryList.SelectedItem, item)) return;
        }
        await TakeAsync(TransitionKind.Merge);
    }

    private async Task PreparePreviewVideoAsync(MediaItem item)
    {
        CancelPreviewLoad();
        var generation = ++_previewGeneration;
        var cancellation = new CancellationTokenSource();
        _previewLoadCancellation = cancellation;
        var token = cancellation.Token;
        if (_previewSession is not null) { _previewSession.StateChanged -= PreviewSession_StateChanged; _previewSession.Dispose(); }
        var session = new VideoSession(new Uri(item.Source), item.Loop);
        _previewSession = session;
        PreviewVideo.Source = null; PreviewVideo.Visibility = Visibility.Collapsed;
        PreviewHint.Text = "Cargando y preparando el primer cuadro…"; PreviewHint.Visibility = Visibility.Visible;
        PreviewMuteButton.IsEnabled = false; PreviewLoop.IsEnabled = true;
        PreviewVideo.IsEnabled = false; PreviewSeek.IsEnabled = false;
        try
        {
            await session.OpenAsync(token);
            if (generation != _previewGeneration || !ReferenceEquals(_previewSession, session)) return;
            PreviewVideo.Source = session.PreviewImage; PreviewVideo.Visibility = Visibility.Visible;
            session.StateChanged += PreviewSession_StateChanged;
            await session.PrimeFirstFrameAsync(token);
            if (generation != _previewGeneration || !ReferenceEquals(_previewSession, session)) return;
            session.Player.IsMuted = _previewMuted;
            session.Player.Volume = 1;
            PreviewHint.Visibility = Visibility.Collapsed;
            PreviewSeek.Maximum = Math.Max(1, session.Duration.TotalSeconds); PreviewSeek.Value = 0;
            PreviewDuration.Text = FormatTime(session.Duration); PreviewCurrent.Text = "00:00";
            PreviewMuteButton.IsEnabled = true; PreviewVideo.IsEnabled = true; PreviewSeek.IsEnabled = true;
            UpdatePreviewMuteButton();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_previewSession, session))
            {
                _previewSession = null; session.Dispose();
                PreviewVideo.Source = null; PreviewVideo.Visibility = Visibility.Collapsed;
                PreviewHint.Text = $"No se pudo preparar el video: {ex.Message}"; PreviewHint.Visibility = Visibility.Visible;
                SetStatus($"No se pudo preparar {item.Name}: {ex.Message}");
            }
        }
    }

    private void CancelPreviewLoad()
    {
        ReleasePreviewCapture();
        _previewGeneration++;
        _previewLoadCancellation?.Cancel(); _previewLoadCancellation?.Dispose(); _previewLoadCancellation = null;
        if (_previewSession is not null) { _previewSession.StateChanged -= PreviewSession_StateChanged; _previewSession.Dispose(); _previewSession = null; }
        if (PreviewVideo is not null) { PreviewVideo.Source = null; PreviewVideo.Visibility = Visibility.Collapsed; PreviewVideo.IsEnabled = false; }
        if (PreviewMuteButton is not null) PreviewMuteButton.IsEnabled = false;
        if (PreviewSeek is not null) PreviewSeek.IsEnabled = false;
    }

    private void PreviewSession_StateChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _previewSession) || _previewSession is null) return;
        PreviewCurrent.Text = FormatTime(_previewSession.Player.Position);
        if (_previewSession.IsEnded) PreviewCurrent.Text = FormatTime(_previewSession.Duration);
    }

    private void StopAudioPreview() => _music.StopActivePreview(_previewMuted);

    private async Task<string?> CaptureWebSnapshotAsync()
    {
        try
        {
            if (PreviewWeb.CoreWebView2 is null) return null;
            var path = Path.Combine(Path.GetTempPath(), $"eventdeck-{Guid.NewGuid():N}.png");
            await using var stream = File.Create(path);
            await PreviewWeb.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            return path;
        }
        catch { return null; }
    }

    private async Task TakeCoreAsync(TransitionKind transition)
    {
        if (_selected is null) { SetStatus("Primero seleccioná un recurso visual de la biblioteca."); return; }
        if (_output is not { IsOpen: true }) { SetStatus("Abrí la salida pública antes de enviar contenido."); return; }
        var item = _selected;
        if ((item.Kind == MediaKind.Image || item.Kind == MediaKind.Video) && !File.Exists(item.Source)) { SetStatus("El archivo ya no existe en esa ruta."); return; }
        if (item.Kind == MediaKind.Web && !Uri.TryCreate(item.Source, UriKind.Absolute, out _)) { SetStatus("La URL no es válida."); return; }
        if (item.Kind == MediaKind.Video && (!_previewPreparationTask.IsCompleted || _previewSession is not { IsReady: true } || !_previewSession.Source.Equals(new Uri(item.Source))))
        { SetStatus("Esperá a que el video termine de prepararse en PREVIEW."); return; }

        var snapshot = item.Kind == MediaKind.Web ? await CaptureWebSnapshotAsync() : null;
        var prepared = item.Kind == MediaKind.Video ? _previewSession : null;
        if (prepared is not null)
        {
            prepared.StateChanged -= PreviewSession_StateChanged;
            _previewSession = null; _previewLoadCancellation?.Cancel(); _previewLoadCancellation?.Dispose(); _previewLoadCancellation = null;
            PreviewMuteButton.IsEnabled = false; PreviewSeek.IsEnabled = false; PreviewVideo.IsEnabled = false;
            PreviewHint.Text = "Enviando video a PROGRAM…"; PreviewHint.Visibility = Visibility.Visible;
            prepared.Dispose();
        }
        try
        {
            await _output.ShowItemAsync(item, snapshot, transition);
            UpdateProgramMonitorSurface();
            ProgramState.Text = item.TypeLabel + "  ·  " + item.Name.ToUpperInvariant();
            UpdateProgramMuteButton(item); RefreshProgramControls();
            SetStatus($"{item.Name} enviado a PROGRAM.");
        }
        catch (Exception ex)
        {
            SetStatus($"No se pudo enviar el recurso: {ex.Message}");
            if (prepared is not null) prepared.Dispose();
            if (ReferenceEquals(_selected, item)) { PreviewHint.Text = $"No se pudo enviar el recurso: {ex.Message}"; PreviewHint.Visibility = Visibility.Visible; }
        }
        finally
        {
            // PROGRAM owns a different decoder on its STA. Re-arm PREVIEW even after a failed TAKE.
            // A selection change during the await already owns its own preparation.
            if (prepared is not null && !_isClosing && ReferenceEquals(_selected, item) &&
                ReferenceEquals(LibraryList.SelectedItem, item) && _previewSession is null)
            {
                _previewPreparationTask = PreparePreviewVideoAsync(item);
                await _previewPreparationTask;
            }
        }
    }

    private async void Cut_Click(object sender, RoutedEventArgs e) => await TakeAsync(TransitionKind.Cut);
    private async void Fade_Click(object sender, RoutedEventArgs e) => await TakeAsync(TransitionKind.Fade);
    private async void Merge_Click(object sender, RoutedEventArgs e) => await TakeAsync(TransitionKind.Merge);
    private async void Black_Click(object sender, RoutedEventArgs e) => await ToggleBlackAsync();

    private void PreviewPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_previewSession is not null) { _previewSession.Play(); PreviewCurrent.Text = FormatTime(_previewSession.Player.Position); }
        else if (_selected?.Kind == MediaKind.Audio && _music.PreparedTrack is { } audio && audio.Source.Equals(_selected.Source, StringComparison.OrdinalIgnoreCase))
        {
            _music.PlayPreview(_previewMuted);
        }
        else if (_selected?.Kind == MediaKind.Web) SetStatus("La página web está activa: podés interactuar directamente en PREVIEW.");
    }
    private void PreviewPause_Click(object sender, RoutedEventArgs e)
    {
        if (_previewSession is not null) _previewSession.Pause();
        else if (_selected?.Kind == MediaKind.Audio && _music.PreparedTrack is { } audio && audio.Source.Equals(_selected.Source, StringComparison.OrdinalIgnoreCase))
        { _music.PausePreview(); }
    }
    private void PreviewStop_Click(object sender, RoutedEventArgs e)
    {
        if (_previewSession is not null) _previewSession.Stop();
        else if (_selected?.Kind == MediaKind.Audio && _music.PreparedTrack is { } audio && audio.Source.Equals(_selected.Source, StringComparison.OrdinalIgnoreCase))
        { _music.StopPreview(); }
        else return;
        PreviewSeek.Value = 0; PreviewCurrent.Text = "00:00";
    }
    private void PreviewLoop_Click(object sender, RoutedEventArgs e)
    {
        if (_selected?.Kind == MediaKind.Video)
        {
            _selected.Loop = PreviewLoop.IsChecked == true;
            if (_previewSession is not null) _previewSession.Loop = _selected.Loop;
            if (_output?.CurrentProgramItem?.Source == _selected.Source) _output.SetProgramLoop(_selected.Loop);
            return;
        }

        var activeProgram = _output?.Snapshot;
        if (activeProgram is not { IsVideo: true, Item: { } activeItem }) return;
        activeItem.Loop = PreviewLoop.IsChecked == true;
        _output?.SetProgramLoop(activeItem.Loop);
        var libraryItem = _bins.SelectMany(bin => bin.Items)
            .FirstOrDefault(item => item.Source.Equals(activeItem.Source, StringComparison.OrdinalIgnoreCase));
        if (libraryItem is not null) libraryItem.Loop = activeItem.Loop;
    }

    private void ToggleVideoLoopFromHotkey()
    {
        var selectedVideo = _selected?.Kind == MediaKind.Video ? _selected : null;
        var activeProgram = _output?.Snapshot;
        var activeItem = activeProgram is { IsVideo: true } ? activeProgram.Item : null;
        var target = selectedVideo ?? activeItem;
        if (target is null) return;

        target.Loop = !target.Loop;
        PreviewLoop.IsChecked = target.Loop;
        PreviewLoop.IsEnabled = selectedVideo is not null || activeItem is not null;

        if (_previewSession is not null && selectedVideo is not null &&
            _previewSession.Source.OriginalString.Equals(selectedVideo.Source, StringComparison.OrdinalIgnoreCase))
            _previewSession.Loop = target.Loop;

        if (activeItem is not null && (selectedVideo is null ||
            selectedVideo.Source.Equals(activeItem.Source, StringComparison.OrdinalIgnoreCase)))
        {
            activeItem.Loop = target.Loop;
            _output?.SetProgramLoop(target.Loop);
            var libraryItem = _bins.SelectMany(bin => bin.Items)
                .FirstOrDefault(item => item.Source.Equals(activeItem.Source, StringComparison.OrdinalIgnoreCase));
            if (libraryItem is not null) libraryItem.Loop = target.Loop;
        }
    }
    private MediaItem? GetVideoAudioTarget()
    {
        // Match F7: a selected video is the operator's next cue; otherwise use Program.
        if (_selected?.Kind == MediaKind.Video) return _selected;
        var programItem = _output?.Snapshot.Item;
        if (programItem?.Kind != MediaKind.Video) return null;
        return _bins.SelectMany(bin => bin.Items).FirstOrDefault(item =>
            item.Source.Equals(programItem.Source, StringComparison.OrdinalIgnoreCase)) ?? programItem;
    }

    private void ProgramMute_Click(object sender, RoutedEventArgs e) => ToggleVideoAudio();

    private void ToggleVideoAudio()
    {
        var target = GetVideoAudioTarget();
        if (target is null) return;
        target.ProgramMuted = !target.ProgramMuted;
        var programItem = _output?.Snapshot.Item;
        if (programItem?.Kind == MediaKind.Video &&
            programItem.Source.Equals(target.Source, StringComparison.OrdinalIgnoreCase))
        {
            programItem.ProgramMuted = target.ProgramMuted;
            _output?.SetProgramMuted(target.ProgramMuted);
        }
        UpdateProgramMuteButton(target);
    }

    private void UpdateProgramMuteButton(MediaItem? item)
    {
        var target = GetVideoAudioTarget();
        ProgramMuteButton.IsEnabled = target is not null;
        ProgramMuteButton.Content = target is { ProgramMuted: true } ? "🔇 SALIDA MUTEADA" : "🔊 SALIDA CON AUDIO";
    }
    private void PreviewMute_Click(object sender, RoutedEventArgs e)
    {
        _previewMuted = !_previewMuted;
        if (_previewSession is not null) _previewSession.Player.IsMuted = _previewMuted;
        if (_selected?.Kind == MediaKind.Audio && _music.PreparedTrack is { } audio && audio.Source.Equals(_selected.Source, StringComparison.OrdinalIgnoreCase)) _music.SetPreviewMuted(_previewMuted);
        UpdatePreviewMuteButton();
    }
    private void UpdatePreviewMuteButton()
    {
        PreviewMuteButton.Content = _previewMuted ? "🔇 AUDIO PREVIEW" : "🔊 AUDIO PREVIEW";
        PreviewMuteButton.Background = _previewMuted ? (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 39, 53)) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 72, 64));
    }
    private void PreviewSeek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed || !PreviewSeek.IsMouseOver) return;
        if (_previewSession is not null) _previewSession.Seek(TimeSpan.FromSeconds(PreviewSeek.Value));
        else if (_selected?.Kind == MediaKind.Audio && _music.PreparedTrack is { } audio && audio.Source.Equals(_selected.Source, StringComparison.OrdinalIgnoreCase)) _music.SeekPrepared(TimeSpan.FromSeconds(PreviewSeek.Value));
    }
    private void PreviewSeek_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_previewSession is not null) _previewSession.Seek(TimeSpan.FromSeconds(PreviewSeek.Value));
        else if (_selected?.Kind == MediaKind.Audio && _music.PreparedTrack is { } audio && audio.Source.Equals(_selected.Source, StringComparison.OrdinalIgnoreCase)) _music.SeekPrepared(TimeSpan.FromSeconds(PreviewSeek.Value));
    }
    private async void LibraryList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LibraryList.SelectedItem is MediaItem { Kind: MediaKind.Audio }) return;
        await TakeAsync(TransitionKind.Fade);
    }

    private void PrepareAudio(MediaItem item) =>
        _music.Prepare(item.Name, item.Source, PreparedAudioVolume.Value, _previewMuted);

    private void RefreshPreparedMusic()
    {
        var track = _music.PreparedTrack;
        var error = _music.PreparationError;
        PreparedAudioTitle.Text = track?.Name ?? error?.Name ?? "Elegí una canción en la biblioteca";
        PreparedAudioState.Text = _music.PreparationState switch
        {
            MusicPreparationState.Loading => "CARGANDO",
            MusicPreparationState.Ready => "LISTA PARA PLAY",
            MusicPreparationState.PreviewPlaying => "VISTA PREVIA",
            MusicPreparationState.PreviewPaused => "VISTA PAUSADA",
            MusicPreparationState.Error => "ERROR",
            _ => "SIN PISTA"
        };
        PreparedAudioDuration.Text = error is not null
            ? error.MissingFile ? "No se encuentra el archivo" : "No se pudo abrir el archivo"
            : track is null ? "Duración: --:--"
            : !track.IsPrepared ? "Duración: cargando…"
            : _music.PreparedHasDuration ? $"Duración: {FormatTime(TimeSpan.FromSeconds(track.DurationSeconds))}"
            : "Duración: no disponible";
        // PLAY during asynchronous Open still queues one request in the engine.
        PlayPreparedButton.IsEnabled = RemovePreparedButton.IsEnabled = track is not null;
    }

    private void PlayPreparedAudio_Click(object sender, RoutedEventArgs e) => PlayPreparedAudio();
    private void PlayPreparedAudio() => _musicNavigation!.LaunchPrepared();

    private void AudioTrackToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AudioTrack track) _music.ToggleTrack(track);
    }
    private void AudioTrackStop_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AudioTrack track) _music.StopTrack(track);
    }
    private async void RemoveAudioTrack_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AudioTrack track) return;
        if (track.StateText == "REPRODUCIENDO")
        {
            if (System.Windows.MessageBox.Show(this, $"‘{track.Name}’ está sonando. ¿Querés fundirla y quitarla de la lista? El archivo original se conserva.", "Quitar pista", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await _music.RemoveWithFadeAsync(track);
        }
        else _music.RemoveTrack(track);
    }
    private void RemovePreparedAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_music.PreparedTrack is null) return;
        _music.RemovePrepared();
        SetStatus("Pista preparada quitada. El archivo original se conserva.");
    }
    private async void ClearAudioList_Click(object sender, RoutedEventArgs e)
    {
        if ((_music.PlayingCount > 0 || _music.PreparedTrack is not null) && System.Windows.MessageBox.Show(this,
            "¿Quitar todas las pistas de la lista, incluida la preparada? Las pistas que suenan se fundirán y los archivos originales se conservarán.",
            "Limpiar lista de música", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await _music.ClearAsync();
    }
    private void AudioTrackSeek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not Slider slider || slider.Tag is not AudioTrack track) return;
        if (Mouse.LeftButton == MouseButtonState.Pressed && slider.IsMouseOver) _music.SeekTrack(track, slider.Value);
    }
    private void AudioTrackVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is Slider slider && slider.Tag is AudioTrack track && Math.Abs(track.Volume - slider.Value) > 0.001) _music.SetVolume(track, slider.Value);
    }
    private void PreparedAudioVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_music.PreparedTrack is not null) _music.SetPreparedVolume(PreparedAudioVolume.Value);
    }

    private void RefreshPlaybackClocks()
    {
        RefreshCaptureControls();
        RefreshProgramControls();
        if (_previewSession is { IsReady: true } session)
        {
            if (!(Mouse.LeftButton == MouseButtonState.Pressed && PreviewSeek.IsMouseOver)) PreviewSeek.Value = Math.Min(session.Player.Position.TotalSeconds, PreviewSeek.Maximum);
            PreviewCurrent.Text = FormatTime(session.IsEnded ? session.Duration : session.Player.Position);
        }
        else if (_selected?.Kind == MediaKind.Audio && _music.PreparedTrack is { } audio && audio.Source.Equals(_selected.Source, StringComparison.OrdinalIgnoreCase) && _music.PreparedHasDuration)
        {
            var position = _music.PreparedPosition;
            if (!(Mouse.LeftButton == MouseButtonState.Pressed && PreviewSeek.IsMouseOver)) PreviewSeek.Value = Math.Min(position.TotalSeconds, PreviewSeek.Maximum);
            PreviewCurrent.Text = FormatTime(position);
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryList.SelectedItem is not MediaItem item) return;
        var playingAudio = item.Kind == MediaKind.Audio ? _music.PlayingTracks.Where(t => t.Source.Equals(item.Source, StringComparison.OrdinalIgnoreCase)).ToList() : new List<AudioTrack>();
        if (playingAudio.Count > 0 && System.Windows.MessageBox.Show(this, "Esta pista está sonando. ¿Querés quitarla de la biblioteca y detener su reproducción? El archivo original se conserva.", "Quitar pista", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (ReferenceEquals(_selected, item))
        {
            CancelPreviewLoad(); PreviewWeb.Visibility = Visibility.Collapsed; PreviewImage.Visibility = Visibility.Collapsed;
            PreviewAudioCard.Visibility = Visibility.Collapsed;
            PreviewHint.Visibility = Visibility.Visible; PreviewHint.Text = "Seleccioná un recurso visual"; _selected = null;
        }
        if (_music.PreparedTrack is not null && _music.PreparedTrack.Source.Equals(item.Source, StringComparison.OrdinalIgnoreCase))
        {
            _music.RemovePrepared();
        }
        foreach (var track in playingAudio) _music.RemoveTrack(track);
        _items.Remove(item); UpdateCount();
    }

    private void BinTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressBinSelection || BinTabs.SelectedItem is not LibraryBin bin || ReferenceEquals(_activeBin, bin)) return;
        _activeBin = bin; _items = bin.Items; AttachResourceView(); LibraryList.SelectedItem = null; UpdateCount();
    }

    private void NewBin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new BinNameDialog("Agregar pestaña") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (_bins.Any(b => b.Name.Equals(dialog.BinName, StringComparison.OrdinalIgnoreCase))) { SetStatus("Ya existe una pestaña con ese nombre."); return; }
        var bin = new LibraryBin { Name = dialog.BinName }; _bins.Add(bin); BinTabs.SelectedItem = bin;
    }

    private void RenameBin_Click(object sender, RoutedEventArgs e)
    {
        if (_activeBin is null) return;
        var dialog = new BinNameDialog("Renombrar pestaña", _activeBin.Name) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (_bins.Any(b => !ReferenceEquals(b, _activeBin) && b.Name.Equals(dialog.BinName, StringComparison.OrdinalIgnoreCase))) { SetStatus("Ya existe una pestaña con ese nombre."); return; }
        _activeBin.Name = dialog.BinName; var selected = _activeBin; BinTabs.ItemsSource = null; BinTabs.ItemsSource = _bins; BinTabs.SelectedItem = selected;
    }

    private void DeleteBin_Click(object sender, RoutedEventArgs e)
    {
        if (_activeBin is null) return;
        if (_bins.Count == 1) { SetStatus("El evento debe conservar al menos una pestaña."); return; }
        if (System.Windows.MessageBox.Show(this, $"¿Eliminar la pestaña ‘{_activeBin.Name}’ y sus referencias a recursos? Los archivos originales no se borran.", "Eliminar pestaña", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var index = _bins.IndexOf(_activeBin); _bins.Remove(_activeBin); BinTabs.SelectedItem = _bins[Math.Max(0, index - 1)];
    }

    private void RefreshProgramControls()
    {
        RefreshOutputStatus();
        var snapshot = _output?.Snapshot;
        if (snapshot is not { IsVideo: true })
        {
            ProgramPosition.Text = "00:00 / 00:00"; ProgramSeek.IsEnabled = false;
            if (snapshot?.Item is null) ProgramState.Text = "NEGRO";
            else if (snapshot.Item.Kind == MediaKind.Video) ProgramState.Text = snapshot.IsEnded ? "FINALIZADO" : "VIDEO EN PROGRAM";
            return;
        }
        var duration = snapshot.Duration;
        var position = snapshot.Position;
        var transportState = snapshot.IsEnded ? "FINALIZADO" : snapshot.IsStopped ? "DETENIDO" : snapshot.IsPlaying ? "REPRODUCIENDO" : "EN PAUSA";
        ProgramState.Text = $"{transportState}  ·  {snapshot.Item?.Name.ToUpperInvariant()}";
        ProgramPosition.Text = $"{FormatTime(position)} / {FormatTime(duration)}";
        ProgramSeek.Maximum = Math.Max(1, duration.TotalSeconds);
        ProgramSeek.IsEnabled = duration > TimeSpan.Zero;
        if (!(Mouse.LeftButton == MouseButtonState.Pressed && ProgramSeek.IsMouseOver)) { _programSeekChanging = true; ProgramSeek.Value = Math.Min(position.TotalSeconds, ProgramSeek.Maximum); _programSeekChanging = false; }
    }

    private void ProgramPlay_Click(object sender, RoutedEventArgs e) => _output?.PlayProgram();
    private void ProgramPause_Click(object sender, RoutedEventArgs e) => _output?.PauseProgram();
    private void ProgramStop_Click(object sender, RoutedEventArgs e) => _output?.StopProgram();
    private void ProgramSeek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_programSeekChanging) return;
        ProgramPosition.Text = $"{FormatTime(TimeSpan.FromSeconds(ProgramSeek.Value))} / {FormatTime(TimeSpan.FromSeconds(ProgramSeek.Maximum))}";
    }
    private void ProgramSeek_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_output?.IsProgramVideo != true) return;
        _output.SeekProgram(TimeSpan.FromSeconds(ProgramSeek.Value));
    }

    private void FadeOutAudioButton_Click(object sender, RoutedEventArgs e) => _ = FadeOutCurrentAudioAsync();
    private Task FadeOutCurrentAudioAsync() => _music.FadeOutCurrentAsync();

    private void LibraryList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData("EventDeck.ResourceItem") is MediaItem dragged)
        {
            ReorderResource(dragged, GetResourceAtPoint(e));
            e.Effects = System.Windows.DragDropEffects.Move;
        }
        else if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files)
        {
            foreach (var file in files) AddPath(file);
            e.Effects = System.Windows.DragDropEffects.Copy;
        }
        UpdateCount();
        e.Handled = true;
    }
    private void LibraryList_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("EventDeck.ResourceItem") ? System.Windows.DragDropEffects.Move
            : e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }
    private void UpdateCount()
    {
        UpdateResourceCountLabel();
        _captures.SetResourceBindings(_bins.SelectMany(bin => bin.Items).Select(item => item.CaptureBinding));
        RefreshResourceView();
    }
    private void UpdatePlayingCount() => PlayingCount.Text = $"{_music.PlayingCount} sonando";
    private void SetStatus(string message) => Title = "EventDeck  ·  " + message;
    private static string FormatTime(TimeSpan value) => $"{(int)value.TotalMinutes:00}:{value.Seconds:00}";

    private async void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Proyecto EventDeck (*.eventdeck.json)|*.eventdeck.json", FileName = string.IsNullOrWhiteSpace(_projectPath) ? "Evento.eventdeck.json" : Path.GetFileName(_projectPath) };
        if (!string.IsNullOrWhiteSpace(_projectPath)) dialog.InitialDirectory = Path.GetDirectoryName(_projectPath);
        if (dialog.ShowDialog() != true) return;
        _projectPath = dialog.FileName;
        var name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(_projectPath));
        try
        {
            await File.WriteAllTextAsync(_projectPath, JsonSerializer.Serialize(new EventProject { Music = _musicCollections.Capture(), Name = name, ActiveBinName = _activeBin?.Name ?? "GENERAL", Bins = _bins.ToList(), Items = _bins.SelectMany(b => b.Items).ToList() }, new JsonSerializerOptions { WriteIndented = true }));
            _projectName = name; ProjectNameText.Text = name; SetStatus("Evento guardado.");
        }
        catch (Exception ex) { SetStatus($"No se pudo guardar: {ex.Message}"); }
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Proyecto EventDeck (*.eventdeck.json)|*.eventdeck.json|Archivo JSON (*.json)|*.json", Title = "Abrir evento" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var json = await File.ReadAllTextAsync(dialog.FileName);
            var project = JsonSerializer.Deserialize<EventProject>(json);
            if (project is null) { SetStatus("El archivo de evento está vacío o no es válido."); return; }
            CancelPreviewLoad();
            _music.Reset();
            RestoreMusicProject(project.Music);
            _bins.Clear();
            var loadedBins = project.Bins?.Where(b => !string.IsNullOrWhiteSpace(b.Name)).ToList() ?? new List<LibraryBin>();
            if (loadedBins.Count == 0) loadedBins.Add(new LibraryBin { Name = "GENERAL", Items = new ObservableCollection<MediaItem>(project.Items ?? new List<MediaItem>()) });
            foreach (var bin in loadedBins)
            {
                RemoveLegacySectionItems(bin.Items);
                _bins.Add(bin);
            }
            _activeBin = _bins.FirstOrDefault(b => b.Name.Equals(project.ActiveBinName, StringComparison.OrdinalIgnoreCase)) ?? _bins[0];
            _suppressBinSelection = true;
            BinTabs.ItemsSource = null; BinTabs.ItemsSource = _bins; BinTabs.SelectedItem = _activeBin;
            _items = _activeBin.Items; AttachResourceView();
            _suppressBinSelection = false;
            _projectPath = dialog.FileName; _projectName = project.Name; ProjectNameText.Text = project.Name;
            PreviewImage.Visibility = Visibility.Collapsed; PreviewVideo.Visibility = Visibility.Collapsed; PreviewWeb.Visibility = Visibility.Collapsed;
            PreviewAudioCard.Visibility = Visibility.Collapsed;
            PreviewHint.Text = "Seleccioná un recurso visual"; PreviewHint.Visibility = Visibility.Visible;
            PreviewTitle.Text = "Seleccioná un recurso"; PreviewSeek.Value = 0; PreviewCurrent.Text = "00:00"; PreviewDuration.Text = "00:00";
            PreparedAudioTitle.Text = "Elegí una canción en la biblioteca"; PreparedAudioState.Text = "SIN PISTA";
            PreparedAudioDuration.Text = "Duración: --:--"; PlayPreparedButton.IsEnabled = false; RemovePreparedButton.IsEnabled = false;
            _selected = null;
            ResourceSearchBox.Text = ""; ResourceKindFilter.SelectedIndex = 0;
            UpdateCount();
            var missing = _bins.SelectMany(b => b.Items).Count(item => item.IsMissing);
            var unlinked = _bins.SelectMany(b => b.Items).Count(item => item.Kind == MediaKind.WindowCapture);
            SetStatus(unlinked > 0 ? $"Evento abierto. {unlinked} captura(s) NO VINCULADA(S); agregá las ventanas nuevamente. Archivos faltantes: {missing}." : missing == 0 ? "Evento abierto." : $"Evento abierto. Hay {missing} archivo(s) que no se encuentran en su ruta guardada.");
            if (missing > 0)
                System.Windows.MessageBox.Show(this, $"El evento contiene {missing} archivo(s) local(es) que no están disponibles.\n\nLos recursos permanecerán en el evento, pero no podrán utilizarse hasta revincularlos. El resto del evento se abrió normalmente.",
                    "Recursos no encontrados", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { SetStatus($"No se pudo abrir el evento: {ex.Message}"); }
    }

    private static void RemoveLegacySectionItems(ObservableCollection<MediaItem> items)
    {
        // FIX1/FIX2 serialized the temporary Section kind as enum value 5.
        // Discard only those separators when loading so older FIX1/FIX2 events remain usable.
        for (var index = items.Count - 1; index >= 0; index--)
            if ((int)items[index].Kind == 5) items.RemoveAt(index);
    }
}

public sealed class ScreenChoice
{
    public WinForms.Screen Screen { get; }
    public string DeviceName => Screen.DeviceName;
    public string Label { get; }
    public ScreenChoice(WinForms.Screen screen, int index) { Screen = screen; Label = $"{screen.DeviceName.Replace(@"\\.\", "")} · {screen.Bounds.Width}×{screen.Bounds.Height}{(screen.Primary ? " · Principal" : "")}"; }
}

public sealed class LibraryBin { public string Name { get; set; } = "GENERAL"; public ObservableCollection<MediaItem> Items { get; set; } = new(); }
public sealed class EventProject { public MusicProjectData? Music { get; set; } public string Name { get; set; } = "Evento"; public string ActiveBinName { get; set; } = "GENERAL"; public List<LibraryBin>? Bins { get; set; } = new(); public List<MediaItem> Items { get; set; } = new(); }
public sealed class OperatorPreferences { public string FadeOutAudioHotkey { get; set; } = "F8"; }
