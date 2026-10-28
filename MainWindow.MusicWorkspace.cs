using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using ComboBox = System.Windows.Controls.ComboBox;

namespace EventDeck;

public partial class MainWindow
{
    private MusicNavigation? _musicNavigation;
    private readonly MusicLibrary _musicLibrary = new();
    private ICollectionView? _musicLibraryView;
    private bool _refreshingMusicFilter;
    private bool _musicWorkspaceReady;
    private bool _musicWorkspaceVisible;
    private AudioTrack? _displayedMusicTrack;

    private void InitializeMusicWorkspace()
    {
        _musicLibraryView = CollectionViewSource.GetDefaultView(_musicLibrary.Entries);
        _musicLibraryView.Filter = item => item is MusicLibraryEntry entry && entry.Matches(MusicSearchBox.Text);
        MusicLibraryList.ItemsSource = _musicLibraryView;
        _musicNavigation = new MusicNavigation(_music, () => _musicLibraryView.Cast<MusicLibraryEntry>().ToList());
        _musicNavigation.Navigated += (_, entry) =>
        {
            _refreshingMusicFilter = true;
            try { MusicLibraryList.SelectedItem = entry; MusicLibraryList.ScrollIntoView(entry); }
            finally { _refreshingMusicFilter = false; }
        };
        Closing += (_, _) => { _musicNavigation.Dispose(); _musicLibrary.Dispose(); };
        _musicWorkspaceReady = true;
        _music.PreparedChanged += (_, _) => RefreshMusicWorkspace();
        _music.StateChanged += (_, _) => RefreshMusicWorkspace();
        RefreshMusicWorkspace();
    }

    private void Workspace_Click(object sender, RoutedEventArgs e)
    {
        if (!_musicWorkspaceReady || sender is not FrameworkElement { Tag: string workspace }) return;
        _musicWorkspaceVisible = workspace == "Music";
        _libraryNavigationActive = false;
        MusicTab.IsChecked = _musicWorkspaceVisible;
        OperationTab.IsChecked = !_musicWorkspaceVisible;
        OperationViewport.Visibility = _musicWorkspaceVisible ? Visibility.Collapsed : Visibility.Visible;
        MusicViewport.Visibility = _musicWorkspaceVisible ? Visibility.Visible : Visibility.Collapsed;
        // The native operator mirror is not clipped by WPF view visibility.
        // This never hides/closes the public OutputWindow or touches its session.
        if (_musicWorkspaceVisible) _output?.HideProgramMirror();
        else Dispatcher.BeginInvoke(new Action(UpdateProgramMonitorSurface));
    }

    private void RefreshMusicWorkspace()
    {
        // The existing engine can retain multiple tracks. The music console shows
        // the latest launched track; the original list still exposes every track.
        var track = _music.PlayingTracks.LastOrDefault();
        if (!ReferenceEquals(track, _displayedMusicTrack))
        {
            _displayedMusicTrack = track;
            MusicNowPanel.DataContext = track;
        }
        MusicNowPanel.IsEnabled = track is not null;
        MusicFadeOut.IsEnabled = _music.PlayingCount > 0 || _music.IsPlayPending;
        MusicPrevious.IsEnabled = _musicNavigation?.Neighbor(-1) is not null;
        MusicNext.IsEnabled = _musicNavigation?.Neighbor(1) is not null;
        MusicLoop.IsEnabled = track is not null;
        MusicLoop.IsChecked = track is not null && _music.IsLooping(track);
        MusicSeek.IsEnabled = track is { DurationSeconds: > 0 };
        MusicActiveCount.Text = $"{_music.PlayingCount} sonando";
    }


    private async void MusicAdd_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Agregar música", Multiselect = true,
            Filter = "Audio local|*.mp3;*.wav;*.wma;*.m4a;*.aac"
        };
        if (dialog.ShowDialog(this) != true) return;
        _musicLibrary.Add(dialog.FileNames);
        RefreshMusicLibraryView();
        await _musicLibrary.ReadPendingDurationsAsync();
    }

    private void MusicSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_musicWorkspaceReady) RefreshMusicLibraryView();
    }

    private void RefreshMusicLibraryView()
    {
        // Filtering may remove the selected row; that must not prepare another song.
        _refreshingMusicFilter = true;
        try { _musicLibraryView?.Refresh(); }
        finally { _refreshingMusicFilter = false; }
        MusicLibraryHint.Text = _musicLibrary.Entries.Count == 0
            ? "Agregá archivos de audio. Esta biblioteca es temporal y no se guarda con el evento."
            : "No hay coincidencias para esta búsqueda.";
        MusicLibraryHint.Visibility = _musicLibraryView?.IsEmpty != false ? Visibility.Visible : Visibility.Collapsed;
        RefreshMusicWorkspace();
    }

    private void MusicLibrary_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_musicWorkspaceReady || _refreshingMusicFilter || MusicLibraryList.SelectedItem is not MusicLibraryEntry entry) return;
        // Preparation only. The operator explicitly launches using the existing PLAY.
        // Never select an Operation resource or touch its PREVIEW/PROGRAM state.
        _musicNavigation!.Prepare(entry, PreparedAudioVolume.Value, _previewMuted);
    }

    private void HandleMusicEnter(KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        // Text entry and controls with native Enter behavior retain their own action.
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox or ButtonBase or ComboBox or Slider) return;
        e.Handled = true;
        if (!e.IsRepeat && _music.PreparedTrack is not null) PlayPreparedAudio();
    }

    private void MusicPrevious_Click(object sender, RoutedEventArgs e) => MoveMusic(-1);
    private void MusicNext_Click(object sender, RoutedEventArgs e) => MoveMusic(1);
    private void MoveMusic(int direction)
    {
        _musicNavigation?.Move(direction, _displayedMusicTrack?.Volume ?? PreparedAudioVolume.Value);
        RefreshMusicWorkspace();
    }
    private void MusicLoop_Click(object sender, RoutedEventArgs e)
    {
        if (_displayedMusicTrack is { } track) _music.SetLoop(track, MusicLoop.IsChecked == true);
    }
    private void MusicAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_musicNavigation is not null) _musicNavigation.AutoPlay = MusicAuto.IsChecked == true;
    }

    private void MusicSeek_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            CommitMusicSeek();
    }
    private void MusicSeek_MouseUp(object sender, MouseButtonEventArgs e) => CommitMusicSeek();
    private void CommitMusicSeek()
    {
        if (_displayedMusicTrack is { } track) _music.SeekTrack(track, MusicSeek.Value);
    }

    private void WorkspaceHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_musicWorkspaceReady) UpdateProgramMonitorSurface();
    }

    private void OperationViewport_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_musicWorkspaceReady) UpdateProgramMonitorSurface();
    }

    private bool IsProgramMonitorInsideWorkspace()
    {
        if (!_musicWorkspaceReady || _musicWorkspaceVisible || !OperationViewport.IsVisible || !ProgramMonitorFrame.IsLoaded) return false;
        var bounds = ProgramMonitorFrame.TransformToAncestor(WorkspaceHost)
            .TransformBounds(new Rect(0, 0, ProgramMonitorFrame.ActualWidth, ProgramMonitorFrame.ActualHeight));
        // Avoid a native child mirror covering navigation or adjacent controls
        // when the operator scrolls the original view on a short display.
        return bounds.Left >= 0 && bounds.Top >= 0
            && bounds.Right <= WorkspaceHost.ActualWidth && bounds.Bottom <= WorkspaceHost.ActualHeight;
    }
}
