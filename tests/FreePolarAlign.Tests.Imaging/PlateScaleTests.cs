using FreePolarAlign.Devices.Simulated.SyntheticSky;
using FreePolarAlign.Imaging.Wcs;
using Xunit;

namespace FreePolarAlign.Tests.Imaging;

/// <summary>
/// Focal length recovery, and the WCS properties a solve is read through.
/// </summary>
public class PlateScaleTests
{
    /// <summary>
    /// Phase 2 requires focal length recovered to within 0.5%. Checked against
    /// the plate scale of a WCS built from known optics, which isolates the
    /// recovery arithmetic from the solver: if this were wrong, a correct solve
    /// would still yield a wrong focal length.
    /// </summary>
    [Theory]
    [InlineData(100.0, 3.8)]
    [InlineData(200.0, 3.8)]
    [InlineData(400.0, 3.8)]
    [InlineData(135.0, 2.4)]
    [InlineData(360.0, 5.0)]
    public void FocalLength_RecoveredFromPlateScale_WithinHalfAPercent(double focalLengthMm, double pitchMicrons)
    {
        TanWcsSolution wcs = SkyRenderer.BuildWcs(83.6, 22.0, 1600, 1200, pitchMicrons, focalLengthMm, rotationDegrees: 33.0);

        double recovered = PlateScale.FocalLengthMillimetres(pitchMicrons, wcs.PixelScaleArcsecondsPerPixel);

        double errorFraction = Math.Abs(recovered - focalLengthMm) / focalLengthMm;
        Assert.True(errorFraction < 0.005, $"recovered {recovered:F3} mm from {focalLengthMm} mm, error {errorFraction:P3}");
    }

    /// <summary>Recovery must be exact in both directions, or the two disagree about what a profile means.</summary>
    [Theory]
    [InlineData(100.0, 3.8)]
    [InlineData(400.0, 2.4)]
    public void ScaleAndFocalLength_AreInverses(double focalLengthMm, double pitchMicrons)
    {
        double scale = PlateScale.ScaleArcsecondsPerPixel(pitchMicrons, focalLengthMm);
        double back = PlateScale.FocalLengthMillimetres(pitchMicrons, scale);

        Assert.Equal(focalLengthMm, back, precision: 9);
    }

    /// <summary>
    /// Camera rotation must survive the round trip through the CD matrix
    /// exactly, and for both parities: a mirror changes handedness, not the
    /// position angle of the y axis.
    /// </summary>
    [Fact]
    public void RotationIsRecoveredFromTheCdMatrix()
    {
        foreach (double rotation in new[] { 0.0, 17.0, 90.0, 183.0, 271.0, 359.0 })
        {
            foreach (bool mirrored in new[] { false, true })
            {
                TanWcsSolution wcs = SkyRenderer.BuildWcs(120.0, 40.0, 1024, 768, 3.8, 200.0, rotation, mirrored);
                double difference = Math.Abs(WrapTo180(wcs.RotationDegrees - rotation));

                Assert.True(difference < 1e-9,
                    $"rotation {rotation} (mirrored: {mirrored}) came back as {wcs.RotationDegrees}");
            }
        }
    }

    /// <summary>
    /// D12 turns the determinant's sign into on-screen arrow parity, so the
    /// convention has to be pinned: an unmirrored sky frame is the *negative*
    /// determinant, because right ascension increases eastward and the sky is
    /// seen from inside.
    /// </summary>
    [Fact]
    public void ParityFollowsTheDeterminantSign()
    {
        TanWcsSolution direct = SkyRenderer.BuildWcs(120.0, 40.0, 1024, 768, 3.8, 200.0, mirrored: false);
        TanWcsSolution mirrored = SkyRenderer.BuildWcs(120.0, 40.0, 1024, 768, 3.8, 200.0, mirrored: true);

        Assert.True(direct.Determinant < 0);
        Assert.False(direct.IsMirrored);

        Assert.True(mirrored.Determinant > 0);
        Assert.True(mirrored.IsMirrored);

        // A mirror must not change the scale, only the handedness.
        Assert.Equal(direct.PixelScaleArcsecondsPerPixel, mirrored.PixelScaleArcsecondsPerPixel, precision: 9);
    }

