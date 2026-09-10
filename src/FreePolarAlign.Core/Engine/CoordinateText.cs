using System.Globalization;

namespace FreePolarAlign.Core.Engine;

/// <summary>
/// Reading and writing sky coordinates as text.
///
/// This exists because of the override in D18: the user can type the
/// coordinates the mount will slew to, and a coordinate that parses to the
/// wrong number points a telescope somewhere it was not asked to go. Two
/// specific traps are handled deliberately rather than left to
/// <c>double.Parse</c>:
///
/// <list type="bullet">
/// <item>
/// Right ascension is conventionally written in hours but decimally in degrees,
/// and the same string can mean either. The rule here is explicit and stated in
/// the UI's own labels: a value containing a separator is sexagesimal
/// <em>hours</em>, a bare number is decimal <em>degrees</em>. "10:30:00" is
/// therefore 157.5 degrees while "10.5" is 10.5 degrees, and both are what the
/// person typing them almost certainly meant.
/// </item>
/// <item>
/// A negative declination's sign belongs to the whole value, not to its degrees
/// field, so "-00 30 00" is minus half a degree. Reading the sign off the parsed
/// number instead loses it entirely for every declination between zero and minus
/// one degree -- a band that includes a good deal of sky.
/// </item>
/// </list>
///
/// Parsing is culture-invariant on purpose. A decimal comma would otherwise make
/// "10,5" mean one thing on the developer's machine and another on the user's,
/// and coordinates are not the place to find that out.
/// </summary>
public static class CoordinateText
{
    private static readonly char[] Separators = { ' ', ':', '\t', 'h', 'H', 'm', 'M', 's', 'S', 'd', 'D', '°', '\'', '"' };

    /// <summary>
    /// Parses a right ascension. Sexagesimal input is read as hours; a bare
    /// decimal number is read as degrees. Result is normalised to [0, 360).
    /// </summary>
    public static bool TryParseRightAscension(string? text, out double degrees)
    {
        degrees = 0.0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();

        if (!LooksSexagesimal(trimmed))
        {
            if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double asDegrees))
            {
                return false;
            }

            if (!double.IsFinite(asDegrees))
            {
                return false;
            }

            degrees = Normalise360(asDegrees);
            return true;
        }

        if (!TryParseSexagesimal(trimmed, out double hours, out bool negative) || negative)
        {
            // A negative right ascension is not a thing anyone means; rejecting
            // it is safer than silently wrapping it into the far side of the sky.
            return false;
        }

        if (hours >= 24.0)
        {
            return false;
        }

        degrees = Normalise360(hours * 15.0);
        return true;
    }

    /// <summary>
    /// Parses a declination in degrees, sexagesimal or decimal. Rejects anything
    /// outside [-90, 90] rather than clamping: a declination of 91 degrees is a
    /// typing mistake, and clamping it would hide the mistake behind a legal
    /// value.
    /// </summary>
    public static bool TryParseDeclination(string? text, out double degrees)
    {
        degrees = 0.0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();

        if (!LooksSexagesimal(trimmed))
        {
            if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double asDegrees)
                || !double.IsFinite(asDegrees))
            {
                return false;
            }

            if (asDegrees is < -90.0 or > 90.0)
            {
                return false;
            }

            degrees = asDegrees;
            return true;
        }

        if (!TryParseSexagesimal(trimmed, out double magnitude, out bool negative))
        {
            return false;
        }

        if (magnitude > 90.0)
        {
            return false;
        }

        degrees = negative ? -magnitude : magnitude;
        return true;
    }

    /// <summary>Right ascension as hours, minutes and seconds -- how it is read off a chart.</summary>
    public static string FormatRightAscension(double degrees)
    {
        double hours = Normalise360(degrees) / 15.0;
        (int whole, int minutes, double seconds) = Split(hours);

        // Rounding the seconds can carry into the minutes and hours, and an
        // "24h 00m 00.0s" readout would be simply wrong, so it is renormalised.
        if (seconds >= 59.95)
        {
            seconds = 0.0;
            minutes++;
        }

        if (minutes >= 60)
        {
            minutes = 0;
            whole++;
        }

        if (whole >= 24)
        {
            whole = 0;
        }

        return FormattableString.Invariant($"{whole:00}h {minutes:00}m {seconds:00.0}s");
    }

    /// <summary>Declination as signed degrees, arcminutes and arcseconds.</summary>
    public static string FormatDeclination(double degrees)
    {
        string sign = degrees < 0.0 ? "-" : "+";
        (int whole, int minutes, double seconds) = Split(Math.Abs(degrees));

        if (seconds >= 59.95)
        {
            seconds = 0.0;
            minutes++;
        }

        if (minutes >= 60)
        {
            minutes = 0;
            whole++;
        }

        return FormattableString.Invariant($"{sign}{whole:00}° {minutes:00}' {seconds:00.0}\"");
    }

    /// <summary>
    /// Round-trippable decimal degrees, for pre-filling an editable field. The
    /// override dialog is seeded with this rather than with the sexagesimal form
    /// so that a user who confirms without editing sends back exactly what was
    /// proposed.
    /// </summary>
    public static string FormatDecimalDegrees(double degrees) =>
        degrees.ToString("F6", CultureInfo.InvariantCulture);

    private static bool LooksSexagesimal(string text) =>
        text.IndexOfAny(Separators) >= 0;

    private static bool TryParseSexagesimal(string text, out double magnitude, out bool negative)
    {
        magnitude = 0.0;
        negative = text.StartsWith('-');

        string body = text.TrimStart('+', '-');
        string[] parts = body.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length is 0 or > 3)
        {
            return false;
        }

        double[] values = new double[3];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                || !double.IsFinite(values[i])
                || values[i] < 0.0)
            {
                return false;
            }
        }

        // Only the leading field may exceed its natural range (a bare "90" is a
        // legal declination); minutes and seconds of 60 or more mean a typo.
        if (parts.Length > 1 && values[1] >= 60.0)
        {
            return false;
        }

        if (parts.Length > 2 && values[2] >= 60.0)
        {
            return false;
        }

        magnitude = values[0] + values[1] / 60.0 + values[2] / 3600.0;
        return true;
    }

    private static (int Whole, int Minutes, double Seconds) Split(double value)
    {
        int whole = (int)Math.Floor(value);
        double remainderMinutes = (value - whole) * 60.0;
        int minutes = (int)Math.Floor(remainderMinutes);
        double seconds = (remainderMinutes - minutes) * 60.0;
        return (whole, minutes, seconds);
    }

    private static double Normalise360(double degrees)
    {
        double wrapped = degrees % 360.0;
        return wrapped < 0.0 ? wrapped + 360.0 : wrapped;
    }
}
