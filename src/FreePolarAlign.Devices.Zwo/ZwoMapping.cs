using System.Text;

namespace FreePolarAlign.Devices.Zwo;

/// <summary>
/// The parts of talking to the ZWO SDK that are plain data handling, kept apart
/// from the native calls so they can be tested on a machine with no camera and
/// no library.
/// </summary>
public static class ZwoMapping
{
    /// <summary>Reads a fixed-size, NUL-terminated C string field.</summary>
    public static string ReadCString(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? field : field[..end]).Trim();
    }

    /// <summary>
    /// The camera's serial number as ZWO prints it -- the eight bytes in
    /// hexadecimal, per the SDK's own note on <c>ASIGetSerialNumber</c> -- or
    /// null when the camera reports none.
    ///
    /// All zeros is treated as none. Older cameras without a serial return
    /// success with an empty buffer on some SDK versions, and eight zeros used
    /// as a settings key would make every such camera share one set of
    /// settings.
    /// </summary>
    public static string? FormatSerialNumber(ReadOnlySpan<byte> serial)
    {
        if (serial.Length == 0 || serial.IndexOfAnyExcept((byte)0) < 0)
        {
            return null;
        }

        return Convert.ToHexString(serial);
    }

    /// <summary>
    /// Exposure in the unit the SDK takes, microseconds, never below one: the
    /// SDK treats zero as "use the last value", which would silently repeat
    /// whatever the previous frame used.
    /// </summary>
    public static long ExposureMicroseconds(TimeSpan duration) =>
        Math.Max(1L, (long)Math.Round(duration.TotalMilliseconds * 1000.0));

    /// <summary>
    /// The bins listed in <c>ASI_CAMERA_INFO.SupportedBins</c>, which the SDK
    /// ends with a 0. Sorted and without repeats, and 1 always included: every
    /// camera reads out unbinned, and a list without it would leave no way back.
    /// </summary>
    public static IReadOnlyList<int> SupportedBins(ReadOnlySpan<int> field)
    {
        var bins = new SortedSet<int> { 1 };
        foreach (int bin in field)
        {
            if (bin <= 0)
            {
                break;
            }

            bins.Add(bin);
        }

        return bins.ToArray();
    }

    /// <summary>
    /// The frame size to ask <c>ASISetROIFormat</c> for at a binning: the whole
    /// sensor in binned pixels, cut down to what the SDK accepts. Its manual
    /// requires the width to be a multiple of 8 and the height of 2, and it
    /// refuses anything else; the columns and rows that do not fit are lost at
    /// the edge, as with any camera's binning.
    /// </summary>
    public static (int Width, int Height) BinnedFrameSize(int sensorWidth, int sensorHeight, int bin)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bin, 1);
        return (sensorWidth / bin / 8 * 8, sensorHeight / bin / 2 * 2);
    }
}
