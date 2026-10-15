using System.Windows;

namespace EventDeck;

public partial class WebResourceDialog : Window
{
    public string ResourceName => NameBox.Text;
    public string Url => UrlBox.Text;
    public WebResourceDialog() => InitializeComponent();
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Add_Click(object sender, RoutedEventArgs e) { if (string.IsNullOrWhiteSpace(UrlBox.Text)) { System.Windows.MessageBox.Show(this, "Ingresá una URL.", "EventDeck", MessageBoxButton.OK, MessageBoxImage.Information); return; } DialogResult = true; }
}
