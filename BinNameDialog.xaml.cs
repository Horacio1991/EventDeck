using System.Windows;

namespace EventDeck;

public partial class BinNameDialog : Window
{
    public string BinName => NameBox.Text.Trim();
    public BinNameDialog(string title, string initial = "")
    { InitializeComponent(); Title = title; NameBox.Text = initial; Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); }; }
    private void Accept_Click(object sender, RoutedEventArgs e)
    { if (string.IsNullOrWhiteSpace(BinName)) { System.Windows.MessageBox.Show(this, "Escribí un nombre para el bin.", "EventDeck", MessageBoxButton.OK, MessageBoxImage.Information); return; } DialogResult = true; }
}
