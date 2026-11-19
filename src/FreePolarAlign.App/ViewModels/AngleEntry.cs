using FreePolarAlign.Core.Engine;

namespace FreePolarAlign.App.ViewModels;

/// <summary>
/// One editable coordinate -- a latitude, a longitude, a declination, a right
/// ascension -- shown sexagesimally (D44) and read back from that or from a
/// decimal in the unit shown: degrees, or hours for a right ascension. The
/// value behind it is always in degrees.
///
/// The display carries whole seconds and the value behind it does not, so a
/// field that is shown and then confirmed untouched would hand back a value
/// slightly off the one it was seeded with: half an arcsecond for degrees, up
/// to seven and a half for a right ascension. Small, but not harmless: Settings
/// would then reconfigure a site nobody changed, and an accepted slew proposal
/// would arrive as an edited one (D16, D18). So the text a field was seeded
/// with reads back as exactly the value it was seeded from.
/// </summary>
internal sealed class AngleEntry
{
    private delegate bool Parser(string? text, out double degrees);

    private readonly Func<double, string> _format;
    private readonly Parser _parse;
    private string? _seedText;
    private double _seedValue;

    private AngleEntry(Func<double, string> format, Parser parse)
    {
        _format = format;
        _parse = parse;
    }

    /// <param name="limit">The largest magnitude accepted: 90 for a latitude or declination, 180 for a longitude.</param>
    public static AngleEntry Degrees(double limit) => new(
        CoordinateText.FormatDegrees,
        (string? text, out double degrees) => CoordinateText.TryParseDegrees(text, limit, out degrees));

    /// <summary>Shown and typed in hours, held in degrees.</summary>
    public static AngleEntry RightAscension() => new(
        CoordinateText.FormatRightAscension,
        CoordinateText.TryParseRightAscension);

    /// <summary>Remembers <paramref name="degrees"/> and returns the text to show for it.</summary>
    public string Seed(double degrees)
    {
        _seedText = _format(degrees);
        _seedValue = degrees;
        return _seedText;
    }

    public bool TryRead(string? text, out double degrees)
    {
        if (_seedText is not null && text?.Trim() == _seedText)
        {
            degrees = _seedValue;
            return true;
        }

        // A decimal comma is accepted as the decimal point: it is how the
        // number is written on many locales, and a comma is not one of the
        // sexagesimal separators, so it cannot mean anything else here.
        return _parse(text?.Replace(',', '.'), out degrees);
    }

    /// <summary>
    /// The text rewritten sexagesimally when it can be read, and as typed when
    /// it cannot, so a mistake stays on screen to be corrected instead of
    /// vanishing.
    /// </summary>
    public string Normalise(string text) =>
        TryRead(text, out double degrees) ? _format(degrees) : text;
}