    /// <summary>
    /// A mirrored frame really is a reflection on the sky, not merely a sign
    /// flip in the header: walking one pixel along +x must move the opposite way
    /// in right ascension. Checked geometrically, so the property cannot be
    /// satisfied by a bookkeeping mistake that leaves the determinant right and
    /// the projection wrong.
    /// </summary>
    [Fact]
    public void MirroredFrame_ReversesTheSkyDirectionOfThePixelXAxis()
    {
        TanWcsSolution direct = SkyRenderer.BuildWcs(120.0, 40.0, 1024, 768, 3.8, 200.0, mirrored: false);
        TanWcsSolution mirrored = SkyRenderer.BuildWcs(120.0, 40.0, 1024, 768, 3.8, 200.0, mirrored: true);

        double DeltaRa(TanWcsSolution wcs)
        {
            (double centreRa, _) = wcs.PixelToWorld(wcs.Crpix1, wcs.Crpix2);
            (double shiftedRa, _) = wcs.PixelToWorld(wcs.Crpix1 + 50.0, wcs.Crpix2);
            return WrapTo180(shiftedRa - centreRa);
        }

        Assert.True(DeltaRa(direct) * DeltaRa(mirrored) < 0,
            "mirroring should reverse which way right ascension runs across the frame");
    }

    [Fact]
    public void FieldRadiusMatchesTheOpticsItWasBuiltFrom()
    {
        const double focalLength = 200.0;
        const double pitch = 3.8;
        const int width = 1684;
        const int height = 1263;

        TanWcsSolution wcs = SkyRenderer.BuildWcs(83.6, 22.0, width, height, pitch, focalLength);

        double fromOptics = PlateScale.FieldDiagonalDegrees(width, height, pitch, focalLength) / 2.0;
        Assert.Equal(fromOptics, wcs.FieldRadiusDegrees(width, height), precision: 9);
    }

    // ---- Equipment profile: the point of which is that a user supplies a
    // ---- focal length once, badly, and the software then measures it.

    [Fact]
    public void UnsolvedProfile_HasNoScaleAndAWideTolerance()
    {
        var profile = new EquipmentProfile("guide scope", PixelPitchMicrons: 3.8, WidthPixels: 1280, HeightPixels: 960);

        Assert.Null(profile.ExpectedScaleArcsecondsPerPixel);
        Assert.Null(profile.ExpectedFieldRadiusDegrees);
        Assert.False(profile.IsFocalLengthSolved);
        Assert.True(profile.ScaleToleranceFraction >= 0.25);
    }

    [Fact]
    public void SolvingAProfile_ReplacesTheClaimedFocalLengthAndNarrowsTolerance()
    {
        // A user who believes their 200 mm guide scope is a 240 mm one: several
        // percent out, which is entirely typical.
        var claimed = new EquipmentProfile("guide scope", 3.8, 1280, 960, FocalLengthMillimetres: 240.0);
        Assert.False(claimed.IsFocalLengthSolved);
        Assert.True(claimed.ScaleToleranceFraction >= 0.25);

        double trueScale = PlateScale.ScaleArcsecondsPerPixel(3.8, 200.0);
        EquipmentProfile solved = claimed.WithSolvedScale(trueScale);

        Assert.True(solved.IsFocalLengthSolved);
        Assert.NotNull(solved.FocalLengthMillimetres);
        Assert.Equal(200.0, solved.FocalLengthMillimetres!.Value, precision: 6);
        Assert.True(solved.ScaleToleranceFraction < claimed.ScaleToleranceFraction);
        Assert.Equal("guide scope", solved.Name);
    }

    [Fact]
    public void SolvedProfile_PredictsTheFieldRadiusASolverNeedsAsAHint()
    {
        var profile = new EquipmentProfile("guide scope", 3.8, 1684, 1263)
            .WithSolvedScale(PlateScale.ScaleArcsecondsPerPixel(3.8, 200.0));

        TanWcsSolution wcs = SkyRenderer.BuildWcs(83.6, 22.0, 1684, 1263, 3.8, 200.0);

        Assert.NotNull(profile.ExpectedFieldRadiusDegrees);
        Assert.Equal(wcs.FieldRadiusDegrees(1684, 1263), profile.ExpectedFieldRadiusDegrees!.Value, precision: 6);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonsensicalOpticsAreRejected(double badValue)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PlateScale.FocalLengthMillimetres(3.8, badValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlateScale.ScaleArcsecondsPerPixel(badValue, 200.0));
    }

    private static double WrapTo180(double degrees)
    {
        double wrapped = degrees % 360.0;
        if (wrapped > 180.0)
        {
            wrapped -= 360.0;
        }
        else if (wrapped <= -180.0)
        {
            wrapped += 360.0;
        }

        return wrapped;
    }
}
