using Avalonia.Controls;

namespace FreePolarAlign.App;

/// <summary>
/// The main window is deliberately code-free beyond loading its markup: every
/// decision the UI makes lives in <see cref="ViewModels.MainWindowViewModel"/>,
/// which is testable, while this file is not.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
