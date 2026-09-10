using FreePolarAlign.Core.Engine;
using Xunit;

namespace FreePolarAlign.Tests.Core;

/// <summary>
/// Reading typed coordinates.
///
/// This is not cosmetic parsing. D18 lets the user override the engine's
/// suggestion by typing coordinates, and those coordinates are then commanded to
/// a motorised telescope. A value that parses to the wrong number points the
/// instrument somewhere nobody asked for, and the two ways that happens are both
/// pinned down here: right ascension read in the wrong unit, and a negative
/// declination whose sign is lost.
/// </summary>
public class CoordinateTextTests
{
    private const double ArcsecondInDegrees = 1.0 / 3600.0;

    // ---- The hours-versus-degrees rule ----

    /// <summary>
    /// The rule the UI's own labels state: separators mean sexagesimal hours.
    /// Getting this backwards would misplace a target by a factor of fifteen --
    /// far enough that the plate solve would simply fail, which is the lucky
    /// outcome, but on a mount with limits it is a collision.
    /// </summary>
    [Theory]
    [InlineData("10:30:00", 157.5)]
    [InlineData("10 30 00", 157.5)]
    [InlineData("10h 30m 00s", 157.5)]
    [InlineData("00:00:00", 0.0)]
    [InlineData("23:59:59", 359.99583333)]
    [InlineData("12:00:00", 180.0)]
    public void SexagesimalRightAscension_IsReadAsHours(string text, double expectedDegrees)
    {
        Assert.True(CoordinateText.TryParseRightAscension(text, out double degrees), text);
        Assert.Equal(expectedDegrees, degrees, precision: 6);
    }

    /// <summary>
    /// A bare number is degrees, because that is the form the software itself
    /// produces and the form a user copying from a solve result will have.
    /// </summary>
    [Theory]
    [InlineData("157.5", 157.5)]
    [InlineData("10.5", 10.5)]
    [InlineData("0", 0.0)]
    [InlineData("359.999", 359.999)]
    public void DecimalRightAscension_IsReadAsDegrees(string text, double expectedDegrees)
    {
        Assert.True(CoordinateText.TryParseRightAscension(text, out double degrees), text);
        Assert.Equal(expectedDegrees, degrees, precision: 6);
    }

    /// <summary>
    /// The same digits mean different things in the two forms, which is exactly
    /// why the distinction is drawn explicitly rather than guessed at.
    /// </summary>
    [Fact]
    public void TheSameDigits_MeanDifferentThings_InTheTwoForms()
    {
        Assert.True(CoordinateText.TryParseRightAscension("10.5", out double asDegrees));
        Assert.True(CoordinateText.TryParseRightAscension("10:30:00", out double asHours));

        Assert.Equal(10.5, asDegrees, precision: 6);
        Assert.Equal(157.5, asHours, precision: 6);
    }

    // ---- The sign trap ----

    /// <summary>
    /// A negative declination's sign belongs to the whole value, not to its
    /// degrees field. Taking the sign from the parsed number instead loses it
    /// entirely between zero and minus one degree -- a band of sky two degrees
    /// wide -- and turns a southern target into a northern one.
    /// </summary>
    [Theory]
    [InlineData("-00 30 00", -0.5)]
    [InlineData("-00:00:36", -0.01)]
    [InlineData("-0 45 00", -0.75)]
    public void NegativeDeclinationInsideTheFirstDegree_KeepsItsSign(string text, double expectedDegrees)
    {
        Assert.True(CoordinateText.TryParseDeclination(text, out double degrees), text);
        Assert.Equal(expectedDegrees, degrees, precision: 6);
        Assert.True(degrees < 0.0, $"'{text}' lost its sign and parsed as {degrees}");
    }

    [Theory]
    [InlineData("+41 16 09", 41.269167)]
    [InlineData("41 16 09", 41.269167)]
    [InlineData("-41 16 09", -41.269167)]
    [InlineData("-12.5", -12.5)]
    [InlineData("90 00 00", 90.0)]
    [InlineData("-90", -90.0)]
    public void Declination_IsReadInDegreesEitherWay(string text, double expectedDegrees)
    {
        Assert.True(CoordinateText.TryParseDeclination(text, out double degrees), text);
        Assert.Equal(expectedDegrees, degrees, precision: 5);
    }

    // ---- Refusing rather than guessing ----

    /// <summary>
    /// Out of range is rejected, not clamped. A declination of 91 degrees is a
    /// typing mistake, and clamping it to 90 would hide the mistake behind a
    /// value the mount will happily accept.
    /// </summary>
    [Theory]
    [InlineData("91")]
    [InlineData("-91")]
    [InlineData("90 00 01")]
    [InlineData("100 00 00")]
    public void DeclinationBeyondThePoles_IsRefused(string text) =>
        Assert.False(CoordinateText.TryParseDeclination(text, out _), text);

