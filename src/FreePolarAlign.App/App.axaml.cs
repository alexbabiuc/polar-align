using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FreePolarAlign.App.Services;
using FreePolarAlign.App.ViewModels;

namespace FreePolarAlign.App;

public sealed partial class App : Application
{
    private EngineStartupResult? _startup;
    private MainWindowViewModel? _viewModel;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Built here rather than at Main() so a bad startup (a missing
            // quad database, a catalogue that failed to load) still lands us
            // inside the Avalonia lifetime and shows a window explaining what
            // happened, rather than dying to an unhandled exception before any
            // UI exists (see EngineFactory.Create's own doc comment).
            _startup = EngineFactory.Create();

            _viewModel = new MainWindowViewModel(
                _startup.Engine,
                _startup.Warning,
                _startup.DefaultConfiguration,
                postToUiThread: action => Dispatcher.UIThread.Post(action));

            var window = new MainWindow { DataContext = _viewModel };
            desktop.MainWindow = window;
            desktop.ShutdownRequested += OnShutdownRequested;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        _viewModel?.Dispose();

        if (_startup is not null)
        {
            foreach (IDisposable disposable in _startup.OwnedDisposables)
            {
                disposable.Dispose();
            }
        }
    }
}
