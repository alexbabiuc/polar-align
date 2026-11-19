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

    // ---- Right ascension is hours ----

    /// <summary>
    /// The rule the UI's own labels state: right ascension is typed in hours.
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
    /// A bare number is hours too (D44). It was degrees until then, so this is
    /// the test that would catch the old reading coming back -- and with it a
    /// target misplaced by a factor of fifteen.
    /// </summary>
    [Theory]
    [InlineData("10.5", 157.5)]
    [InlineData("20.5", 307.5)]
    [InlineData("0", 0.0)]
    [InlineData("23.999", 359.985)]
    public void DecimalRightAscension_IsReadAsHours(string text, double expectedDegrees)
    {
        Assert.True(CoordinateText.TryParseRightAscension(text, out double degrees), text);
        Assert.Equal(expectedDegrees, degrees, precision: 6);
    }

    /// <summary>The two forms of the same hours agree, which is what lets a field rewrite one as the other.</summary>
    [Fact]
    public void DecimalAndSexagesimalHours_MeanTheSame()
    {
        Assert.True(CoordinateText.TryParseRightAscension("10.5", out double asDecimal));
        Assert.True(CoordinateText.TryParseRightAscension("10:30:00", out double asSexagesimal));

        Assert.Equal(asSexagesimal, asDecimal, precision: 9);
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
    [InlineData("24")]
    [InlineData("157.5")]
    [InlineData("-1.5")]
    public void ImpossibleRightAscension_IsRefused(string text) =>
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

        // Whole seconds of time are 15 arcseconds of angle, so rounding can move
        // the value by half that.
        Assert.Equal(degrees, reparsed, tolerance: 7.5 * ArcsecondInDegrees);
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
        string text = CoordinateText.FormatDegrees(degrees);

        Assert.True(CoordinateText.TryParseDeclination(text, out double reparsed), text);
        Assert.Equal(degrees, reparsed, tolerance: ArcsecondInDegrees);
    }

    /// <summary>The hours counterpart of the d:mm:ss form, unpadded and in whole seconds (D44).</summary>
    [Theory]
    [InlineData(157.5, "10:30:00")]
    [InlineData(0.0, "0:00:00")]
    [InlineData(218.04, "14:32:10")]
    [InlineData(-15.0, "23:00:00")]
    public void RightAscension_IsShownAsHoursMinutesSeconds(double degrees, string expected)
    {
        Assert.Equal(expected, CoordinateText.FormatRightAscension(degrees));
    }

    /// <summary>
    /// Rounding the seconds must not be allowed to produce "24:00:00", which is
    /// not a right ascension and which the parser would refuse.
    /// </summary>
    [Fact]
    public void FormattingNearTheWrapPoint_DoesNotProduceTwentyFourHours()
    {
        string text = CoordinateText.FormatRightAscension(359.99999);

        Assert.Equal("0:00:00", text);
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
        string text = CoordinateText.FormatDegrees(degrees);

        Assert.DoesNotContain(":60", text);
        Assert.True(CoordinateText.TryParseDeclination(text, out _), text);
    }

    /// <summary>
    /// The one form every coordinate in degrees is shown in: degrees, then
    /// arcminutes and arcseconds of two digits each, whole seconds. "20.5" is
    /// the case asked for by name; the rest are the edges of the rounding --
    /// a carry through both fields, and a sign that must belong to the whole
    /// value below one degree and vanish when rounding leaves nothing.
    /// </summary>
    [Theory]
    [InlineData(20.5, "20:30:00")]
    [InlineData(46.77, "46:46:12")]
    [InlineData(-0.5, "-0:30:00")]
    [InlineData(-122.4194, "-122:25:10")]
    [InlineData(41.99999, "42:00:00")]
    [InlineData(-0.0001, "0:00:00")]
    [InlineData(0.0, "0:00:00")]
    public void Degrees_AreShownAsDegreesMinutesSeconds(double degrees, string expected)
    {
        Assert.Equal(expected, CoordinateText.FormatDegrees(degrees));
    }

    /// <summary>
    /// The limit is the caller's: a longitude of 122 degrees is legal and a
    /// latitude of 122 is a typing mistake, in either notation.
    /// </summary>
    [Theory]
    [InlineData("-122:25:10", 180.0, true)]
    [InlineData("-122.4194", 180.0, true)]
    [InlineData("-122:25:10", 90.0, false)]
    [InlineData("180.5", 180.0, false)]
    [InlineData("20:61:00", 90.0, false)]
    public void Degrees_ParseWithinTheirLimit(string text, double limit, bool accepted)
    {
        Assert.Equal(accepted, CoordinateText.TryParseDegrees(text, limit, out _));
    }
}
