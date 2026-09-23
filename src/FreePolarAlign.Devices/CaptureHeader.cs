using System.Globalization;
using FreePolarAlign.Imaging.Fits;

namespace FreePolarAlign.Devices;

/// <summary>
/// The FITS keywords every captured frame carries, so that a file found on its
/// own is still interpretable.
///
/// This exists because a frame without provenance costs real time. Diagnosing a
/// failed night meant working out, from pixel statistics alone, which software
/// wrote a file, at what exposure, through what focal length, and roughly where
/// it pointed -- all of it known at the moment of capture and none of it
/// written down. Every keyword here answers a question that was actually asked
/// of a file after the fact.
///
/// The names are the conventional ones, so that other astronomy software reads
/// them without being told: DATE-OBS, EXPTIME, INSTRUME, FOCALLEN, XPIXSZ and
/// so on. Two choices are worth stating:
///
/// The mount's position goes in <c>RA</c>/<c>DEC</c> and <c>OBJCTRA</c>/
/// <c>OBJCTDEC</c>, never in <c>CRVAL1</c>/<c>CRVAL2</c>. The CRVAL keywords
/// mean "this frame has been solved and its centre is here", and a mount's
/// belief is not a solution -- on a misaligned mount it is wrong by exactly the
/// error being measured. Writing it there would make every capture look like a
/// plate solution to any program that read it, this one included.
///
/// <c>DATE-AVG</c> carries the exposure midpoint as well as <c>DATE-OBS</c>
/// carrying the start. The midpoint is the timestamp this project computes with
/// (a star moves 15 arcseconds a second, so the difference matters at the
/// accuracy being claimed), and a file that recorded only the start would lose
/// the number that was actually used.
/// </summary>
public static class CaptureHeader
{
    /// <param name="existing">
    /// Keywords the provider has already set, kept as they are. A driver that
    /// reports something this does not know about should not lose it.
    /// </param>
    public static FitsHeader Build(
        FitsHeader? existing,
        ICamera camera,
        TimeSpan exposure,
        DateTime startUtc,
        DateTime midpointUtc,
        CaptureContext? context)
    {
        var header = new FitsHeader();

        if (existing is not null)
        {
            foreach (FitsCard card in existing.Cards)
            {
                if (card.Value is not null)
                {
                    header.Set(card.Keyword, card.Value, card.Comment);
                }
            }
        }

        if (context?.ApplicationName is { Length: > 0 } application)
        {
            header.Set("CREATOR", application, "capture software");

            if (context.ApplicationVersion is { Length: > 0 } version)
            {
                header.Set("SWCREATE", $"{application} {version}", "capture software and version");
                header.Set("SWVERS", version, "capture software version");
            }
        }

        header.Set("DATE-OBS", Timestamp(startUtc), "exposure start, UTC");
        header.Set("DATE-AVG", Timestamp(midpointUtc), "exposure midpoint, UTC");
        header.Set("EXPTIME", exposure.TotalSeconds, "exposure, seconds");
        header.Set("EXPOSURE", exposure.TotalSeconds, "exposure, seconds");

        header.Set("INSTRUME", camera.Name, "camera");
        header.Set("XPIXSZ", camera.PixelSizeMicrons, "pixel size, microns");
        header.Set("YPIXSZ", camera.PixelSizeMicrons, "pixel size, microns");

        if (camera.ReadoutModeIndex is { } modeIndex)
        {
            CameraReadoutMode? mode = camera.ReadoutModes.FirstOrDefault(m => m.Index == modeIndex);
            if (mode is not null)
            {
                header.Set("READOUTM", mode.Name, "readout mode");
            }
        }

        if (context?.FocalLengthMillimetres is { } focalLength && focalLength > 0.0)
        {
            header.Set("FOCALLEN", focalLength, "focal length, mm");

            // Derived rather than measured, and said so: this is what the
            // entered focal length implies, not what a solve found.
            double scale = 206.264806247 * camera.PixelSizeMicrons / focalLength;
            header.Set("PIXSCALE", scale, "arcsec/pixel implied by FOCALLEN");
        }

        if (context?.MountRaDegrees is { } ra && context.MountDecDegrees is { } dec)
        {
            header.Set("RA", ra, "mount's reported RA, degrees (not a solve)");
            header.Set("DEC", dec, "mount's reported Dec, degrees (not a solve)");
            header.Set("OBJCTRA", FormatRightAscension(ra), "mount's reported RA (not a solve)");
            header.Set("OBJCTDEC", FormatDeclination(dec), "mount's reported Dec (not a solve)");
        }

        return header;
    }

    /// <summary>ISO 8601 to the millisecond, which is the FITS convention for DATE-* keywords.</summary>
    private static string Timestamp(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>Sexagesimal hours, the form OBJCTRA is conventionally written in.</summary>
    internal static string FormatRightAscension(double raDegrees)
    {
        (int hours, int minutes, double seconds) = Sexagesimal(Wrap(raDegrees, 360.0) / 15.0, secondDecimals: 2);

        // A value a hair under 24h can round up into it.
        hours %= 24;

        return string.Create(CultureInfo.InvariantCulture, $"{hours:D2} {minutes:D2} {seconds:00.00}");
    }

    /// <summary>Sexagesimal degrees with an explicit sign, the form OBJCTDEC is conventionally written in.</summary>
    internal static string FormatDeclination(double decDegrees)
    {
        char sign = decDegrees < 0.0 ? '-' : '+';
        (int degrees, int minutes, double seconds) = Sexagesimal(Math.Abs(decDegrees), secondDecimals: 1);

        return string.Create(CultureInfo.InvariantCulture, $"{sign}{degrees:D2} {minutes:D2} {seconds:00.0}");
    }

    /// <summary>
    /// Splits a positive value into whole units, whole minutes and seconds,
    /// rounding the seconds first and then carrying.
    ///
    /// Carrying is the whole point and is easy to leave out: 61.15 degrees is
    /// 61 degrees 9 minutes exactly, but arrives as 8 minutes and 59.999...
    /// seconds, which rounds to "61 08 60.0" -- a value no astronomy program
    /// will read back as what was meant, and one that looks right at a glance.
    /// </summary>
    private static (int Units, int Minutes, double Seconds) Sexagesimal(double value, int secondDecimals)
    {
        int units = (int)value;
        double remainingMinutes = (value - units) * 60.0;
        int minutes = (int)remainingMinutes;
        double seconds = Math.Round((remainingMinutes - minutes) * 60.0, secondDecimals, MidpointRounding.AwayFromZero);

        if (seconds >= 60.0)
        {
            seconds -= 60.0;
            minutes++;
        }

        if (minutes >= 60)
        {
            minutes -= 60;
            units++;
        }

        return (units, minutes, seconds);
    }

    private static double Wrap(double value, double period)
    {
        double wrapped = value % period;
        return wrapped < 0.0 ? wrapped + period : wrapped;
    }
}
