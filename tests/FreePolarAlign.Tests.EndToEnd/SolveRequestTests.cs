using FreePolarAlign.Core.Engine;
using FreePolarAlign.Imaging.Wcs;
using FreePolarAlign.Solving;
using Xunit;

namespace FreePolarAlign.Tests.EndToEnd;

/// <summary>
/// What the session tells a solver about the field before any solve has
/// measured it (D41).
/// </summary>
public class SolveRequestTests
{
    /// <summary>
    /// A typed focal length reaches the solver as a scale, marked as a claim by
    /// its wide tolerance. Withheld, as it was through 0.0.29, ASTAP fell back on
    /// a field ladder that stops at 0.37° and could not solve a 0.23° frame
    /// from an 800 mm scope.
    /// </summary>
    [Fact]
    public async Task TypedFocalLength_ReachesTheFirstSolveAsAClaimedScale()
    {
        using var harness = new SessionHarness(withMount: false);
        await harness.ConnectAsync(mount: false);
        await harness.Session.SendAsync(new ConfigureFocalLengthCommand(800.0));
        await harness.ConfirmSiteAsync();
        await harness.StartAsync();

        await harness.WaitForSampleAsync(1, poll: false);

        PlateSolveRequest first;
        lock (harness.Perfect.Requests)
        {
            first = harness.Perfect.Requests[0];
        }

        double expected = PlateScale.ScaleArcsecondsPerPixel(harness.Camera.PixelSizeMicrons, 800.0);
        Assert.NotNull(first.ApproximateScaleArcsecPerPixel);
        Assert.Equal(expected, first.ApproximateScaleArcsecPerPixel!.Value, precision: 9);
        Assert.Equal(0.25, first.ScaleToleranceFraction);
    }

    [Fact]
    public async Task NoFocalLength_SolvesWithoutAScale()
    {
        using var harness = new SessionHarness(withMount: false);
        await harness.ReadyAsync(mount: false);

        await harness.WaitForSampleAsync(1, poll: false);

        PlateSolveRequest first;
        lock (harness.Perfect.Requests)
        {
            first = harness.Perfect.Requests[0];
        }

        Assert.Null(first.ApproximateScaleArcsecPerPixel);
        Assert.Null(first.ScaleToleranceFraction);
    }
}
