using FreePolarAlign.Imaging.Fits;
using Xunit;

namespace FreePolarAlign.Tests.Imaging;

/// <summary>
/// How a captured frame is packaged onto disk.
///
/// This looks like a detail of file formatting and is not. The ASCOM capture
/// path wrote BITPIX 32, reasoning that ASCOM hands back an Int32 array and
/// widening loses no data -- which is true, and which cost every solve against
/// real hardware: Watney detects zero stars in such a file, so a night's
/// captures all failed with NoStarsDetected while the simulator, writing 16-bit,
/// solved perfectly. Nothing in the suite noticed, because nothing checked what
/// the capture path actually wrote.
/// </summary>
public class CapturedFrameTests
{
    private static double[,] Frame(params double[] values)
    {
        var pixels = new double[1, values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            pixels[0, i] = values[i];
        }

        return pixels;
    }

    private static FitsImage RoundTrip(FitsImage image)
    {
        using var stream = new MemoryStream();
        FitsFile.Write(stream, image);
        stream.Position = 0;
        return FitsFile.Read(stream);
    }

    /// <summary>
    /// The assertion the defect would have failed: sixteen-bit unsigned, which
    /// is what every solver in this ecosystem reads.
    /// </summary>
    [Fact]
    public void ACapturedFrameIsSixteenBitUnsigned()
    {
        FitsImage image = FitsImage.ForCapturedFrame(4, 1, Frame(0.0, 1234.0, 40000.0, 65535.0));

        Assert.Equal(FitsBitPix.Int16, image.BitPix);
        Assert.Equal(32768.0, image.Bzero);
        Assert.Equal(1.0, image.Bscale);
    }

    /// <summary>
    /// Ordinary sensor values survive the file exactly -- no scaling, no
    /// rounding, the ADU that came off the sensor.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(140.0)]
    [InlineData(12716.0)]
    [InlineData(65534.0)]
    [InlineData(65535.0)]
    public void SensorValuesRoundTripExactly(double value)
    {
        FitsImage image = FitsImage.ForCapturedFrame(1, 1, Frame(value));

        Assert.Equal(value, RoundTrip(image).Pixels[0, 0]);
    }

    /// <summary>
    /// A frame that will not fit in sixteen bits is not clipped and not silently
    /// rescaled: BSCALE is what FITS provides for exactly this, and a reader
    /// that honours it gets the original values back to within one step.
    /// </summary>
    [Fact]
    public void AFrameWiderThanSixteenBitsUsesBscale_AndStaysSixteenBit()
    {
        FitsImage image = FitsImage.ForCapturedFrame(3, 1, Frame(0.0, 100000.0, 200000.0));

        Assert.Equal(FitsBitPix.Int16, image.BitPix);
        Assert.True(image.Bscale > 1.0, $"BSCALE was {image.Bscale}, so the range cannot fit");

        FitsImage read = RoundTrip(image);
        for (int x = 0; x < 3; x++)
        {
            Assert.True(
                Math.Abs(read.Pixels[0, x] - image.Pixels[0, x]) <= image.Bscale,
                $"pixel {x} came back as {read.Pixels[0, x]} from {image.Pixels[0, x]}");
        }
    }

    /// <summary>
    /// Negative values -- a driver subtracting a bias pedestal too enthusiastically
    /// -- are carried rather than clamped to zero, for the same reason.
    /// </summary>
    [Fact]
    public void NegativeValuesAreCarried_NotClamped()
    {
        FitsImage image = FitsImage.ForCapturedFrame(2, 1, Frame(-500.0, 3000.0));

        FitsImage read = RoundTrip(image);

        Assert.True(read.Pixels[0, 0] < 0.0, $"a negative pedestal came back as {read.Pixels[0, 0]}");
        Assert.Equal(-500.0, read.Pixels[0, 0], precision: 6);
        Assert.Equal(3000.0, read.Pixels[0, 1], precision: 6);
    }

    /// <summary>
    /// The extremes of the unsigned range are the ones a sign error would break,
    /// and a frame containing a saturated star contains the top one.
    /// </summary>
    [Fact]
    public void TheFullUnsignedRangeSurvives()
    {
        FitsImage read = RoundTrip(FitsImage.ForCapturedFrame(2, 1, Frame(0.0, 65535.0)));

        Assert.Equal(0.0, read.Pixels[0, 0]);
        Assert.Equal(65535.0, read.Pixels[0, 1]);
    }

    // ---- The depth the camera says it read out at ----

    /// <summary>
    /// A camera in an 8-bit readout mode gets an 8-bit file: what lands on disk
    /// is what came off the sensor. Verified separately that Watney solves
    /// BITPIX 8 frames identically to 16-bit ones, so this costs nothing at the
    /// other end and halves the file.
    /// </summary>
    [Fact]
    public void AnEightBitReadoutIsWrittenAsEightBit()
    {
        FitsImage image = FitsImage.ForCapturedFrame(3, 1, Frame(0.0, 128.0, 255.0), bitsPerPixel: 8);

        Assert.Equal(FitsBitPix.Byte, image.BitPix);
        Assert.Equal(0.0, image.Bzero);

        FitsImage read = RoundTrip(image);
        Assert.Equal(0.0, read.Pixels[0, 0]);
        Assert.Equal(128.0, read.Pixels[0, 1]);
        Assert.Equal(255.0, read.Pixels[0, 2]);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(14)]
    [InlineData(16)]
    public void ADeeperReadoutIsWrittenAsSixteenBitUnsigned(int bits)
    {
        FitsImage image = FitsImage.ForCapturedFrame(2, 1, Frame(0.0, 900.0), bitsPerPixel: bits);

        Assert.Equal(FitsBitPix.Int16, image.BitPix);
        Assert.Equal(32768.0, image.Bzero);
        Assert.Equal(900.0, RoundTrip(image).Pixels[0, 1]);
    }

