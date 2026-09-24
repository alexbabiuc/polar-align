using System.Text;

namespace FreePolarAlign.Devices.ToupTek;

/// <summary>
/// The data handling around the ToupTek SDK, kept apart from the native calls
/// so it can be tested with no camera and no library.
/// </summary>
public static class ToupTekMapping
{
    /// <summary>
    /// Bytes per <c>ToupcamDeviceV2</c> entry and where its fields sit.
    ///
    /// On Windows the two name fields are 64 <c>wchar_t</c> each (128 bytes),
    /// elsewhere 64 <c>char</c>; the model pointer follows. Checked against the
    /// real header with the C compiler for both ABIs: 264 bytes with the id at
    /// 128 on Windows x64, 136 bytes with the id at 64 on macOS and Linux. A
    /// wrong stride here reads the second camera's name out of the first one's
    /// pointer.
    /// </summary>
    public static (int Stride, int IdOffset, int FieldBytes) DeviceLayout(bool wideStrings) =>
        wideStrings ? (264, 128, 128) : (136, 64, 64);

    /// <summary>One enumerated camera: what to show, and what to hand <c>Toupcam_Open</c>.</summary>
    public sealed record EnumeratedCamera(string DisplayName, string Id);

    /// <summary>Parses the array <c>Toupcam_EnumV2</c> fills.</summary>
    public static IReadOnlyList<EnumeratedCamera> ParseDevices(ReadOnlySpan<byte> buffer, int count, bool wideStrings)
    {
        (int stride, int idOffset, int fieldBytes) = DeviceLayout(wideStrings);
        var cameras = new List<EnumeratedCamera>(count);

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = buffer.Slice(i * stride, stride);
            string name = ReadString(entry[..fieldBytes], wideStrings);
            string id = ReadString(entry.Slice(idOffset, fieldBytes), wideStrings);

            if (id.Length > 0)
            {
                cameras.Add(new EnumeratedCamera(name.Length > 0 ? name : id, id));
            }
        }

        return cameras;
    }

    /// <summary>
    /// The serial number <c>Toupcam_get_SerialNumber</c> writes -- a plain
    /// <c>char[32]</c> on every platform, unlike the names -- or null when
    /// empty.
    /// </summary>
    public static string? ReadSerialNumber(ReadOnlySpan<byte> buffer)
    {
        string serial = ReadString(buffer, wide: false);
        return serial.Length > 0 ? serial : null;
    }

    /// <summary>Microseconds, never zero, and within the <c>unsigned</c> the SDK takes.</summary>
    public static uint ExposureMicroseconds(TimeSpan duration) =>
        (uint)Math.Clamp(Math.Round(duration.TotalMilliseconds * 1000.0), 1.0, uint.MaxValue);

    private static string ReadString(ReadOnlySpan<byte> field, bool wide)
    {
        if (wide)
        {
            int end = 0;
            while (end + 1 < field.Length && (field[end] != 0 || field[end + 1] != 0))
            {
                end += 2;
            }

            return Encoding.Unicode.GetString(field[..end]).Trim();
        }

        int nul = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(nul < 0 ? field : field[..nul]).Trim();
    }
}
