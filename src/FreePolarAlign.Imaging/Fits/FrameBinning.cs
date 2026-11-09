namespace FreePolarAlign.Imaging.Fits;

/// <summary>
/// Software binning of a captured frame (D43): each block of
/// <c>factor × factor</c> pixels becomes one, holding their mean.
///
/// The mean rather than the sum, so a binned frame keeps the sensor's own range
/// and is stored at the same depth: a sum of four saturated 16-bit wells does
/// not fit in 16 bits, and a frame that changed type with the binning would be
/// a second kind of frame for every solver to cope with.
///
/// A trailing row or column that does not fill a whole block is dropped, as a
/// camera binning in hardware does. Keeping it would put pixels of a different
/// size along one edge.
/// </summary>
public static class FrameBinning
{
    /// <summary>
    /// The binned frame, with the header describing it: pixel size, the scale
    /// the focal length implies and the binning keywords are multiplied by
    /// <paramref name="factor"/>. Everything else is carried over as it was.
    /// A factor of 1 returns <paramref name="image"/> itself.
    /// </summary>
    public static FitsImage Bin(FitsImage image, int factor)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1);

        if (factor == 1)
        {
            return image;
        }

        int width = image.Width / factor;
        int height = image.Height / factor;
        if (width < 1 || height < 1)
        {
            throw new ArgumentException(
                $"A {image.Width}x{image.Height} frame is too small to bin {factor}x{factor}.", nameof(image));
        }

        double[,] source = image.Pixels;
        var pixels = new double[height, width];
        double cell = factor * factor;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double sum = 0.0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int row = (y * factor) + dy;
                    for (int dx = 0; dx < factor; dx++)
                    {
                        sum += source[row, (x * factor) + dx];
                    }
                }

                pixels[y, x] = sum / cell;
            }
        }

        return FitsImage.ForCapturedFrame(width, height, pixels, DeclaredDepth(image), Describe(image.ExtraHeader, factor));
    }

    /// <summary>
    /// The depth the original was written at, when it was written at a camera's
    /// depth rather than fitted to its range. A mean never leaves the range of
    /// what it averages, so whatever held the original holds the binned frame.
    /// </summary>
    private static int? DeclaredDepth(FitsImage image) => image switch
    {
        { BitPix: FitsBitPix.Byte, Bscale: 1.0, Bzero: 0.0 } => 8,
        { BitPix: FitsBitPix.Int16, Bscale: 1.0, Bzero: 32768.0 } => 16,
        _ => null,
    };

    private static FitsHeader Describe(FitsHeader original, int factor)
    {
        var header = new FitsHeader();
        foreach (FitsCard card in original.Cards)
        {
            header.Set(card.Keyword, card.Value!, card.Comment);
        }

        // Scaled where present and left absent where not: a frame that never
        // said its pixel size should not start claiming one.
        Scale(header, "XPIXSZ", factor);
        Scale(header, "YPIXSZ", factor);
        Scale(header, "PIXSCALE", factor);

        // Binning is written whether or not the camera wrote it, starting from
        // the camera's own figure when it did.
        header.Set("XBINNING", (int)Math.Round(header.GetDouble("XBINNING", 1.0)) * factor, "binning, x");
        header.Set("YBINNING", (int)Math.Round(header.GetDouble("YBINNING", 1.0)) * factor, "binning, y");

        return header;
    }

    private static void Scale(FitsHeader header, string keyword, int factor)
    {
        if (!header.TryGet(keyword, out object? value) || value is null)
        {
            return;
        }

        string? comment = header.Cards.First(c => c.Keyword == keyword).Comment;
        header.Set(keyword, header.GetDouble(keyword) * factor, comment);
    }
}
