using FreePolarAlign.Imaging.Fits;
using FreePolarAlign.Imaging.Wcs;
using Xunit;

namespace FreePolarAlign.Tests.Imaging;

public class FitsRoundTripTests
{
    [Fact]
    public void Int16Image_WithTanWcs_RoundTripsExactly()
    {
        const int width = 12;
        const int height = 9;
        var pixels = new double[height, width];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixels[y, x] = (x * 7 + y * 13) % 4000 - 2000;
            }
        }

        var wcs = new TanWcsSolution(
            Crpix1: width / 2.0 + 0.5,
            Crpix2: height / 2.0 + 0.5,
            Crval1Degrees: 83.633083,
            Crval2Degrees: 22.014500,
            Cd1_1: -0.0002777777777778,
            Cd1_2: 0.0,
            Cd2_1: 0.0,
            Cd2_2: 0.0002777777777778);

        var header = new FitsHeader();
        wcs.WriteToHeader(header);

        var image = new FitsImage(width, height, FitsBitPix.Int16, bzero: 0.0, bscale: 1.0, pixels, header);

        string path = Path.Combine(Path.GetTempPath(), $"fpa-roundtrip-{Guid.NewGuid():N}.fits");
        try
        {
            FitsFile.Write(path, image);
            FitsImage readBack = FitsFile.Read(path);

            Assert.Equal(width, readBack.Width);
            Assert.Equal(height, readBack.Height);
            Assert.Equal(FitsBitPix.Int16, readBack.BitPix);
            Assert.Equal(0.0, readBack.Bzero);
            Assert.Equal(1.0, readBack.Bscale);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Assert.Equal(pixels[y, x], readBack.Pixels[y, x]);
                }
            }

            TanWcsSolution readWcs = TanWcsSolution.FromHeader(readBack.ExtraHeader);
            Assert.Equal(wcs.Crpix1, readWcs.Crpix1, precision: 12);
            Assert.Equal(wcs.Crpix2, readWcs.Crpix2, precision: 12);
            Assert.Equal(wcs.Crval1Degrees, readWcs.Crval1Degrees, precision: 12);
            Assert.Equal(wcs.Crval2Degrees, readWcs.Crval2Degrees, precision: 12);
            Assert.Equal(wcs.Cd1_1, readWcs.Cd1_1, precision: 15);
            Assert.Equal(wcs.Cd1_2, readWcs.Cd1_2, precision: 15);
            Assert.Equal(wcs.Cd2_1, readWcs.Cd2_1, precision: 15);
            Assert.Equal(wcs.Cd2_2, readWcs.Cd2_2, precision: 15);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Float32Image_RoundTripsToSinglerecision()
    {
        const int width = 5;
        const int height = 4;
        var pixels = new double[height, width];
        var rng = new Random(42);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixels[y, x] = (float)(rng.NextDouble() * 65535.0);
            }
        }

        var image = new FitsImage(width, height, FitsBitPix.Float32, bzero: 0.0, bscale: 1.0, pixels);

        string path = Path.Combine(Path.GetTempPath(), $"fpa-roundtrip-{Guid.NewGuid():N}.fits");
        try
        {
            FitsFile.Write(path, image);
            FitsImage readBack = FitsFile.Read(path);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Assert.Equal((float)pixels[y, x], (float)readBack.Pixels[y, x]);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TanWcs_PixelToWorldToPixel_IsSelfConsistent()
    {
        var wcs = new TanWcsSolution(
            Crpix1: 512.5,
            Crpix2: 512.5,
            Crval1Degrees: 10.684,
            Crval2Degrees: 41.269,
            Cd1_1: -0.0001333,
            Cd1_2: 0.0000012,
            Cd2_1: -0.0000012,
            Cd2_2: 0.0001333);

        foreach ((double x, double y) in new[] { (1.0, 1.0), (512.5, 512.5), (900.0, 40.0), (50.0, 1000.0) })
        {
            (double ra, double dec) = wcs.PixelToWorld(x, y);
            (double xBack, double yBack) = wcs.WorldToPixel(ra, dec);

            Assert.Equal(x, xBack, precision: 6);
            Assert.Equal(y, yBack, precision: 6);
        }
    }

    [Fact]
    public void Determinant_ReflectsCdMatrixParity()
    {
        var rightHanded = new TanWcsSolution(1, 1, 0, 0, 0.0001, 0.0, 0.0, 0.0001);
        var leftHanded = new TanWcsSolution(1, 1, 0, 0, -0.0001, 0.0, 0.0, 0.0001);

        Assert.True(rightHanded.Determinant > 0);
        Assert.True(leftHanded.Determinant < 0);
    }
}
