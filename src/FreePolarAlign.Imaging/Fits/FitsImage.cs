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

    /// <summary>
    /// Packages raw sensor pixels as a captured frame, on disk as 16-bit
    /// unsigned integers (BITPIX 16, BZERO 32768) -- the convention every
    /// camera, capture program and plate solver in this ecosystem actually
    /// speaks.
    ///
    /// This is not a stylistic preference, it is the difference between solving
    /// and not solving. The ASCOM capture path used to write BITPIX 32, on the
    /// reasoning that ASCOM's ImageArray hands back Int32 and widening loses
    /// nothing. Nothing is lost from the *data*, but Watney detects no stars at
    /// all in such a file: measured on one real frame, the same pixels written
    /// as BITPIX 16 yielded 176 detected stars and a solve in 651 ms, and as
    /// BITPIX 32 yielded zero stars and NoStarsDetected. Sensor values occupy
    /// the bottom sixteen bits either way, so in a 32-bit container the frame
    /// reads as very nearly black to anything that scales by the container's
    /// range rather than the data's.
    ///
    /// The depth is the camera's where the camera states one (see
    /// <paramref name="bitsPerPixel"/>) and the frame's own range otherwise.
    ///
    /// <paramref name="pixels"/> are physical values, in the sensor's own ADU.
    /// Values outside what 16 bits can hold -- a negative pedestal, or a driver
    /// summing binned wells past 65535 -- are not clipped and not rescaled
    /// behind the caller's back: BZERO and BSCALE are chosen to fit the range,
    /// which is precisely what FITS provides them for, and any reader that
    /// honours them recovers the original values. BSCALE stays 1 for every
    /// frame that fits, which is every frame a normal camera produces.
    /// </summary>
    /// <param name="bitsPerPixel">
    /// The depth the camera says it read out at, when it says. A driver in an
    /// 8-bit readout mode gets an 8-bit file and a 16-bit one gets a 16-bit
    /// file, so what lands on disk is what came off the sensor rather than a
    /// guess from the frame's own range -- a dark 16-bit frame whose brightest
    /// pixel happens to fall under 255 is not an 8-bit frame, and only the
    /// driver can tell the difference.
    ///
    /// Honoured only when the pixels actually fit the claim. A driver reporting
    /// 8 bits while handing back values past 255 is contradicting itself, and
    /// the data is believed over the claim -- writing 8 bits there would throw
    /// away most of the frame. Anything deeper than 16 bits, or no answer at
    /// all, falls through to the range-based choice below.
    /// </param>
    public static FitsImage ForCapturedFrame(
        int width, int height, double[,] pixels, int? bitsPerPixel = null, FitsHeader? extraHeader = null)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        (double minimum, double maximum) = FiniteRange(pixels);

        if (bitsPerPixel is > 0 and <= 8 && minimum >= 0.0 && maximum <= MaximumUnsigned8Bit)
        {
            // FITS's 8-bit type is unsigned already, so it needs no offset.
            return new FitsImage(width, height, FitsBitPix.Byte, 0.0, 1.0, pixels, extraHeader);
        }

        if (bitsPerPixel is > 8 and <= 16 && minimum >= 0.0 && maximum <= MaximumUnsigned16Bit)
        {
            return new FitsImage(width, height, FitsBitPix.Int16, UnsignedOffset, 1.0, pixels, extraHeader);
        }

        // No usable declaration, or one the data contradicts: fall back to what
        // the frame itself requires.
        double bscale = 1.0;
        double bzero = UnsignedOffset;

        if (minimum < 0.0 || maximum > MaximumUnsigned16Bit)
        {
            // Smallest integer step that brings the range inside 16 bits. Integer
            // rather than fractional so the stored values stay whole numbers of a
            // known unit, which is what makes the file readable as integers by
            // anything that ignores BSCALE.
            bscale = Math.Max(1.0, Math.Ceiling((maximum - minimum) / MaximumUnsigned16Bit));
            bzero = minimum + (UnsignedOffset * bscale);
        }

        return new FitsImage(width, height, FitsBitPix.Int16, bzero, bscale, pixels, extraHeader);
    }

    /// <summary>Half of the 16-bit range: the offset that makes a signed container hold unsigned values.</summary>
    private const double UnsignedOffset = 32768.0;

    private const double MaximumUnsigned16Bit = 65535.0;

    private const double MaximumUnsigned8Bit = 255.0;

    private static (double Minimum, double Maximum) FiniteRange(double[,] pixels)
    {
        double minimum = double.PositiveInfinity;
        double maximum = double.NegativeInfinity;

        foreach (double value in pixels)
        {
            if (!double.IsFinite(value))
            {
                continue;
            }

            if (value < minimum)
            {
                minimum = value;
            }

            if (value > maximum)
            {
                maximum = value;
            }
        }

        // An empty or wholly non-finite frame has no range to fit; the ordinary
        // unsigned offset is as good an answer as any and keeps the caller from
        // having to handle a second failure mode for a frame that is already
        // unusable.
        return double.IsFinite(minimum) && double.IsFinite(maximum) ? (minimum, maximum) : (0.0, 0.0);
    }
}
