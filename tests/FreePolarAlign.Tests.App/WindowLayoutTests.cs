using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using FreePolarAlign.App.ViewModels;
using FreePolarAlign.Core.Engine;
using Xunit;
using AppShell = FreePolarAlign.App.App;
using MainWindow = FreePolarAlign.App.MainWindow;

namespace FreePolarAlign.Tests.App;

/// <summary>
/// The window's layout, rendered off-screen at the sizes it actually has to
/// work at.
///
/// This exists because of a defect no other test here could have caught. The
/// captured-frame panel asked for a 220px minimum height inside a row that was
/// given whatever height was left over, and on a short window -- any window
/// with the install warnings banner showing -- what was left over was less than
/// that. It did not shrink and it did not scroll: it drew itself across the
/// panels above and below, with their text over the top of it. Every existing
/// test passed, because every existing test checks what the view model decides
/// rather than where the controls end up.
///
/// So the invariant asserted here is the one that was broken, stated
/// structurally rather than in pixels: a panel stays inside the space it was
/// given, and keeps enough of it to be worth having. Nothing here checks how
/// anything looks.
/// </summary>
public class WindowLayoutTests
{
    /// <summary>
    /// One Avalonia session for the whole class, since the toolkit can only be
    /// initialised once per process and every test here needs a UI thread to
    /// build a window on.
    ///
    /// The real <see cref="AppShell"/> is used rather than a stub, for the
    /// Fluent theme the window's controls are styled against -- sizes come from
    /// control templates, so a window laid out without them would not be the
    /// window. It builds no engine and touches no files: it only does that when
    /// it finds a desktop lifetime, and a headless session is not one.
    /// </summary>
    private static readonly HeadlessUnitTestSession Session =
        HeadlessUnitTestSession.StartNew(typeof(WindowLayoutTests));

    /// <summary>
    /// Found by name by the headless session. Skia and real drawing rather than
    /// the headless stub: control heights come out of text measurement, and the
    /// stub shaper's differ from the real ones -- measured, by enough to turn a
    /// 67px picture into a 0px one, which would make every floor asserted below
    /// meaningless.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<AppShell>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <summary>
    /// Runs a test body on the session's UI thread. Avalonia controls have
    /// thread affinity, so every line that touches one has to go through here.
    /// </summary>
    private static void OnUiThread(Action body) =>
        Session.Dispatch(
                () =>
                {
                    body();
                    return Task.CompletedTask;
                },
                CancellationToken.None)
            .GetAwaiter().GetResult();

    /// <summary>An engine that never says anything, so the window lays out in its initial state.</summary>
    private sealed class SilentEngine : IAlignmentEngine
    {
        public IObservable<EngineEvent> Events { get; } = new NeverPublishes();

        public ValueTask SendAsync(EngineCommand command, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public void Dispose()
        {
        }

        private sealed class NeverPublishes : IObservable<EngineEvent>
        {
            public IDisposable Subscribe(IObserver<EngineEvent> observer) => new Nothing();

            private sealed class Nothing : IDisposable
            {
                public void Dispose()
                {
                }
            }
        }
    }

    /// <param name="withWarnings">
    /// The banner that pushed the original defect over the edge: an install
    /// warning (a plugin that would not load, a missing quad database) takes a
    /// permanent strip off the top of the window, and the user who reported this
    /// had two of them.
    /// </param>
    private static void WithWindow(double width, double height, bool withWarnings, Action<Window> assertions)
    {
        OnUiThread(() =>
        {
            var viewModel = new MainWindowViewModel(
                new SilentEngine(),
                catalog: null,
                settingsStore: null,
                settings: null,
                log: null,
                warnings: withWarnings
                    ? new[]
                    {
                        "'FreePolarAlign.Devices' does not contain any usable IDeviceProvider implementation.",
                        "'FreePolarAlign.Imaging' does not contain any usable IDeviceProvider implementation.",
                    }
                    : null,
                new SessionConfiguration(6, 70.0, TimeSpan.FromSeconds(2)),
                postToUiThread: action => action());

            var window = new MainWindow { DataContext = viewModel, Width = width, Height = height };

            try
            {
                window.Show();
                window.UpdateLayout();
                assertions(window);
            }
            finally
            {
                window.Close();
                viewModel.Dispose();
            }
        });
    }

    private static Grid Column(Window window) => window.FindControl<Grid>("MeasurementColumn")!;

    private static Border Panel(Window window) => window.FindControl<Border>("CapturedFramePanel")!;

