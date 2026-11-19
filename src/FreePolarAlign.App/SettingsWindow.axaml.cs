using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FreePolarAlign.App;

/// <summary>Holds no logic: see <see cref="ViewModels.SettingsViewModel"/>.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OnSiteLostFocus(object? sender, RoutedEventArgs e) =>
        (DataContext as ViewModels.SettingsViewModel)?.NormaliseSiteText();
}
