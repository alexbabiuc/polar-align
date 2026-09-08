namespace FreePolarAlign.Imaging.Fits;

/// <summary>FITS BITPIX values: the on-disk pixel data type.</summary>
public enum FitsBitPix
{
    Byte = 8,
    Int16 = 16,
    Int32 = 32,
    Int64 = 64,
    Float32 = -32,
    Float64 = -64
}

/// <summary>
/// A simple 2D FITS image: pixel data plus enough header state to read and
/// write it back exactly (BITPIX/BZERO/BSCALE), plus an open-ended
/// <see cref="ExtraHeader"/> for everything else (in particular the WCS
/// keywords -- see <see cref="Wcs.TanWcsSolution"/>).
/// </summary>
public sealed class FitsImage
{
    public FitsImage(int width, int height, FitsBitPix bitPix, double bzero, double bscale, double[,] pixels, FitsHeader? extraHeader = null)
    {
        if (pixels.GetLength(0) != height || pixels.GetLength(1) != width)
        {
            throw new ArgumentException($"Pixel array is {pixels.GetLength(1)}x{pixels.GetLength(0)}, expected {width}x{height}.");
        }

        Width = width;
        Height = height;
        BitPix = bitPix;
        Bzero = bzero;
        Bscale = bscale;
        Pixels = pixels;
        ExtraHeader = extraHeader ?? new FitsHeader();
    }

    public int Width { get; }

    public int Height { get; }

    public FitsBitPix BitPix { get; }

    public double Bzero { get; }

    public double Bscale { get; }

    /// <summary>Physical pixel values, indexed [row (y), column (x)], row 0 = first row stored in the file (FITS's "bottom" row by convention).</summary>
    public double[,] Pixels { get; }

    public FitsHeader ExtraHeader { get; }
}
