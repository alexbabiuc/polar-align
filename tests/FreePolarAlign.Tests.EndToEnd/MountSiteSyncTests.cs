using FreePolarAlign.Core.Engine;
using FreePolarAlign.Devices;
using FreePolarAlign.Devices.Simulated;
using FreePolarAlign.Session;
using Xunit;

namespace FreePolarAlign.Tests.EndToEnd;

/// <summary>
/// D35: the site can be copied from the mount and written to it, only when the
/// user asks, and the site in use is still the user's (D19).
/// </summary>
public class MountSiteSyncTests
{
    /// <summary>Half a degree north of the simulated mount's own site: thirty arcminutes of altitude bias if confused.</summary>
    private static readonly GeodeticLocation Elsewhere = new(SessionHarness.Latitude + 0.5, 15.25, 450.0);

    /// <summary>
    /// What "From mount" copies. The site is normally applied at startup, before
    /// any mount connects, so the mount's site has to be read at connection or
    /// it is never seen at all -- and nor was the disagreement, before D35.
    /// </summary>
    [Fact]
    public async Task ConnectingAMount_ReportsItsSite_AndTheDisagreementWithTheSiteAlreadyInUse()
    {
        using var harness = new SessionHarness();
        await harness.Session.SendAsync(new ConfigureSiteCommand(
            Elsewhere.LatitudeDegrees, Elsewhere.LongitudeDegrees, Elsewhere.HeightMeters));
        await harness.ConnectAsync();

        MountSiteReportedEvent? reported = harness.Recorder.Last<MountSiteReportedEvent>();
        Assert.True(reported is not null, harness.Recorder.Trail());
        Assert.Equal(SessionHarness.Site.LatitudeDegrees, reported!.LatitudeDegrees);
        Assert.Equal(SessionHarness.Site.LongitudeDegrees, reported.LongitudeDegrees);
        Assert.Equal(SessionHarness.Site.HeightMeters, reported.HeightMeters);
        Assert.False(reported.AfterWrite);
        Assert.Contains("latitude", reported.DisagreementWithSiteInUse, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WritingTheSiteInUseToTheMount_StoresIt_AndClearsTheDisagreement()
    {
        using var harness = new SessionHarness();
        await harness.ConnectAsync();
        await harness.Session.SendAsync(new ConfigureSiteCommand(
            Elsewhere.LatitudeDegrees, Elsewhere.LongitudeDegrees, Elsewhere.HeightMeters));
        Assert.NotNull(harness.Recorder.Last<SiteConfiguredEvent>()!.MountReportedDisagreement);

        await harness.Session.SendAsync(new WriteSiteToMountCommand(
            Elsewhere.LatitudeDegrees, Elsewhere.LongitudeDegrees, Elsewhere.HeightMeters));

        MountSiteReportedEvent written = harness.Recorder.Last<MountSiteReportedEvent>()!;
        Assert.True(written.AfterWrite, harness.Recorder.Trail());
        Assert.Null(written.WriteProblem);
        Assert.Null(written.DisagreementWithSiteInUse);
        Assert.Equal(Elsewhere, await harness.Simulated.GetSiteLocationAsync());
    }

    /// <summary>
    /// Writing changes the mount, not the site in use: only Save does that
    /// (D34), so a write never announces a new site.
    /// </summary>
    [Fact]
    public async Task WritingToTheMount_DoesNotChangeTheSiteInUse()
    {
        using var harness = new SessionHarness();
        await harness.ConnectAsync();
        await harness.ConfirmSiteAsync();
        int sitesBefore = harness.Recorder.All<SiteConfiguredEvent>().Count();

        await harness.Session.SendAsync(new WriteSiteToMountCommand(
            Elsewhere.LatitudeDegrees, Elsewhere.LongitudeDegrees, Elsewhere.HeightMeters));

        Assert.Equal(sitesBefore, harness.Recorder.All<SiteConfiguredEvent>().Count());
        Assert.Contains("latitude", harness.Recorder.Last<MountSiteReportedEvent>()!.DisagreementWithSiteInUse,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WritingToTheMountMidSequence_IsRefused_AndTheMountKeepsItsSite()
    {
        using var harness = new SessionHarness();
        await harness.ReadyAsync();

        await harness.Session.SendAsync(new WriteSiteToMountCommand(
            Elsewhere.LatitudeDegrees, Elsewhere.LongitudeDegrees, Elsewhere.HeightMeters));

        Assert.Contains("sequence is running", harness.Recorder.Last<CommandRejectedEvent>()?.Reason);
        Assert.Equal(SessionHarness.Site, await harness.Simulated.GetSiteLocationAsync());
    }

    /// <summary>
    /// ASCOM has no flag saying the site can be set, so refusal is found out by
    /// trying. It must arrive as a sentence, not as an exception out of the
    /// engine, and say what the mount holds instead.
    /// </summary>
    [Fact]
    public async Task ADriverThatRefusesTheWrite_IsReportedInWords_WithWhatTheMountStillHolds()
    {
        using var harness = new SessionHarness(siteWrites: SimulatedSiteWrites.Refused);
        await harness.ConnectAsync();
        await harness.ConfirmSiteAsync();

        await harness.Session.SendAsync(new WriteSiteToMountCommand(
            Elsewhere.LatitudeDegrees, Elsewhere.LongitudeDegrees, Elsewhere.HeightMeters));

        MountSiteReportedEvent reported = harness.Recorder.Last<MountSiteReportedEvent>()!;
        Assert.True(reported.AfterWrite, harness.Recorder.Trail());
        Assert.StartsWith("The mount did not accept the site:", reported.WriteProblem);
        Assert.Contains("not implemented", reported.WriteProblem);
        Assert.Contains("45.00000°", reported.WriteProblem);
        Assert.Equal(SessionHarness.Latitude, reported.LatitudeDegrees);
    }

    /// <summary>A driver that keeps a different value than it was given has not done what was asked, and says nothing.</summary>
    [Fact]
    public async Task ADriverThatKeepsADifferentValue_IsReported()
    {
        using var harness = new SessionHarness(siteWrites: SimulatedSiteWrites.StoredToWholeArcminutes);
        await harness.ConnectAsync();

        await harness.Session.SendAsync(new WriteSiteToMountCommand(44.4268, 26.1025, 85.0));

        MountSiteReportedEvent reported = harness.Recorder.Last<MountSiteReportedEvent>()!;
        Assert.Equal(44.4333, reported.LatitudeDegrees, precision: 4);
        Assert.Contains("kept 44.43333°", reported.WriteProblem);
    }

    [Fact]
    public async Task AnAltitudeOutsideAscomsRange_IsRefusedBeforeReachingTheMount()
    {
        using var harness = new SessionHarness();
        await harness.ConnectAsync();

        await harness.Session.SendAsync(new WriteSiteToMountCommand(45.0, 15.0, 12000.0));

        Assert.Contains("10000 m", harness.Recorder.Last<CommandRejectedEvent>()?.Reason);
        Assert.Equal(SessionHarness.Site, await harness.Simulated.GetSiteLocationAsync());
    }
}
