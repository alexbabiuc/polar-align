using FreePolarAlign.Solving;
using Xunit;

namespace FreePolarAlign.Tests.Solving;

/// <summary>
/// Watney has no notion of "arcsec per pixel"; it searches by field radius in
/// degrees instead (D3). These tests pin down the conversion in isolation, with
/// no solve and no quad database involved.
/// </summary>
public class FieldRadiusConversionTests
{
    [Fact]
    public void KnownScaleAndDimensions_ProducesExpectedFieldRadius()
    {
        // 1000x1000 px at 2.0"/px: diagonal = 1414.2136 px, so the field radius
        // -- half the diagonal, in degrees -- is 0.5 * 1414.2136 * 2.0 / 3600.
        double diagonalPixels = Math.Sqrt(1000.0 * 1000.0 + 1000.0 * 1000.0);
        double expected = 0.5 * diagonalPixels * 2.0 / 3600.0;

        double radius = WatneyPlateSolver.ComputeFieldRadiusDegrees(
            scaleArcsecPerPixel: 2.0, imageWidth: 1000, imageHeight: 1000);

        Assert.Equal(expected, radius, precision: 9);
    }

    [Fact]
    public void RadiusScalesWithPixelScale()
    {
        double atTwo = WatneyPlateSolver.ComputeFieldRadiusDegrees(2.0, 1600, 1200);
        double atFour = WatneyPlateSolver.ComputeFieldRadiusDegrees(4.0, 1600, 1200);

        Assert.Equal(2.0 * atTwo, atFour, precision: 9);
    }

    [Fact]
    public void RadiusUsesTheDiagonalNotAnAxis()
    {
        // A 3-4-5 frame: the diagonal is 5000 px, so the radius must come from
        // 2500 px and not from either half-side (1500 or 2000).
        double radius = WatneyPlateSolver.ComputeFieldRadiusDegrees(1.0, 4000, 3000);

        Assert.Equal(2500.0 * 1.0 / 3600.0, radius, precision: 9);
    }

    [Fact]
    public void Radius_IsClampedToWatneysThirtyDegreeLimit()
    {
        // An enormous scale hint would otherwise ask Watney for a field radius
        // its own option setters reject outright.
        double radius = WatneyPlateSolver.ComputeFieldRadiusDegrees(500.0, 4000, 3000);

        Assert.True(radius <= 30.0);
        Assert.True(radius > 0.0);
    }

    [Fact]
    public void Radius_IsClampedAboveZero()
    {
        double radius = WatneyPlateSolver.ComputeFieldRadiusDegrees(1e-9, 10, 10);

        Assert.True(radius > 0.0);
    }
}
