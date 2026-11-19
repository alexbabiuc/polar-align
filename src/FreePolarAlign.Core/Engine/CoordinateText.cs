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
/// Right ascension is read in hours in both forms: "10:30:00" and "10.5" are
/// both 157.5 degrees. Until D44 a bare number was read as degrees, the form a
/// solve result gives; it was changed so that every coordinate field reads a
/// decimal in the unit it displays, and the UI's label says which unit that is.
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
    /// Parses a right ascension written in hours, sexagesimal ("10:30:00") or
    /// decimal ("10.5"). Returns degrees in [0, 360).
    /// </summary>
    public static bool TryParseRightAscension(string? text, out double degrees)
    {
        degrees = 0.0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        double hours;

        if (!LooksSexagesimal(trimmed))
        {
            if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out hours)
                || !double.IsFinite(hours))
            {
                return false;
            }
        }
        else if (!TryParseSexagesimal(trimmed, out hours, out bool negative) || negative)
        {
            return false;
        }

        // Negative, or 24 hours and beyond, is refused rather than wrapped: a
        // typo wrapped into a legal value is a target on the far side of the sky.
        if (hours is < 0.0 or >= 24.0)
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
    public static bool TryParseDeclination(string? text, out double degrees) =>
        TryParseDegrees(text, 90.0, out degrees);

    /// <summary>
    /// Parses an angle in degrees -- a declination, a latitude, a longitude --
    /// written either as degrees, arcminutes and arcseconds ("-12:30:00",
    /// "-12 30") or as decimal degrees ("-12.5"). Anything whose magnitude
    /// exceeds <paramref name="limit"/> is refused, not clamped.
    /// </summary>
    public static bool TryParseDegrees(string? text, double limit, out double degrees)
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
                || !double.IsFinite(asDegrees)
                || Math.Abs(asDegrees) > limit)
            {
                return false;
            }

            degrees = asDegrees;
            return true;
        }

        if (!TryParseSexagesimal(trimmed, out double magnitude, out bool negative) || magnitude > limit)
        {
            return false;
        }

        degrees = negative ? -magnitude : magnitude;
        return true;
    }

    /// <summary>
    /// Right ascension as "hh:mm:ss" -- "10:30:00", "05:30:00" -- the hours counterpart of
    /// <see cref="FormatDegrees"/>, in whole seconds of time (D44). A value that
    /// rounds up to 24 hours is shown as 00:00:00, which is where it is.
    /// </summary>
    public static string FormatRightAscension(double degrees)
    {
        long totalSeconds = (long)Math.Round(Normalise360(degrees) / 15.0 * 3600.0, MidpointRounding.AwayFromZero)
                            % (24 * 3600);
        return FormattableString.Invariant($"{totalSeconds / 3600:00}:{totalSeconds / 60 % 60:00}:{totalSeconds % 60:00}");
    }

    /// <summary>
    /// An angle in degrees as "dd:mm:ss" -- "46:46:12", "-00:30:00" -- which is how
    /// every coordinate in degrees is shown to the user. Whole arcseconds: a
    /// latitude off by the half-arcsecond this can round away biases the
    /// altitude reading by under a hundredth of an arcminute. The sign belongs
    /// to the whole value, as <see cref="TryParseDegrees"/> reads it back, and
    /// is dropped once rounding has made the value zero.
    /// </summary>
    public static string FormatDegrees(double degrees)
    {
        long totalSeconds = (long)Math.Round(Math.Abs(degrees) * 3600.0, MidpointRounding.AwayFromZero);
        string sign = degrees < 0.0 && totalSeconds > 0 ? "-" : string.Empty;
        long whole = totalSeconds / 3600;
        long minutes = totalSeconds / 60 % 60;
        long seconds = totalSeconds % 60;
        return FormattableString.Invariant($"{sign}{whole:00}:{minutes:00}:{seconds:00}");
    }

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

    private static double Normalise360(double degrees)
    {
        double wrapped = degrees % 360.0;
        return wrapped < 0.0 ? wrapped + 360.0 : wrapped;
    }
}
