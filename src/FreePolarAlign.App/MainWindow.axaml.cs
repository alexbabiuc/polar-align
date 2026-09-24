using Avalonia.Controls;
using Avalonia.Threading;
using FreePolarAlign.App.ViewModels;

namespace FreePolarAlign.App;

/// <summary>
/// The main window holds no decisions -- every one of them lives in
/// <see cref="MainWindowViewModel"/>, which is testable, while this file is not.
///
/// The one thing it does own is the clock. The mount is polled from here rather
/// than from a timer inside the engine: the engine times its own settle delays
/// (D26), but it learns that a hand-controller slew has ended only from these
/// polls, and a test can drive them without a scheduler.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// How often the mount is asked where it is. Fast enough that the readout
    /// does not look frozen while the telescope is tracking, slow enough not to
    /// flood an ASCOM driver whose every property read is an out-of-process COM
    /// call.
    /// </summary>
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often the sampling countdown is redrawn. Once a second because it
    /// counts in whole seconds; the settle delay it most often shows is two.
    /// </summary>
    private static readonly TimeSpan ClockInterval = TimeSpan.FromSeconds(1);

    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _clockTimer;

    public MainWindow()
    {
        InitializeComponent();

        _statusTimer = new DispatcherTimer { Interval = StatusPollInterval };
        _statusTimer.Tick += OnStatusTick;
        _statusTimer.Start();

        _clockTimer = new DispatcherTimer { Interval = ClockInterval };
        _clockTimer.Tick += OnClockTick;
        _clockTimer.Start();
    }

    private void OnStatusTick(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            // Fire and forget on purpose: a poll that overruns its interval has
            // nothing useful to report, and awaiting it here would either block
            // the UI thread or need a queue of stale requests.
            _ = viewModel.RefreshMountStatusAsync();
        }
    }

    private void OnClockTick(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.OnClockTick();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTick;
        _clockTimer.Stop();
        _clockTimer.Tick -= OnClockTick;
        base.OnClosed(e);
    }
}
