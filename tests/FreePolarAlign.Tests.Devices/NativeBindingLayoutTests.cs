using System.Runtime.InteropServices;
using FreePolarAlign.Devices.ToupTek;
using FreePolarAlign.Devices.Zwo;
using Xunit;

namespace FreePolarAlign.Tests.Devices;

/// <summary>
/// The native struct layouts, which is where a vendor-SDK binding goes wrong
/// silently.
///
/// A struct declared a few bytes short is not an error anywhere: the SDK writes
/// its full size into memory the runtime handed over as smaller, and the damage
/// shows up later as a corrupted neighbour or a crash nowhere near the cause.
/// The ZWO structs are the risky ones because they carry C <c>long</c> fields,
/// which are 32 bits on Windows and 64 elsewhere, so the right size is
/// genuinely different per platform.
///
/// The expected numbers were not worked out by hand. They were checked against
/// the real vendor headers with the C compiler, for both the Windows x64 and the
/// macOS/Linux ABI, using compile-time assertions that were confirmed to fail on
/// a wrong value. These tests pin the managed declarations to those numbers on
/// whichever platform they run.
/// </summary>
public class NativeBindingLayoutTests
{
    private static readonly bool Windows = OperatingSystem.IsWindows();

    [Fact]
    public void TheZwoCameraInfoMatchesTheHeader()
    {
        Assert.Equal(Windows ? 240 : 248, Marshal.SizeOf<AsiNative.CameraInfo>());
        Assert.Equal(Windows ? 68 : 72, (int)Marshal.OffsetOf<AsiNative.CameraInfo>(nameof(AsiNative.CameraInfo.MaxHeight)));
        Assert.Equal(Windows ? 184 : 192, (int)Marshal.OffsetOf<AsiNative.CameraInfo>(nameof(AsiNative.CameraInfo.PixelSize)));
        Assert.Equal(Windows ? 216 : 224, (int)Marshal.OffsetOf<AsiNative.CameraInfo>(nameof(AsiNative.CameraInfo.BitDepth)));
    }

    [Fact]
    public void TheZwoControlCapsMatchTheHeader()
    {
        Assert.Equal(Windows ? 248 : 264, Marshal.SizeOf<AsiNative.ControlCaps>());
        Assert.Equal(192, (int)Marshal.OffsetOf<AsiNative.ControlCaps>(nameof(AsiNative.ControlCaps.MaxValue)));
        Assert.Equal(Windows ? 212 : 224, (int)Marshal.OffsetOf<AsiNative.ControlCaps>(nameof(AsiNative.ControlCaps.ControlType)));
    }

    [Fact]
    public void TheZwoSerialNumberIsEightBytes() =>
        Assert.Equal(8, Marshal.SizeOf<AsiNative.SerialNumber>());

    [Fact]
    public void TheToupTekFrameInfoMatchesTheHeader() =>
        Assert.Equal(24, Marshal.SizeOf<ToupcamNative.FrameInfoV2>());

    /// <summary>
    /// The ToupTek enumeration array is parsed by hand, so its layout is a pair
    /// of numbers rather than a struct; these are the numbers the compiler gave.
    /// </summary>
    [Theory]
    [InlineData(true, 264, 128)]
    [InlineData(false, 136, 64)]
    public void TheToupTekDeviceEntryMatchesTheHeader(bool wideStrings, int stride, int idOffset)
    {
        (int actualStride, int actualIdOffset, _) = ToupTekMapping.DeviceLayout(wideStrings);

        Assert.Equal(stride, actualStride);
        Assert.Equal(idOffset, actualIdOffset);
    }
}
