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
    private readonly MusicCollections _musicCollections = new();
    private MusicLibrary _musicLibrary => _musicCollections.Active.Library;
    private bool _changingMusicCollection;
    private ICollectionView? _musicLibraryView;
    private bool _refreshingMusicFilter;
    private bool _musicWorkspaceReady;
    private bool _musicWorkspaceVisible;
    private AudioTrack? _displayedMusicTrack;

    private void InitializeMusicWorkspace()
    {
        _musicNavigation = new MusicNavigation(_music, () => Array.Empty<MusicLibraryEntry>());
        _musicNavigation.Navigated += (_, entry) =>
        {
            if (!_musicLibrary.Entries.Contains(entry)) return;
            _refreshingMusicFilter = true;
            try { MusicLibraryList.SelectedItem = entry; MusicLibraryList.ScrollIntoView(entry); }
            finally { _refreshingMusicFilter = false; }
        };
        Closing += (_, _) => { _musicNavigation.Dispose(); _musicCollections.Dispose(); };
        MusicCollectionTabs.ItemsSource = _musicCollections.Items;
        _musicWorkspaceReady = true;
        AttachMusicCollection();
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
        MusicSequenceContext.Text = _musicNavigation?.ContextName is { } context ? $"SECUENCIA · {context}" : "";
    }


    private async void MusicAdd_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Agregar música", Multiselect = true,
            Filter = "Audio local|*.mp3;*.wav;*.wma;*.m4a;*.aac"
        };
        if (dialog.ShowDialog(this) != true) return;
        var library = _musicLibrary;
        library.Add(dialog.FileNames);
        RefreshMusicLibraryView();
        await library.ReadPendingDurationsAsync();
    }

    private void MusicSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_musicWorkspaceReady && !_changingMusicCollection) RefreshMusicLibraryView();
    }

    private void RefreshMusicLibraryView()
    {
        // Filtering may remove the selected row; that must not prepare another song.
        foreach (var entry in _musicLibrary.Entries) entry.RefreshAvailability();
        _refreshingMusicFilter = true;
        try { _musicLibraryView?.Refresh(); }
        finally { _refreshingMusicFilter = false; }
        MusicLibraryHint.Text = _musicLibrary.Entries.Count == 0
            ? "Agregá archivos de audio a esta colección. Se guardará junto con el evento."
            : "No hay coincidencias para esta búsqueda.";
        MusicLibraryHint.Visibility = _musicLibraryView?.IsEmpty != false ? Visibility.Visible : Visibility.Collapsed;
        RefreshMusicWorkspace();
    }

    private void MusicLibrary_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_musicWorkspaceReady || _changingMusicCollection || _refreshingMusicFilter || MusicLibraryList.SelectedItem is not MusicLibraryEntry entry) return;
        // Preparation only. The operator explicitly launches using the existing PLAY.
        // Never select an Operation resource or touch its PREVIEW/PROGRAM state.
        entry.RefreshAvailability();
        _musicNavigation!.Prepare(entry, PreparedAudioVolume.Value, _previewMuted, _musicCollections.Active);
        RefreshMusicWorkspace();
    }

    private void AttachMusicCollection()
    {
        _changingMusicCollection = true;
        try
        {
            MusicSearchBox.Text = "";
            MusicLibraryList.SelectedItem = null;
            _musicLibraryView = CollectionViewSource.GetDefaultView(_musicLibrary.Entries);
            _musicLibraryView.Filter = item => item is MusicLibraryEntry entry && entry.Matches(MusicSearchBox.Text);
            MusicLibraryList.ItemsSource = _musicLibraryView;
            MusicCollectionTabs.SelectedItem = _musicCollections.Active;
        }
        finally { _changingMusicCollection = false; }
        RefreshMusicLibraryView();
        _ = _musicLibrary.ReadPendingDurationsAsync();
    }

    private void MusicCollection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_musicWorkspaceReady || _changingMusicCollection || MusicCollectionTabs.SelectedItem is not MusicCollection collection) return;
        _musicCollections.Select(collection);
        AttachMusicCollection();
    }

    private void MusicCollection_Add(object sender, RoutedEventArgs e)
    {
        var dialog = new BinNameDialog("Nueva colección musical") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _changingMusicCollection = true;
        try { _musicCollections.Create(dialog.BinName); }
        finally { _changingMusicCollection = false; }
        AttachMusicCollection();
    }

    private void MusicCollection_Rename(object sender, RoutedEventArgs e)
    {
        var collection = _musicCollections.Active;
        var dialog = new BinNameDialog("Renombrar colección musical", collection.Name) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        collection.Name = dialog.BinName;
        RefreshMusicWorkspace();
    }

    private void MusicCollection_Delete(object sender, RoutedEventArgs e)
    {
        var collection = _musicCollections.Active;
        if (collection.Library.Entries.Count > 0 && System.Windows.MessageBox.Show(this,
            $"¿Eliminar la colección ‘{collection.Name}’ y sus referencias musicales?\nLos archivos originales y la canción SONANDO se conservan.",
            "Eliminar colección musical", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _changingMusicCollection = true;
        try { _musicCollections.Remove(collection); }
        finally { _changingMusicCollection = false; }
        AttachMusicCollection();
    }

    private void RestoreMusicProject(MusicProjectData? data)
    {
        // OpenProject already resets MusicSession. Restore organization only, never playback.
        _musicNavigation!.Reset();
        MusicAuto.IsChecked = false;
        MusicLoop.IsChecked = false;
        _changingMusicCollection = true;
        try { _musicCollections.Restore(data); }
        finally { _changingMusicCollection = false; }
        AttachMusicCollection();
    }

    private void HandleMusicKeyDown(KeyEventArgs e)
    {
        if (!_musicWorkspaceVisible || e.Handled) return;
        if (e.Key is Key.Left or Key.Right)
        {
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            var focus = Keyboard.FocusedElement;
            // Opt in only controls without native horizontal navigation.
            // Text editors, sliders (including their thumbs), collection tabs,
            // toggles and any unknown controls keep their own arrow behavior.
            bool navigationFocus = ReferenceEquals(focus, this)
                || ReferenceEquals(focus, MusicLibraryList)
                || focus is System.Windows.Controls.Button
                || focus is ListBoxItem item
                    && ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), MusicLibraryList);
            if (!navigationFocus) return;
            e.Handled = true;
            if (!e.IsRepeat) MoveMusic(e.Key == Key.Left ? -1 : 1);
            return;
        }
        HandleMusicEnter(e);
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
