using FreePolarAlign.Devices;
using FreePolarAlign.Imaging.Fits;
using Xunit;

namespace FreePolarAlign.Tests.Devices;

/// <summary>
/// Turning a vendor SDK's byte buffer into a frame. The two things that can go
/// wrong both produce plausible-looking frames -- a byte-swapped 16-bit frame
/// is noise with stars in it, a row-flipped one solves as a mirror image -- so
/// each is pinned with a buffer whose right answer is unambiguous.
/// </summary>
public sealed class NativeFrameTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("fpa-native-frame-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void SixteenBitPixelsAreLittleEndian()
    {
        // 0x1234 stored low byte first, as both SDKs document.
        double[,] pixels = NativeFrame.ToPixels(new byte[] { 0x34, 0x12 }, 1, 1, 2);

        Assert.Equal(0x1234, pixels[0, 0]);
    }

    /// <summary>
    /// The first row the camera sends is pixel row 0, as the ASCOM path writes
    /// it -- the path whose frames have actually been solved against real sky.
    /// </summary>
    [Fact]
    public void TheFirstRowSentIsRowZero()
    {
        byte[] buffer = { 1, 2, 3, 4, 5, 6 }; // 3 wide, 2 high, 8-bit

        double[,] pixels = NativeFrame.ToPixels(buffer, 3, 2, 1);

        Assert.Equal(new double[] { 1, 2, 3 }, new[] { pixels[0, 0], pixels[0, 1], pixels[0, 2] });
        Assert.Equal(new double[] { 4, 5, 6 }, new[] { pixels[1, 0], pixels[1, 1], pixels[1, 2] });
    }

    [Fact]
    public void AShortBufferIsRefused_NotReadPastItsEnd() =>
        Assert.Throws<ArgumentException>(() => NativeFrame.ToPixels(new byte[5], 3, 2, 1));

    /// <summary>
    /// The frame lands at the readout's own depth (D20) and records the gain it
    /// was taken at -- the number nobody could supply when an over-exposed
    /// night was being diagnosed.
    /// </summary>
    [Fact]
    public void AWrittenFrameIsSixteenBit_AndRecordsItsGain()
    {
        var pixels = new double[4, 4];
        pixels[1, 2] = 4000.0;
        var camera = new GainCamera();

        CapturedImage captured = NativeFrame.Write(
            camera, pixels, 16, TimeSpan.FromSeconds(0.5), new DateTime(2026, 9, 24, 21, 0, 0, DateTimeKind.Utc),
            null, _directory);

        FitsImage written = FitsFile.Read(captured.FitsPath);
        Assert.Equal(FitsBitPix.Int16, written.BitPix);
        Assert.Equal(4000.0, written.Pixels[1, 2]);
        Assert.Contains(written.ExtraHeader!.Cards, card => card.Keyword == "GAIN" && Convert.ToDouble(card.Value) == 250.0);
        Assert.Equal(new DateTime(2026, 9, 24, 21, 0, 0, 250, DateTimeKind.Utc), captured.ExposureMidpointUtc);
    }

    private sealed class GainCamera : ICamera
    {
        public string Name => "Gain Camera";

        public int SensorWidthPixels => 4;

        public int SensorHeightPixels => 4;

        public double PixelSizeMicrons => 2.9;

        public bool IsConnected => true;

        public IReadOnlyList<CameraReadoutMode> ReadoutModes => Array.Empty<CameraReadoutMode>();

        public int? ReadoutModeIndex => null;

        public CameraGainRange? GainRange => new(0, 600);

        public int? Gain => 250;

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
}