    /// <summary>
    /// Twenty-four hours or more of right ascension, and any negative value, are
    /// refused rather than wrapped. Wrapping would turn a typo into a legal
    /// coordinate on the far side of the sky, which is the worst of the
    /// available outcomes.
    /// </summary>
    [Theory]
    [InlineData("24:00:00")]
    [InlineData("25:00:00")]
    [InlineData("-01:00:00")]
    public void ImpossibleSexagesimalRightAscension_IsRefused(string text) =>
        Assert.False(CoordinateText.TryParseRightAscension(text, out _), text);

    /// <summary>
    /// Minutes and seconds of sixty or more are a typo, not an unusual way of
    /// writing a larger value.
    /// </summary>
    [Theory]
    [InlineData("10 60 00")]
    [InlineData("10 30 60")]
    [InlineData("10 75 00")]
    public void SexagesimalFieldsOutOfRange_AreRefused(string text)
    {
        Assert.False(CoordinateText.TryParseRightAscension(text, out _), text);
        Assert.False(CoordinateText.TryParseDeclination(text, out _), text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("north")]
    [InlineData("12:xx:00")]
    [InlineData("1 2 3 4")]
    public void Nonsense_IsRefused(string? text)
    {
        Assert.False(CoordinateText.TryParseRightAscension(text, out _));
        Assert.False(CoordinateText.TryParseDeclination(text, out _));
    }

    /// <summary>
    /// Parsing is culture-invariant. A decimal comma would otherwise make the
    /// same typed coordinate mean one thing on the developer's machine and
    /// another on the user's, and a telescope is a poor place to discover that.
    /// </summary>
    [Fact]
    public void DecimalCommas_AreNotTreatedAsDecimalPoints()
    {
        // Were the current culture consulted, this would parse as 10.5 on a
        // machine using a comma separator and as 105 on one using a point.
        // Neither is a coordinate anyone typed, so it is refused.
        Assert.False(CoordinateText.TryParseDeclination("10,5", out _));
    }

    // ---- Round trips ----

    /// <summary>
    /// Formatting for display and reading back must agree, or the coordinates
    /// shown to the user would not be the ones the mount is sent to.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(83.822083)]
    [InlineData(157.5)]
    [InlineData(279.234)]
    [InlineData(359.9)]
    public void RightAscension_SurvivesFormattingAndReparsing(double degrees)
    {
        string text = CoordinateText.FormatRightAscension(degrees);

        Assert.True(CoordinateText.TryParseRightAscension(text, out double reparsed), text);

        // A tenth of a second of time is 1.5 arcseconds of angle, which is the
        // resolution the displayed form carries.
        Assert.Equal(degrees, reparsed, tolerance: 2.0 * ArcsecondInDegrees);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    [InlineData(41.269167)]
    [InlineData(-27.8)]
    [InlineData(89.999)]
    [InlineData(-89.999)]
    public void Declination_SurvivesFormattingAndReparsing(double degrees)
    {
        string text = CoordinateText.FormatDeclination(degrees);

        Assert.True(CoordinateText.TryParseDeclination(text, out double reparsed), text);
        Assert.Equal(degrees, reparsed, tolerance: ArcsecondInDegrees);
    }

    /// <summary>
    /// The editable field is seeded with the decimal form so that confirming
    /// without editing returns exactly what was proposed -- which is how the
    /// engine tells "accepted" from "overridden", and therefore whether to
    /// re-resolve the coordinates for the moment of the slew (D16).
    /// </summary>
    [Theory]
    [InlineData(157.512345)]
    [InlineData(0.000001)]
    [InlineData(359.999999)]
    public void DecimalForm_RoundTripsFarTighterThanTheOverrideThreshold(double degrees)
    {
        Assert.True(
            CoordinateText.TryParseRightAscension(CoordinateText.FormatDecimalDegrees(degrees), out double reparsed));

        // The engine treats a difference below one arcsecond as "unedited", so
        // the seeded text has to round trip well inside that.
        Assert.Equal(degrees, reparsed, tolerance: 0.1 * ArcsecondInDegrees);
    }

    /// <summary>
    /// Rounding the seconds must not be allowed to produce "24h 00m 00.0s",
    /// which is not a right ascension.
    /// </summary>
    [Fact]
    public void FormattingNearTheWrapPoint_DoesNotProduceTwentyFourHours()
    {
        string text = CoordinateText.FormatRightAscension(359.99999);

        Assert.DoesNotContain("24h", text);
        Assert.True(CoordinateText.TryParseRightAscension(text, out double reparsed), text);
        Assert.InRange(reparsed, 0.0, 360.0);
    }

    /// <summary>
    /// And the same rounding must not turn 60 arcseconds into a displayed
    /// "60.0" that would then be refused by the parser it is fed back into.
    /// </summary>
    [Theory]
    [InlineData(41.99999)]
    [InlineData(-41.99999)]
    [InlineData(12.016666)]
    public void DeclinationFormatting_NeverShowsSixtyArcsecondsOrArcminutes(double degrees)
    {
        string text = CoordinateText.FormatDeclination(degrees);

        Assert.DoesNotContain("60'", text);
        Assert.DoesNotContain("60.0\"", text);
        Assert.True(CoordinateText.TryParseDeclination(text, out _), text);
    }
}
