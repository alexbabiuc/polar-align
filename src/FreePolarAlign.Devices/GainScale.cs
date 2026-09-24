namespace FreePolarAlign.Devices;

/// <summary>
/// Maps a camera's own gain range onto 0 to 100 and back.
///
/// The control the user sees is a percentage because the raw ranges are not
/// comparable and mostly not meaningful to a person: an ASI290 counts 0 to 600
/// in tenths of a decibel, a ToupTek camera 100 to several thousand in percent
/// of unity. A percentage of *this camera's* range says the one thing that
/// transfers between cameras -- how far towards maximum it is turned -- and the
/// raw value is still shown beside it for anyone matching another program.
///
/// Whole percents only, and that is enough: on the widest range seen here a
/// step of one percent is 49 raw units, and gain is set once per session by eye.
/// A percentage stored for one camera also means the same thing on a
/// replacement camera of a different model, which a raw value would not.
/// </summary>
public static class GainScale
{
    public const int MinimumPercent = 0;

    public const int MaximumPercent = 100;

    /// <summary>The raw value <paramref name="percent"/> of the way from minimum to maximum, rounded to the nearest whole unit.</summary>
    public static int ToRaw(int percent, CameraGainRange range)
    {
        ArgumentNullException.ThrowIfNull(range);

        int clamped = Math.Clamp(percent, MinimumPercent, MaximumPercent);
        double raw = range.Minimum + ((range.Maximum - range.Minimum) * clamped / 100.0);
        return range.Clamp((int)Math.Round(raw, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// How far <paramref name="raw"/> is from minimum to maximum, as a whole
    /// percent. A degenerate range -- a camera reporting one legal gain -- reads
    /// as zero rather than dividing by nothing.
    /// </summary>
    public static int ToPercent(int raw, CameraGainRange range)
    {
        ArgumentNullException.ThrowIfNull(range);

        int span = range.Maximum - range.Minimum;
        if (span <= 0)
        {
            return MinimumPercent;
        }

        double percent = (range.Clamp(raw) - range.Minimum) * 100.0 / span;
        return Math.Clamp((int)Math.Round(percent, MidpointRounding.AwayFromZero), MinimumPercent, MaximumPercent);
    }
}
