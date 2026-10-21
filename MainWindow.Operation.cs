using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace EventDeck;

public partial class MainWindow
{
    private async Task ToggleBlackAsync()
    {
        if (_output is not { IsOpen: true } output) { SetStatus("Abrí la salida para usar BLACK."); return; }
        if (_takeInProgress) { SetStatus("Esperá a que termine el envío actual."); return; }
        _takeInProgress = true;
        try
        {
            await output.ToggleBlackAsync();
            RefreshProgramControls();
            SetStatus(output.Snapshot.IsBlackActive
                ? "BLACK ACTIVO · salida cubierta. Program y audio continúan; pulsá BLACK para mostrar Program."
                : "BLACK DESACTIVADO · Program visible en la salida.");
        }
        catch (Exception ex) { SetStatus("No se pudo cambiar BLACK: " + ex.Message); }
        finally { _takeInProgress = false; }
    }

    private void ScreenCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshOutputStatus();

    private void RefreshOutputStatus()
    {
        if (OutputStatusText is null || SelectedScreenText is null || BlackButton is null) return;
        var open = _output?.IsOpen == true;
        var snapshot = _output?.Snapshot;
        var black = open && snapshot?.IsBlackActive == true;
        BlackButton.IsEnabled = open;
        BlackButton.Content = black ? "BLACK · ACTIVO" : "BLACK · OFF";
        BlackButton.Background = black ? Brushes.Firebrick : (Brush)new SolidColorBrush(Color.FromRgb(86, 42, 50));
        BlackButton.FontWeight = black ? FontWeights.Bold : FontWeights.Normal;
        var display = snapshot?.DisplayDevice?.Replace(@"\\.\", "");
        OutputStatusText.Text = !open ? "SALIDA INACTIVA"
            : snapshot?.IsDisplaySuspended == true ? "SALIDA SUSPENDIDA · esperando el monitor original"
            : $"SALIDA ACTIVA{(snapshot!.IsTestMode ? " · PRUEBA EN VENTANA" : "")} · " +
              (display is not null && snapshot.DisplayWidth > 0 && snapshot.DisplayHeight > 0
                  ? $"{display} · {snapshot.DisplayWidth}×{snapshot.DisplayHeight}"
                  : "pantalla pendiente de identificar") +
              (black ? " · BLACK ACTIVO" : snapshot.Item is null ? " · SIN PROGRAM (NEGRO)" : " · BLACK OFF");
        OutputStatusText.Foreground = open && snapshot?.IsDisplaySuspended == true ? Brushes.Goldenrod : black ? Brushes.Salmon : open ? Brushes.LightGreen : Brushes.LightGray;
        SelectedScreenText.Text = ScreenCombo.SelectedItem is ScreenChoice selected
            ? $"{(open ? "Próxima apertura (no mueve la salida actual)" : "Destino seleccionado")}: {selected.Label}"
            : "Sin pantalla seleccionada";
    }
}
