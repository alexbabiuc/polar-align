using FreePolarAlign.App.ViewModels;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Session;
using FreePolarAlign.Tests.Support;
using Xunit;

namespace FreePolarAlign.Tests.App;

/// <summary>
/// The log panel opens as it was last left: hidden by someone who wanted the
/// room for the frame, it stays hidden on the next start.
/// </summary>
public class LogPanelTests
{
    private sealed class InMemorySettingsStore : ISettingsStore
    {
        public InMemorySettingsStore(AppSettings initial) => Saved = initial;

        public AppSettings Saved { get; private set; }

        public string Location => "(in memory)";

        public SettingsLoadResult Load() => new(Saved, null);

        public string? Save(AppSettings settings)
        {
            Saved = settings;
            return null;
        }
    }

    private static MainWindowViewModel Build(InMemorySettingsStore store) =>
        new(
            engine: null,
            catalog: null,
            settingsStore: store,
            settings: store.Saved,
            log: null,
            warnings: null,
            new SessionConfiguration(6, 70.0), new TestClock());

    [Fact]
    public void Settings_written_before_the_toggle_existed_show_the_log()
    {
        var viewModel = Build(new InMemorySettingsStore(AppSettings.Empty));

        Assert.True(viewModel.IsLogVisible);
    }

    [Fact]
    public void A_hidden_log_stays_hidden_on_the_next_start()
    {
        var store = new InMemorySettingsStore(AppSettings.Empty);
        Build(store).IsLogVisible = false;

        Assert.True(store.Saved.IsLogHidden);
        Assert.False(Build(store).IsLogVisible);
    }

    [Fact]
    public void A_log_shown_again_is_shown_on_the_next_start()
    {
        var store = new InMemorySettingsStore(AppSettings.Empty with { IsLogHidden = true });
        Build(store).IsLogVisible = true;

        Assert.True(Build(store).IsLogVisible);
    }

    [Fact]
    public void The_choice_survives_the_settings_file()
    {
        string path = Path.Combine(Path.GetTempPath(), $"fpa-log-{Guid.NewGuid():N}.json");
        try
        {
            var file = new JsonFileSettingsStore(path);
            Assert.Null(file.Save(AppSettings.Empty with { IsLogHidden = true }));

            Assert.True(file.Load().Settings.IsLogHidden);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
