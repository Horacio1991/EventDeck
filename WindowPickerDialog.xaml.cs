using System.Windows;
namespace EventDeck;
public partial class WindowPickerDialog : Window
{
    internal WindowChoice? SelectedWindow => WindowsList.SelectedItem as WindowChoice;
    public WindowPickerDialog() { InitializeComponent(); Refresh(); }
    private void Refresh() => WindowsList.ItemsSource = WindowCatalog.List();
    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedWindow is { } window && WindowCatalog.Exists(window)) DialogResult = true;
        else Refresh();
    }
}