    /// <summary>
    /// The declaration is honoured, not obeyed. A driver claiming 8 bits while
    /// handing back values past 255 is contradicting itself, and believing it
    /// would throw away almost the whole frame -- so the data wins.
    /// </summary>
    [Fact]
    public void ADepthTheDataContradictsIsIgnored()
    {
        FitsImage image = FitsImage.ForCapturedFrame(2, 1, Frame(0.0, 4095.0), bitsPerPixel: 8);

        Assert.Equal(FitsBitPix.Int16, image.BitPix);
        Assert.Equal(4095.0, RoundTrip(image).Pixels[0, 1]);
    }

    /// <summary>
    /// A dark 16-bit frame whose brightest pixel happens to fall under 255 is
    /// still a 16-bit frame. Only the driver knows that, which is why the depth
    /// is asked for rather than inferred from the pixels.
    /// </summary>
    [Fact]
    public void ADarkSixteenBitFrameIsNotMistakenForAnEightBitOne()
    {
        FitsImage image = FitsImage.ForCapturedFrame(2, 1, Frame(40.0, 200.0), bitsPerPixel: 16);

        Assert.Equal(FitsBitPix.Int16, image.BitPix);
    }

    /// <summary>
    /// No declaration is the backup case: drivers that do not report a usable
    /// MaxADU still have to produce a solvable file, and the frame's own range
    /// decides.
    /// </summary>
    [Fact]
    public void WithoutADeclaredDepthTheRangeDecides()
    {
        Assert.Equal(FitsBitPix.Int16, FitsImage.ForCapturedFrame(2, 1, Frame(0.0, 200.0)).BitPix);
        Assert.Equal(FitsBitPix.Int16, FitsImage.ForCapturedFrame(2, 1, Frame(0.0, 60000.0)).BitPix);
    }

    // ---- What actually lands on disk ----

    /// <summary>
    /// The declared depth and the bytes written are the same statement.
    ///
    /// Worth pinning down explicitly, because a file whose BITPIX says 16 while
    /// its data is four bytes per pixel would be read as garbage by every other
    /// program and is exactly the kind of mismatch a keyword-only change would
    /// leave behind. The file size is the honest witness: a FITS file is a
    /// 2880-byte header block followed by width x height x (BITPIX/8) bytes,
    /// padded up to the next 2880.
    /// </summary>
    [Theory]
    [InlineData(8, 255.0, 1)]
    [InlineData(16, 65535.0, 2)]
    [InlineData(null, 65535.0, 2)]
    public void TheFileIsAsWideAsItSaysItIs(int? declaredBits, double maximum, int expectedBytesPerPixel)
    {
        const int width = 64;
        const int height = 48;

        var pixels = new double[height, width];
        var random = new Random(3);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixels[y, x] = Math.Round(random.NextDouble() * maximum);
            }
        }

        FitsImage image = FitsImage.ForCapturedFrame(width, height, pixels, declaredBits);

        using var stream = new MemoryStream();
        FitsFile.Write(stream, image);

        long dataBytes = (long)width * height * expectedBytesPerPixel;
        long padded = (dataBytes + 2879) / 2880 * 2880;

        Assert.Equal(expectedBytesPerPixel * 8, Math.Abs((int)image.BitPix));
        Assert.Equal(2880 + padded, stream.Length);

        // And the values survive the narrower container unchanged.
        stream.Position = 0;
        FitsImage read = FitsFile.Read(stream);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Assert.Equal(pixels[y, x], read.Pixels[y, x]);
            }
        }
    }

    /// <summary>
    /// The same check at the size the camera actually produces, since that is
    /// the number a person compares against a file listing: a 1936x1096 frame
    /// is 4,248,000 bytes at 16 bits and 8,493,120 at 32.
    /// </summary>
    [Fact]
    public void AFullFrameIsTheSizeSixteenBitsImplies()
    {
        const int width = 1936;
        const int height = 1096;
        var pixels = new double[height, width];

        using var stream = new MemoryStream();
        FitsFile.Write(stream, FitsImage.ForCapturedFrame(width, height, pixels, bitsPerPixel: 16));

        Assert.Equal(4_248_000, stream.Length);
    }

    /// <summary>A uniform frame (lens cap on) has no range to fit and must not divide by it.</summary>
    [Fact]
    public void AUniformFrameIsWritable()
    {
        FitsImage read = RoundTrip(FitsImage.ForCapturedFrame(2, 1, Frame(1000.0, 1000.0)));

        Assert.Equal(1000.0, read.Pixels[0, 0]);
        Assert.Equal(1000.0, read.Pixels[0, 1]);
    }
}
