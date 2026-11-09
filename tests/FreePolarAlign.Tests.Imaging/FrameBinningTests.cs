using FreePolarAlign.Imaging.Fits;
using Xunit;

namespace FreePolarAlign.Tests.Imaging;

/// <summary>
/// D43's binning: the mean of each block, at the original depth, with a header
/// that describes the binned pixel rather than the sensor's.
/// </summary>
public class FrameBinningTests
{
    private static FitsImage Frame(int width, int height, Func<int, int, double> value, FitsHeader? header = null)
    {
        var pixels = new double[height, width];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixels[y, x] = value(x, y);
            }
        }

        return FitsImage.ForCapturedFrame(width, height, pixels, 16, header);
    }

    [Fact]
    public void EachPixel_IsTheMeanOfItsBlock()
    {
        FitsImage frame = Frame(4, 2, (x, y) => (10 * y) + x);

        FitsImage binned = FrameBinning.Bin(frame, 2);

        Assert.Equal(2, binned.Width);
        Assert.Equal(1, binned.Height);
        Assert.Equal((0 + 1 + 10 + 11) / 4.0, binned.Pixels[0, 0]);
        Assert.Equal((2 + 3 + 12 + 13) / 4.0, binned.Pixels[0, 1]);
    }

    /// <summary>A partial block along an edge is dropped, as a camera binning in hardware drops it.</summary>
    [Fact]
    public void AnOddRowAndColumn_AreDropped()
    {
        FitsImage binned = FrameBinning.Bin(Frame(5, 3, (_, _) => 100), 2);

        Assert.Equal(2, binned.Width);
        Assert.Equal(1, binned.Height);
    }

    /// <summary>
    /// A mean of saturated 16-bit wells is still 65535, so the binned frame is
    /// written as the sensor's frames are. A sum would not fit, and would come
    /// out as a rescaled file of a different kind.
    /// </summary>
    [Fact]
    public void ASaturatedFrame_StaysAPlainSixteenBitFrame()
    {
        FitsImage binned = FrameBinning.Bin(Frame(4, 4, (_, _) => 65535), 2);

        Assert.Equal(FitsBitPix.Int16, binned.BitPix);
        Assert.Equal(32768.0, binned.Bzero);
        Assert.Equal(1.0, binned.Bscale);
        Assert.Equal(65535.0, binned.Pixels[1, 1]);
    }

    /// <summary>
    /// The header describes the pixel the file holds. ASPS takes its pixel size
    /// from XPIXSZ, so a binned frame still claiming the sensor's would be
    /// solved at half its real scale.
    /// </summary>
    [Fact]
    public void TheHeader_DescribesTheBinnedPixel()
    {
        var header = new FitsHeader();
        header.Set("XPIXSZ", 3.76, "pixel size, microns");
        header.Set("YPIXSZ", 3.76, "pixel size, microns");
        header.Set("PIXSCALE", 0.97, "arcsec/pixel implied by FOCALLEN");
        header.Set("FOCALLEN", 800.0, "focal length, mm");

        FitsHeader binned = FrameBinning.Bin(Frame(4, 4, (_, _) => 1, header), 2).ExtraHeader;

        Assert.Equal(7.52, binned.GetDouble("XPIXSZ"), 9);
        Assert.Equal(7.52, binned.GetDouble("YPIXSZ"), 9);
        Assert.Equal(1.94, binned.GetDouble("PIXSCALE"), 9);
        Assert.Equal(800.0, binned.GetDouble("FOCALLEN"));
        Assert.Equal(2.0, binned.GetDouble("XBINNING"));
        Assert.Equal(2.0, binned.GetDouble("YBINNING"));
    }

    /// <summary>What a frame never said, binning does not make it say.</summary>
    [Fact]
    public void AnAbsentPixelSize_StaysAbsent()
    {
        FitsHeader binned = FrameBinning.Bin(Frame(4, 4, (_, _) => 1), 2).ExtraHeader;

        Assert.False(binned.TryGet("XPIXSZ", out _));
        Assert.False(binned.TryGet("PIXSCALE", out _));
    }

    [Fact]
    public void BinningByOne_ReturnsTheFrameUntouched()
    {
        FitsImage frame = Frame(3, 3, (x, y) => x + y);

        Assert.Same(frame, FrameBinning.Bin(frame, 1));
    }

    /// <summary>The whole round trip the engine makes: written, read back, binned, written again.</summary>
    [Fact]
    public void ABinnedFrameOnDisk_ReadsBackBinned()
    {
        string path = Path.Combine(Path.GetTempPath(), $"fpa-bin-{Guid.NewGuid():N}.fits");
        try
        {
            FitsFile.Write(path, Frame(6, 4, (x, y) => 1000 + x + (6 * y)));

            FitsFile.Write(path, FrameBinning.Bin(FitsFile.Read(path), 2));
            FitsImage reread = FitsFile.Read(path);

            Assert.Equal(3, reread.Width);
            Assert.Equal(2, reread.Height);

            // (1000 + 1001 + 1006 + 1007) / 4 = 1003.5, stored as a whole number,
            // so within half a unit.
            Assert.InRange(reread.Pixels[0, 0], 1003.0, 1004.0);
            Assert.Equal(2.0, reread.ExtraHeader.GetDouble("XBINNING"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
