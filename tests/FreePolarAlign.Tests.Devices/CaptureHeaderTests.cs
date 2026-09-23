using FreePolarAlign.Devices;
using FreePolarAlign.Imaging.Fits;
using Xunit;

namespace FreePolarAlign.Tests.Devices;

/// <summary>
/// What a captured frame says about itself.
///
/// These keywords exist because their absence cost time: diagnosing a failed
/// night meant deducing from pixel statistics alone which program wrote a file,
/// at what exposure, through what focal length and roughly where it pointed --
/// every one of which was known when the shutter closed. The tests pin down the
/// two things that would quietly undo that: a keyword going missing, and the
/// mount's *belief* being written somewhere that makes it look like a
/// measurement.
/// </summary>
public class CaptureHeaderTests
{
    private sealed class FakeCamera : ICamera
    {
        public string Name { get; init; } = "ZWO ASI290MM Mini";

        public int SensorWidthPixels => 1936;

        public int SensorHeightPixels => 1096;

        public double PixelSizeMicrons { get; init; } = 2.9;

        public bool IsConnected => true;

        public IReadOnlyList<CameraReadoutMode> ReadoutModes { get; init; } = Array.Empty<CameraReadoutMode>();

        public int? ReadoutModeIndex { get; init; }

        public Task SetReadoutModeAsync(int index, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<CapturedImage> ExposeAsync(
            TimeSpan duration, CaptureContext? context = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private static readonly DateTime Start = new(2026, 9, 23, 20, 6, 33, DateTimeKind.Utc);

    private static FitsHeader Build(CaptureContext? context, ICamera? camera = null) =>
        CaptureHeader.Build(
            existing: null,
            camera ?? new FakeCamera(),
            TimeSpan.FromSeconds(2),
            Start,
            Start.AddSeconds(1),
            context);

    private static string? Value(FitsHeader header, string keyword) =>
        header.Cards.FirstOrDefault(c => c.Keyword == keyword)?.Value?.ToString();

    [Fact]
    public void TheFrameNamesTheApplicationAndItsVersion()
    {
        FitsHeader header = Build(new CaptureContext("free-polar-align", "1.1.0+55aba32"));

        Assert.Equal("free-polar-align", Value(header, "CREATOR"));
        Assert.Equal("1.1.0+55aba32", Value(header, "SWVERS"));
        Assert.Contains("1.1.0+55aba32", Value(header, "SWCREATE")!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFrameNamesTheCameraAndItsPixels()
    {
        FitsHeader header = Build(null, new FakeCamera { Name = "ASCOM.ASICamera2.Camera", PixelSizeMicrons = 2.9 });

        Assert.Equal("ASCOM.ASICamera2.Camera", Value(header, "INSTRUME"));
        Assert.Equal(2.9, double.Parse(Value(header, "XPIXSZ")!), precision: 6);
        Assert.Equal(2.9, double.Parse(Value(header, "YPIXSZ")!), precision: 6);
    }

    /// <summary>
    /// Start *and* midpoint. The midpoint is what this project computes with --
    /// a star moves 15 arcseconds a second, which is three pixels here -- and a
    /// file recording only the start would lose the number actually used.
    /// </summary>
    [Fact]
    public void TheFrameCarriesBothTheStartAndTheMidpoint()
    {
        FitsHeader header = Build(null);

        Assert.Equal("2026-09-23T20:06:33.000", Value(header, "DATE-OBS"));
        Assert.Equal("2026-09-23T20:06:34.000", Value(header, "DATE-AVG"));
        Assert.Equal(2.0, double.Parse(Value(header, "EXPTIME")!), precision: 6);
    }

    [Fact]
    public void TheFrameCarriesTheFocalLengthAndWhatItImplies()
    {
        FitsHeader header = Build(new CaptureContext(FocalLengthMillimetres: 105.0));

        Assert.Equal(105.0, double.Parse(Value(header, "FOCALLEN")!), precision: 6);

        // 2.9 micron pixels at 105 mm: the 5.70"/pixel this project reports.
        Assert.Equal(5.70, double.Parse(Value(header, "PIXSCALE")!), precision: 2);
    }

    /// <summary>
    /// The mount's reported position, in keywords that do not claim to be a
    /// solution. CRVAL1/2 mean "this frame was solved and its centre is here",
    /// and on a misaligned mount the mount's belief is wrong by exactly the
    /// error being measured -- writing it there would make every capture look
    /// like a plate solution to anything that read it, including this software.
    /// </summary>
    [Fact]
    public void TheMountsBeliefIsRecordedWithoutPretendingToBeASolve()
    {
        FitsHeader header = Build(new CaptureContext(MountRaDegrees: 281.125, MountDecDegrees: 61.15));

        Assert.Equal(281.125, double.Parse(Value(header, "RA")!), precision: 6);
        Assert.Equal(61.15, double.Parse(Value(header, "DEC")!), precision: 6);
        Assert.Equal("18 44 30.00", Value(header, "OBJCTRA"));
        Assert.Equal("+61 09 00.0", Value(header, "OBJCTDEC"));

        Assert.Null(Value(header, "CRVAL1"));
        Assert.Null(Value(header, "CRVAL2"));
        Assert.Null(Value(header, "CTYPE1"));
    }

    [Fact]
    public void ASouthernDeclinationKeepsItsSign()
    {
        FitsHeader header = Build(new CaptureContext(MountRaDegrees: 0.5, MountDecDegrees: -33.9));

        Assert.Equal("-33 54 00.0", Value(header, "OBJCTDEC"));
        Assert.Equal("00 02 00.00", Value(header, "OBJCTRA"));
    }

    /// <summary>
    /// Everything the caller cannot supply is simply absent. An unknown focal
    /// length written as zero would be read back as a measurement of zero.
    /// </summary>
    [Fact]
    public void WhatIsNotKnownIsNotWritten()
    {
        FitsHeader header = Build(null);

        Assert.Null(Value(header, "FOCALLEN"));
        Assert.Null(Value(header, "PIXSCALE"));
        Assert.Null(Value(header, "RA"));
        Assert.Null(Value(header, "OBJCTRA"));
        Assert.Null(Value(header, "CREATOR"));

        // But what the camera itself knows is always there.
        Assert.NotNull(Value(header, "INSTRUME"));
        Assert.NotNull(Value(header, "DATE-OBS"));
    }

    [Fact]
    public void KeywordsTheProviderAlreadySetAreKept()
    {
        var existing = new FitsHeader();
        existing.Set("GAIN", 75.0, "driver gain");

        FitsHeader header = CaptureHeader.Build(
            existing, new FakeCamera(), TimeSpan.FromSeconds(2), Start, Start.AddSeconds(1), null);

        Assert.Equal(75.0, double.Parse(Value(header, "GAIN")!), precision: 6);
    }

    [Fact]
    public void TheReadoutModeIsNamedWhenTheCameraOffersOne()
    {
        var camera = new FakeCamera
        {
            ReadoutModes = new[] { new CameraReadoutMode(0, "High Speed", 8), new CameraReadoutMode(1, "Normal", 16) },
            ReadoutModeIndex = 1,
        };

        Assert.Equal("Normal", Value(Build(null, camera), "READOUTM"));
    }

    /// <summary>The whole header has to survive a round trip through a real file, not just exist in memory.</summary>
    [Fact]
    public void TheKeywordsSurviveTheFile()
    {
        FitsHeader header = Build(new CaptureContext(
            "free-polar-align", "1.1.0", MountRaDegrees: 281.125, MountDecDegrees: 61.15, FocalLengthMillimetres: 105.0));

        var pixels = new double[4, 4];
        using var stream = new MemoryStream();
        FitsFile.Write(stream, new FitsImage(4, 4, FitsBitPix.Int16, 32768.0, 1.0, pixels, header));
        stream.Position = 0;

        FitsImage read = FitsFile.Read(stream);

        Assert.Equal("free-polar-align", Value(read.ExtraHeader, "CREATOR"));
        Assert.Equal("ZWO ASI290MM Mini", Value(read.ExtraHeader, "INSTRUME"));
        Assert.Equal("18 44 30.00", Value(read.ExtraHeader, "OBJCTRA"));
        Assert.Equal(105.0, double.Parse(Value(read.ExtraHeader, "FOCALLEN")!), precision: 6);
        Assert.Equal("2026-09-23T20:06:34.000", Value(read.ExtraHeader, "DATE-AVG"));
    }
}
