using FreePolarAlign.Imaging.Detection;
using FreePolarAlign.Imaging.Fits;
using Xunit;

namespace FreePolarAlign.Tests.Imaging;

/// <summary>
/// The frame drawn from a detector's own measurements, handed to a solver that
/// could not match the original.
///
/// The property that matters is that a star comes back where it went in: the
/// whole point is to ask a solver about the *positions* this project measured,
/// so a rendering that shifted them would be asking about a different sky.
/// </summary>
public class RestampedFrameTests
{
    private static DetectedStar Star(double x, double y, double flux) =>
        new(x, y, flux, PeakAboveBackground: flux / 4.0, FwhmPixels: 3.0, Elongation: 1.0,
            PositionAngleDegrees: 0.0, PixelCount: 9);

    /// <summary>
    /// Drawn, detected again, and found within a tenth of a pixel of where it
    /// was put. A tenth is far tighter than the arcsecond this project works to
    /// and leaves the round trip nowhere to hide a systematic shift -- an
    /// off-by-one between the detector's 1-based coordinates and the array's
    /// 0-based ones would show up here as exactly one pixel.
    /// </summary>
    [Fact]
    public void StarsComeBackWhereTheyWerePut()
    {
        var stars = new[]
        {
            Star(100.4, 60.7, 50000.0),
            Star(250.0, 180.25, 20000.0),
            Star(60.9, 200.1, 8000.0),
        };

        FitsImage frame = RestampedFrame.Render(320, 240, stars);
        IReadOnlyList<DetectedStar> found = StarDetector.Detect(frame);

        Assert.Equal(stars.Length, found.Count);

        foreach (DetectedStar expected in stars)
        {
            DetectedStar nearest = found.MinBy(f => ((f.X - expected.X) * (f.X - expected.X))
                                                    + ((f.Y - expected.Y) * (f.Y - expected.Y)))!;
            Assert.True(
                Math.Abs(nearest.X - expected.X) < 0.1 && Math.Abs(nearest.Y - expected.Y) < 0.1,
                $"star at ({expected.X}, {expected.Y}) came back at ({nearest.X:F2}, {nearest.Y:F2})");
        }
    }

    /// <summary>
    /// Brightness order survives, because that is what a matcher uses to choose
    /// which stars to form quads from. The absolute values are deliberately not
    /// preserved -- several magnitudes compressed onto one output range -- but
    /// reordering two stars would hand the solver a different pattern.
    /// </summary>
    [Fact]
    public void BrightnessOrderIsPreserved()
    {
        var stars = new[]
        {
            Star(50.0, 50.0, 1000.0),
            Star(150.0, 50.0, 40000.0),
            Star(250.0, 50.0, 8000.0),
        };

        FitsImage frame = RestampedFrame.Render(320, 240, stars);
        IReadOnlyList<DetectedStar> found = StarDetector.Detect(frame);

        // The detector returns stars in flux order, so their x positions should
        // follow the order of the fluxes they were drawn with.
        Assert.Equal(3, found.Count);
        Assert.Equal(150.0, found[0].X, precision: 0);
        Assert.Equal(250.0, found[1].X, precision: 0);
        Assert.Equal(50.0, found[2].X, precision: 0);
    }

    /// <summary>
    /// Nothing but the stars: the drawn background is flat, so a solver's own
    /// background estimate has nothing to fight. This is the noise, gradient and
    /// amp glow that the redraw exists to remove.
    /// </summary>
    [Fact]
    public void TheBackgroundIsFlat()
    {
        FitsImage frame = RestampedFrame.Render(64, 64, new[] { Star(32.0, 32.0, 10000.0) });

        // A corner, far from the single star.
        Assert.Equal(frame.Pixels[0, 0], frame.Pixels[0, 63]);
        Assert.Equal(frame.Pixels[0, 0], frame.Pixels[63, 0]);
    }

    [Fact]
    public void AFrameWithNoStarsIsStillAValidFrame()
    {
        FitsImage frame = RestampedFrame.Render(32, 24, Array.Empty<DetectedStar>());

        Assert.Equal(32, frame.Width);
        Assert.Equal(24, frame.Height);
        Assert.Empty(StarDetector.Detect(frame));
    }

    /// <summary>Stars near the edge are drawn as far as the frame allows rather than throwing.</summary>
    [Fact]
    public void StarsAtTheEdgeDoNotRunOff()
    {
        FitsImage frame = RestampedFrame.Render(
            64, 64, new[] { Star(1.0, 1.0, 10000.0), Star(64.0, 64.0, 10000.0) });

        Assert.True(frame.Pixels[0, 0] > 1000.0, "a star at the first pixel left no signal there");
        Assert.True(frame.Pixels[63, 63] > 1000.0, "a star at the last pixel left no signal there");
    }
}
