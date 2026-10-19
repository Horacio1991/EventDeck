using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfMessageBox = System.Windows.MessageBox;
using WpfItemsControl = System.Windows.Controls.ItemsControl;
using WpfListBoxItem = System.Windows.Controls.ListBoxItem;

namespace EventDeck;

public partial class MainWindow
{
    private bool _eventManagementReady;
    private ICollectionView? _resourceView;
    private string _resourceSearch = "";
    private string _resourceKindFilter = "ALL";
    private System.Windows.Point _resourceDragStart;
    private MediaItem? _resourceDragCandidate;

    private void AttachResourceView()
    {
        _resourceView = CollectionViewSource.GetDefaultView(_items);
        _resourceView.Filter = MatchesResourceFilter;
        LibraryList.ItemsSource = _resourceView;
        RefreshResourceView();
    }

    private bool MatchesResourceFilter(object candidate)
    {
        if (candidate is not MediaItem item) return false;
        return MatchesResource(item);
    }

    private bool MatchesResource(MediaItem item)
    {
        if (!string.IsNullOrWhiteSpace(_resourceSearch) &&
            !item.Name.Contains(_resourceSearch, StringComparison.CurrentCultureIgnoreCase)) return false;
        return _resourceKindFilter switch
        {
            "IMAGE" => item.Kind == MediaKind.Image,
            "VIDEO" => item.Kind == MediaKind.Video,
            "AUDIO" => item.Kind == MediaKind.Audio,
            "CAPTURE" => item.Kind == MediaKind.WindowCapture,
            "WEB" => item.Kind == MediaKind.Web,
            _ => true
        };
    }

    private void RefreshResourceView()
    {
        _resourceView?.Refresh();
        if (LibraryList.SelectedItem is MediaItem selected && _resourceView?.Contains(selected) != true)
        {
            LibraryList.SelectedItem = null;
            _selected = null;
        }
        UpdateResourceCountLabel();
        RefreshEventHealth();
    }

    private void ResourceSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_eventManagementReady) return;
        _resourceSearch = ResourceSearchBox.Text.Trim();
        RefreshResourceView();
    }

    private void ResourceFilter_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_eventManagementReady) return;
        if (ResourceKindFilter?.SelectedValue is string value)
        {
            _resourceKindFilter = value;
            RefreshResourceView();
        }
    }

    private void UpdateResourceCountLabel()
    {
        if (ItemCount is null) return;
        var total = _items.Count;
        var visible = _resourceView?.Cast<object>().OfType<MediaItem>().Count() ?? total;
        ItemCount.Text = visible == total ? $"  {total} recursos" : $"  {visible} / {total} recursos";
    }

    private void RefreshEventHealth()
    {
        if (EventHealthText is null) return;
        var missing = _bins.SelectMany(bin => bin.Items).Count(item => item.IsMissing);
        if (missing == 0)
        {
            EventHealthText.Text = "✓ EVENTO LISTO";
            EventHealthText.Foreground = System.Windows.Media.Brushes.LightGreen;
            EventHealthText.ToolTip = "No se detectan archivos locales faltantes.";
        }
        else
        {
            EventHealthText.Text = $"⚠ {missing} RECURSO{(missing == 1 ? "" : "S")} FALTANTE{(missing == 1 ? "" : "S")}";
            EventHealthText.Foreground = System.Windows.Media.Brushes.Orange;
            EventHealthText.ToolTip = "Hay archivos locales que requieren revinculación.";
        }
        RelinkResourceButton.IsEnabled = LibraryList.SelectedItem is MediaItem { IsMissing: true };
    }

    private void RelinkResource_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryList.SelectedItem is not MediaItem { IsMissing: true } item) return;
        var filter = item.Kind switch
        {
            MediaKind.Image => "Imágenes|*.png;*.jpg;*.jpeg;*.bmp|Todos los archivos|*.*",
            MediaKind.Video => "Videos|*.mp4;*.m4v;*.wmv;*.avi|Todos los archivos|*.*",
            MediaKind.Audio => "Audio|*.mp3;*.wav;*.wma;*.m4a;*.aac|Todos los archivos|*.*",
            _ => "Todos los archivos|*.*"
        };
        var dialog = new OpenFileDialog { Title = "Revincular archivo", Filter = filter, FileName = Path.GetFileName(item.Source) };
        var directory = Path.GetDirectoryName(item.Source);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)) dialog.InitialDirectory = directory;
        if (dialog.ShowDialog() != true) return;
        var replacementKind = GetMediaKind(dialog.FileName);
        if (replacementKind != item.Kind)
        {
            WpfMessageBox.Show(this, $"El archivo elegido es de tipo {replacementKind?.ToString() ?? "no compatible"}; este recurso requiere {item.Kind}.",
                "Tipo de archivo incompatible", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var wasSelected = ReferenceEquals(LibraryList.SelectedItem, item);
        var itemIndex = _items.IndexOf(item);
        if (itemIndex < 0) return;
        _items.RemoveAt(itemIndex);
        item.Source = dialog.FileName;
        _items.Insert(itemIndex, item);
        UpdateCount();
        if (wasSelected)
        {
            LibraryList.SelectedItem = null;
            LibraryList.SelectedItem = item;
        }
        SetStatus($"Archivo revinculado: {Path.GetFileName(item.Source)}.");
    }

    private static MediaKind? GetMediaKind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".bmp" => MediaKind.Image,
        ".mp4" or ".m4v" or ".wmv" or ".avi" => MediaKind.Video,
        ".mp3" or ".wav" or ".wma" or ".m4a" or ".aac" => MediaKind.Audio,
        _ => null
    };

    private void ResourceList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _resourceDragStart = e.GetPosition(LibraryList);
        _resourceDragCandidate = (e.OriginalSource as DependencyObject) is { } source &&
            WpfItemsControl.ContainerFromElement(LibraryList, source) is WpfListBoxItem container
                ? container.Content as MediaItem : null;
    }

    private void ResourceList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _resourceDragCandidate is not { } item) return;
        var position = e.GetPosition(LibraryList);
        if (Math.Abs(position.X - _resourceDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _resourceDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var data = new System.Windows.DataObject("EventDeck.ResourceItem", item);
        _resourceDragCandidate = null;
        System.Windows.DragDrop.DoDragDrop(LibraryList, data, System.Windows.DragDropEffects.Move);
    }

    private MediaItem? GetResourceAtPoint(System.Windows.DragEventArgs e) =>
        (e.OriginalSource as DependencyObject) is { } source &&
        WpfItemsControl.ContainerFromElement(LibraryList, source) is WpfListBoxItem container
            ? container.Content as MediaItem : null;

    private void ReorderResource(MediaItem dragged, MediaItem? target)
    {
        var oldIndex = _items.IndexOf(dragged);
        if (oldIndex < 0) return;
        var targetIndex = target is null ? _items.Count : _items.IndexOf(target);
        if (targetIndex < 0 || targetIndex == oldIndex) return;
        var newIndex = targetIndex > oldIndex ? targetIndex - 1 : targetIndex;
        if (newIndex == oldIndex) return;
        _items.Move(oldIndex, newIndex);
        RefreshResourceView();
        LibraryList.SelectedItem = dragged;
    }
}
