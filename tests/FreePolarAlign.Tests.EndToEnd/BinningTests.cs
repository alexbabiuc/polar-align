using FreePolarAlign.Core.Engine;
using FreePolarAlign.Imaging.Wcs;
using FreePolarAlign.Session;
using Xunit;

namespace FreePolarAlign.Tests.EndToEnd;

/// <summary>
/// D43: the engine bins frames itself, and the focal length it learns and the
/// scale it asks the solver for stay in the sensor's own pixels whatever the
/// binning. Get either wrong by the factor and every hint is off by two, which
/// with the 5% tolerance a measured focal length earns is no solve at all.
/// </summary>
public class BinningTests
{
    /// <summary>The stub camera's pixel at the perfect solver's 2″ a binned pixel, so 1″ a sensor pixel.</summary>
    private static readonly double SensorFocalLength = PlateScale.FocalLengthMillimetres(3.76, 1.0);

    [Fact]
    public async Task ABinnedFrame_TeachesTheFocalLengthOfTheSensorsPixels()
    {
        using var harness = new SessionHarness(binning: 2, realFrames: true);
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);

        Assert.Null(harness.Recorder.Last<FrameNotBinnedEvent>());
        Assert.Equal(SensorFocalLength, harness.Session.Profile!.FocalLengthMillimetres!.Value, 6);
    }

    [Fact]
    public async Task ABinnedFrame_IsSolvedAtTheBinnedScale()
    {
        using var harness = new SessionHarness(binning: 2, realFrames: true);
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);
        await harness.Session.SendAsync(new CaptureNextPointCommand());
        await harness.WaitForSampleAsync(2);

        // The first solve had no scale to go on; every one after it was hinted
        // from the focal length the first one measured.
        double?[] hints;
        lock (harness.Perfect.Requests)
        {
            hints = harness.Perfect.Requests.Skip(1).Select(r => r.ApproximateScaleArcsecPerPixel).ToArray();
        }

        Assert.NotEmpty(hints);
        Assert.All(hints, hint => Assert.Equal(2.0, hint!.Value, 6));
    }

    /// <summary>
    /// A frame that cannot be read back is used as it came, and counted as
    /// unbinned, so the focal length learned from it is the full-resolution one
    /// -- twice what a binned frame of the same scale gives.
    /// </summary>
    [Fact]
    public async Task AFrameThatCannotBeBinned_IsUsedAtFullResolution_AndSaysSo()
    {
        using var harness = new SessionHarness(binning: 2, realFrames: false);
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);

        Assert.NotNull(harness.Recorder.Last<FrameNotBinnedEvent>());
        Assert.Equal(PlateScale.FocalLengthMillimetres(3.76, 2.0), harness.Session.Profile!.FocalLengthMillimetres!.Value, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task ABinningOutsideOneToFour_IsRefused(int factor)
    {
        using var harness = new SessionHarness();
        await harness.ConnectAsync();

        await harness.Session.SendAsync(new SetBinningCommand(factor));

        Assert.NotNull(harness.Recorder.Last<CommandRejectedEvent>());
        Assert.NotEqual(factor, harness.Recorder.Last<BinningChangedEvent>()?.Factor);
    }

    [Fact]
    public async Task AChangedBinning_IsAnnounced()
    {
        using var harness = new SessionHarness();
        await harness.ConnectAsync();

        await harness.Session.SendAsync(new SetBinningCommand(2));

        Assert.Null(harness.Recorder.Last<CommandRejectedEvent>());
        Assert.Equal(2, harness.Recorder.Last<BinningChangedEvent>()!.Factor);
    }

    // ---- In the driver where it can (D43 as revised) ----

    /// <summary>The event's figures without its timestamp, which record equality would otherwise compare.</summary>
    private static (int, int, int) Arrangement(BinningAppliedEvent e) => (e.Requested, e.ByDriver, e.ByApplication);

    /// <summary>
    /// A driver that offers the bin does it, and the frame is not binned again:
    /// binned twice it would be at a quarter of the scale it is solved at.
    /// </summary>
    [Fact]
    public async Task ADriverThatOffersTheBin_DoesIt_AndTheFrameIsNotBinnedAgain()
    {
        using var harness = new SessionHarness(binning: 2, realFrames: true, driverBins: new[] { 1, 2 });
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);

        Assert.Equal(2, harness.Camera.Binning);
        Assert.Equal((2, 2, 1), Arrangement(harness.Recorder.Last<BinningAppliedEvent>()!));
        Assert.Equal(SensorFocalLength, harness.Session.Profile!.FocalLengthMillimetres!.Value, 6);
    }

    /// <summary>A driver that bins, but not by the factor wanted, is kept unbinned and the session bins.</summary>
    [Fact]
    public async Task ADriverWithoutTheBin_IsLeftUnbinned_AndTheSessionBins()
    {
        using var harness = new SessionHarness(binning: 2, realFrames: true, driverBins: new[] { 1, 3 });
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);

        Assert.Equal(1, harness.Camera.Binning);
        Assert.Empty(harness.Camera.BinningRequests);
        Assert.Equal((2, 1, 2), Arrangement(harness.Recorder.Last<BinningAppliedEvent>()!));
        Assert.Equal(SensorFocalLength, harness.Session.Profile!.FocalLengthMillimetres!.Value, 6);
    }

    /// <summary>
    /// A driver that lists a bin and then refuses it is asked once, said so
    /// once, and binned for by the session from then on -- not asked again on
    /// every frame, each with a warning in the log.
    /// </summary>
    [Fact]
    public async Task ADriverThatRefusesTheBin_IsAskedOnce_AndTheSessionBins()
    {
        using var harness = new SessionHarness(
            binning: 2, realFrames: true, driverBins: new[] { 1, 2 }, driverRefusesBinning: true);
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);
        await harness.Session.SendAsync(new CaptureNextPointCommand());
        await harness.WaitForSampleAsync(2);

        Assert.Equal(new[] { 2 }, harness.Camera.BinningRequests);
        Assert.Single(harness.Recorder.All<DriverBinningRefusedEvent>());
        Assert.Equal((2, 1, 2), Arrangement(harness.Recorder.Last<BinningAppliedEvent>()!));
        Assert.Equal(SensorFocalLength, harness.Session.Profile!.FocalLengthMillimetres!.Value, 6);
    }

    /// <summary>Turned off again, the driver is set back to none rather than left binning.</summary>
    [Fact]
    public async Task TurningBinningOff_SetsTheDriverBackToNone()
    {
        using var harness = new SessionHarness(binning: 2, realFrames: true, driverBins: new[] { 1, 2 });
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);

        await harness.Session.SendAsync(new SetBinningCommand(1));
        await harness.Recorder.WaitForAsync<BinningAppliedEvent>(e => Arrangement(e) == (1, 1, 1));

        Assert.Equal(1, harness.Camera.Binning);
    }
}