    /// <summary>
    /// Nothing inside the captured-frame panel is drawn outside it.
    ///
    /// This is the assertion that catches the original defect, and the obvious
    /// one does not: the panel itself was always inside its column, because the
    /// grid arranged it into the row it was given. What overflowed was the
    /// panel's *content* -- the frame view demanded 220px inside a panel that
    /// had less, and neither shrank nor clipped, so it and everything docked
    /// below it were painted across the neighbouring panels. Checked against the
    /// whole subtree rather than against the frame view alone, since which
    /// control overflows is not the point.
    /// </summary>
    [Theory]
    [InlineData(1380.0, 880.0, false)]
    [InlineData(1380.0, 880.0, true)]
    [InlineData(1100.0, 720.0, false)]
    [InlineData(1100.0, 720.0, true)]
    public void NothingInTheCapturedFramePanelIsDrawnOutsideIt(double width, double height, bool withWarnings)
    {
        WithWindow(width, height, withWarnings, window =>
        {
            Border panel = Panel(window);
            Rect bounds = panel.Bounds;

            foreach (Control descendant in panel.GetLogicalDescendants().OfType<Control>())
            {
                if (!descendant.IsVisible || descendant.Bounds.Height <= 0.0)
                {
                    continue;
                }

                Point? offset = descendant.TranslatePoint(new Point(0, 0), panel);
                if (offset is not { } topLeft)
                {
                    continue;
                }

                double bottom = topLeft.Y + descendant.Bounds.Height;
                double right = topLeft.X + descendant.Bounds.Width;

                Assert.True(
                    topLeft.Y >= -0.5 && bottom <= bounds.Height + 0.5
                        && topLeft.X >= -0.5 && right <= bounds.Width + 0.5,
                    $"at {width}x{height} (warnings: {withWarnings}) a {descendant.GetType().Name} occupied " +
                    $"{topLeft.X:F0},{topLeft.Y:F0} to {right:F0},{bottom:F0} inside a panel only " +
                    $"{bounds.Width:F0}x{bounds.Height:F0}");
            }
        });
    }

    [Theory]
    [InlineData(1380.0, 880.0, false)]
    [InlineData(1380.0, 880.0, true)]
    [InlineData(1100.0, 720.0, false)]
    [InlineData(1100.0, 720.0, true)]
    public void TheCapturedFramePanelStaysInsideItsColumn(double width, double height, bool withWarnings)
    {
        WithWindow(width, height, withWarnings, window =>
        {
            Grid column = Column(window);
            Border panel = Panel(window);

            // Bounds are relative to the parent, which here is the column itself.
            Assert.True(
                panel.Bounds.Top >= -0.5 && panel.Bounds.Bottom <= column.Bounds.Height + 0.5,
                $"at {width}x{height} (warnings: {withWarnings}) the frame panel occupied " +
                $"{panel.Bounds.Top:F0}..{panel.Bounds.Bottom:F0} of a column {column.Bounds.Height:F0} tall");
        });
    }

    /// <summary>
    /// Nothing in the window scrolls horizontally, so a panel wider than its
    /// column is the same defect in the other direction.
    /// </summary>
    [Theory]
    [InlineData(1380.0, 880.0)]
    [InlineData(1100.0, 720.0)]
    public void TheCapturedFramePanelStaysInsideItsColumnHorizontally(double width, double height)
    {
        WithWindow(width, height, withWarnings: true, window =>
        {
            Grid column = Column(window);
            Border panel = Panel(window);

            Assert.True(
                panel.Bounds.Left >= -0.5 && panel.Bounds.Right <= column.Bounds.Width + 0.5,
                $"at {width}x{height} the frame panel spanned {panel.Bounds.Left:F0}..{panel.Bounds.Right:F0} " +
                $"of a column {column.Bounds.Width:F0} wide");
        });
    }

    /// <summary>
    /// And it is still a picture: the frame view keeps a usable share of the
    /// panel rather than being squeezed to a sliver by the controls around it.
    ///
    /// Containment on its own would be satisfied by a zero-height view, which is
    /// only a tidier way of showing the user nothing -- and is what the first
    /// attempt at this fix actually did at the window's minimum size. Both
    /// figures are floors rather than layout specifications, and both are
    /// measured with the warnings banner showing, since that is the case that
    /// squeezes hardest and the one the defect was reported from.
    ///
    /// The two numbers are far apart because the window's minimum size genuinely
    /// cannot fit everything: at 720px with two warning banners the frame view
    /// is small, and the honest answer for a user in that position is a taller
    /// window, not a rearranged one. At the default size it is a picture.
    /// </summary>
    [Theory]
    [InlineData(1380.0, 880.0, 200.0)]
    [InlineData(1100.0, 720.0, 80.0)]
    public void TheFrameViewKeepsRoomForAPicture(double width, double height, double floor)
    {
        WithWindow(width, height, withWarnings: true, window =>
        {
            Border panel = Panel(window);
            Image image = panel.GetLogicalDescendants().OfType<Image>().Single();
            var host = (Control)image.Parent!;

            Assert.True(
                host.Bounds.Height >= floor,
                $"at {width}x{height} the frame view was only {host.Bounds.Height:F0}px tall, under the {floor:F0}px floor");
        });
    }

    /// <summary>
    /// The brightness control stays reachable at every size. It is docked below
    /// the picture, so the way to lose it is for the picture to take the panel's
    /// whole height -- which is the same failure as the panel overflowing, seen
    /// from inside.
    /// </summary>
    [Theory]
    [InlineData(1380.0, 880.0)]
    [InlineData(1100.0, 720.0)]
    public void TheBrightnessControlStaysVisible(double width, double height)
    {
        WithWindow(width, height, withWarnings: true, window =>
        {
            Border panel = Panel(window);
            Slider slider = panel.GetLogicalDescendants().OfType<Slider>().Single();

            Assert.True(slider.Bounds.Height > 0.0, $"at {width}x{height} the brightness slider had no height");
            Assert.True(
                slider.Bounds.Bottom <= panel.Bounds.Height + 0.5,
                $"at {width}x{height} the brightness slider ended at {slider.Bounds.Bottom:F0} " +
                $"in a panel {panel.Bounds.Height:F0} tall");
        });
    }
}
