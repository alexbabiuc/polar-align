using System.Text;
using FreePolarAlign.Devices;
using FreePolarAlign.Devices.ToupTek;
using FreePolarAlign.Devices.Zwo;
using Xunit;

namespace FreePolarAlign.Tests.Devices;

/// <summary>
/// Everything about the native-SDK plugins that is not a native call: names,
/// serial numbers, exposure units, the device array. Testable with no camera
/// and no vendor library, which is the only way any of it is testable here.
/// </summary>
public class NativeSdkMappingTests
{
    // ---- Naming duplicates ----

    [Fact]
    public void AUniqueNameIsLeftAsPrinted() =>
        Assert.Equal(new[] { "ZWO ASI290MM Mini" }, DeviceNaming.Disambiguate(new[] { "ZWO ASI290MM Mini" }));

    /// <summary>
    /// Two cameras of one model get numbers, in enumeration order, and only
    /// they do: the common single-camera case keeps the name on the case.
    /// </summary>
    [Fact]
    public void DuplicatesAreNumbered_AndOnlyDuplicates()
    {
        IReadOnlyList<string> names = DeviceNaming.Disambiguate(
            new[] { "ZWO ASI290MM Mini", "ZWO ASI120MM", "ZWO ASI290MM Mini" });

        Assert.Equal(new[] { "ZWO ASI290MM Mini #1", "ZWO ASI120MM", "ZWO ASI290MM Mini #2" }, names);
    }

    // ---- ZWO ----

    [Fact]
    public void AZwoSerialNumberIsPrintedInHex() =>
        Assert.Equal(
            "1A2B3C4D5E6F7081",
            ZwoMapping.FormatSerialNumber(new byte[] { 0x1A, 0x2B, 0x3C, 0x4D, 0x5E, 0x6F, 0x70, 0x81 }));

    /// <summary>
    /// Eight zero bytes is no serial at all. Used as a settings key it would
    /// give every serial-less camera one shared set of settings.
    /// </summary>
    [Fact]
    public void AnAllZeroZwoSerialNumberIsNone() =>
        Assert.Null(ZwoMapping.FormatSerialNumber(new byte[8]));

    [Fact]
    public void AFixedCStringStopsAtTheTerminator()
    {
        byte[] field = new byte[64];
        Encoding.ASCII.GetBytes("ZWO ASI290MM Mini").CopyTo(field, 0);
        field[40] = (byte)'x'; // garbage after the terminator must not leak in

        Assert.Equal("ZWO ASI290MM Mini", ZwoMapping.ReadCString(field));
    }

    /// <summary>
    /// Microseconds, and never zero: the SDK reads zero as "keep the last
    /// value", which would silently repeat the previous frame's exposure.
    /// </summary>
    [Theory]
    [InlineData(0.1, 100_000)]
    [InlineData(2.0, 2_000_000)]
    [InlineData(0.0, 1)]
    public void ZwoExposureIsInMicroseconds(double seconds, long expected) =>
        Assert.Equal(expected, ZwoMapping.ExposureMicroseconds(TimeSpan.FromSeconds(seconds)));

    // ---- ToupTek ----

    /// <summary>
    /// The enumeration array parsed at both layouts, with a second entry so a
    /// wrong stride shows up as the second camera coming back garbled.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheToupTekDeviceArrayIsParsedAtEitherLayout(bool wideStrings)
    {
        (int stride, int idOffset, int fieldBytes) = ToupTekMapping.DeviceLayout(wideStrings);
        byte[] buffer = new byte[stride * 2];
        Encoding encoding = wideStrings ? Encoding.Unicode : Encoding.UTF8;

        void Put(int entry, int offset, string text) =>
            encoding.GetBytes(text).CopyTo(buffer, (entry * stride) + offset);

        Put(0, 0, "ATR585M");
        Put(0, idOffset, "tp-1-2-3");
        Put(1, 0, "G3M678M");
        Put(1, idOffset, "tp-1-4-7");

        IReadOnlyList<ToupTekMapping.EnumeratedCamera> cameras = ToupTekMapping.ParseDevices(buffer, 2, wideStrings);

        Assert.Equal(2, cameras.Count);
        Assert.Equal(new ToupTekMapping.EnumeratedCamera("ATR585M", "tp-1-2-3"), cameras[0]);
        Assert.Equal(new ToupTekMapping.EnumeratedCamera("G3M678M", "tp-1-4-7"), cameras[1]);
        Assert.True(fieldBytes <= idOffset, "the name field must end before the id begins");
    }

    [Fact]
    public void AToupTekSerialNumberIsReadAsPlainText()
    {
        byte[] buffer = new byte[32];
        Encoding.ASCII.GetBytes("ZP250212241204105").CopyTo(buffer, 0);

        Assert.Equal("ZP250212241204105", ToupTekMapping.ReadSerialNumber(buffer));
        Assert.Null(ToupTekMapping.ReadSerialNumber(new byte[32]));
    }

    [Fact]
    public void ToupTekExposureIsNeverZero() =>
        Assert.Equal(1u, ToupTekMapping.ExposureMicroseconds(TimeSpan.Zero));
}
