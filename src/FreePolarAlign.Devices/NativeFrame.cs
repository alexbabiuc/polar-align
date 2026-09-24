using System.Buffers.Binary;
using FreePolarAlign.Imaging.Fits;

namespace FreePolarAlign.Devices;

/// <summary>
/// The part of capturing through a vendor SDK that does not depend on the
/// vendor: turning the byte buffer it hands back into a frame on disk.
///
/// Both ZWO and ToupTek return a packed buffer, one or two bytes per pixel,
/// little-endian, first row at the top of the sensor. Written once here rather
/// than once per plugin, because the two ways it can go wrong -- byte order and
/// row order -- produce frames that look plausible and solve as a mirror image
/// or not at all.
/// </summary>
public static class NativeFrame
{
    /// <summary>
    /// Unpacks a buffer into pixels indexed <c>[row, column]</c>, row 0 being
    /// the first row the camera sent.
    ///
    /// Row order matches what the ASCOM path writes -- ASCOM's <c>ImageArray</c>
    /// row 0 goes to pixel row 0 too -- so a frame means the same thing whichever
    /// provider took it. That path is the one proven against real sky: frames
    /// it wrote solved under Watney and under nova.astrometry.net, so this
    /// matches it rather than choosing afresh.
    /// </summary>
    /// <param name="bytesPerPixel">1 for an 8-bit readout, 2 for anything deeper.</param>
    public static double[,] ToPixels(ReadOnlySpan<byte> buffer, int width, int height, int bytesPerPixel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        if (bytesPerPixel is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytesPerPixel), bytesPerPixel, "Only 8-bit and 16-bit packed buffers are supported.");
        }

        long required = (long)width * height * bytesPerPixel;
        if (buffer.Length < required)
        {
            throw new ArgumentException(
                $"A {width}x{height} frame at {bytesPerPixel} byte(s) per pixel needs {required} bytes; the camera returned {buffer.Length}.",
                nameof(buffer));
        }

        var pixels = new double[height, width];

        if (bytesPerPixel == 1)
        {
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    pixels[y, x] = buffer[row + x];
                }
            }
        }
        else
        {
            for (int y = 0; y < height; y++)
            {
                int row = y * width * 2;
                for (int x = 0; x < width; x++)
                {
                    // Explicitly little-endian, which is what both SDKs document,
                    // rather than whatever the host happens to be.
                    pixels[y, x] = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(row + (x * 2), 2));
                }
            }
        }

        return pixels;
    }

    /// <summary>
    /// Writes a captured frame at the readout's own depth (D20), with the
    /// provenance header every capture carries, and returns what the session
    /// needs to know about it.
    /// </summary>
    public static CapturedImage Write(
        ICamera camera,
        double[,] pixels,
        int bitsPerPixel,
        TimeSpan duration,
        DateTime startUtc,
        CaptureContext? context,
        string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(pixels);

        int height = pixels.GetLength(0);
        int width = pixels.GetLength(1);

        // The midpoint, not the start or end, because it is the instant the
        // solved position belongs to; a star moves 15 arcseconds a second.
        DateTime midpointUtc = startUtc + TimeSpan.FromTicks(duration.Ticks / 2);

        FitsImage image = FitsImage.ForCapturedFrame(width, height, pixels, bitsPerPixel);
        var described = new FitsImage(
            image.Width, image.Height, image.BitPix, image.Bzero, image.Bscale, image.Pixels,
            CaptureHeader.Build(image.ExtraHeader, camera, duration, startUtc, midpointUtc, context));

        Directory.CreateDirectory(workingDirectory);
        string path = Path.Combine(workingDirectory, $"{startUtc:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.fits");
        FitsFile.Write(path, described);

        return new CapturedImage(path, midpointUtc, duration);
    }

    /// <summary>
    /// Where native-SDK frames go, alongside the ASCOM ones. One directory so a
    /// user looking for a night's frames finds them in one place.
    /// </summary>
    public static string DefaultWorkingDirectory() =>
        Path.Combine(Path.GetTempPath(), "FreePolarAlign", "captures");
}
