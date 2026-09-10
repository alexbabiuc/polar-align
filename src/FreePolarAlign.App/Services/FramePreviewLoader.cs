using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FreePolarAlign.Imaging.Display;
using FreePolarAlign.Imaging.Fits;

namespace FreePolarAlign.App.Services;

/// <param name="Bitmap">Ready to display, or null when the frame could not be read.</param>
/// <param name="Problem">
/// Why it could not be read. Shown rather than swallowed: a frame that will not
/// open is itself diagnostic -- a camera writing truncated files, or a disk that
/// has filled up mid-session.
/// </param>
public sealed record FramePreview(
    Bitmap? Bitmap,
    StretchStatistics? Statistics,
    int SourceWidth,
    int SourceHeight,
    int Decimation,
    string? Problem);

/// <summary>
/// Reads a captured FITS frame from disk and turns it into something the window
/// can show.
///
/// Everything expensive happens off the UI thread, and everything that decides
/// what the pixels look like happens in <see cref="ImageStretch"/>, which is
/// tested. What is left here is the part that cannot be tested without a
/// renderer: wrapping the bytes in an Avalonia bitmap.
///
/// The bitmap is built as 32-bit BGRA rather than 8-bit grey. Grey8 would halve
/// the copy and is supported in principle, but BGRA is the one format every
/// backend accepts without negotiation, and a preview is not where a few
/// megabytes matter.
/// </summary>
public static class FramePreviewLoader
{
    /// <summary>
    /// Cap on the preview's size in pixels. Larger would be wasted: the panel it
    /// sits in is a few hundred pixels across, and decoding a full frame at
    /// native resolution costs time on every capture for detail no one can see.
    /// </summary>
    private const int MaximumPreviewWidth = 1400;

    private const int MaximumPreviewHeight = 1400;

    public static Task<FramePreview> LoadAsync(string path, double targetBackground, bool autoStretch) =>
        Task.Run(() => Load(path, targetBackground, autoStretch));

    private static FramePreview Load(string path, double targetBackground, bool autoStretch)
    {
        try
        {
            FitsImage image = FitsFile.Read(path);

            ImagePreview preview = autoStretch
                ? ImageStretch.Create(image, targetBackground, MaximumPreviewWidth, MaximumPreviewHeight)
                : ImageStretch.CreateLinear(image, MaximumPreviewWidth, MaximumPreviewHeight);

            return new FramePreview(
                ToBitmap(preview),
                preview.Statistics,
                preview.SourceWidth,
                preview.SourceHeight,
                preview.Decimation,
                Problem: null);
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException
                                   or InvalidDataException or ArgumentException)
        {
            return new FramePreview(null, null, 0, 0, 1, $"Could not read '{Path.GetFileName(path)}': {ex.Message}");
        }
    }

    private static Bitmap ToBitmap(ImagePreview preview)
    {
        byte[] bgra = ImageStretch.ToBgra(preview);

        var bitmap = new WriteableBitmap(
            new PixelSize(preview.Width, preview.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        using (ILockedFramebuffer buffer = bitmap.Lock())
        {
            int stride = preview.Width * 4;

            // Copied row by row rather than in one block: a framebuffer's stride
            // is not required to equal its row width, and assuming it does
            // produces a sheared image on any backend that pads.
            for (int row = 0; row < preview.Height; row++)
            {
                nint destination = buffer.Address + (row * buffer.RowBytes);
                System.Runtime.InteropServices.Marshal.Copy(bgra, row * stride, destination, stride);
            }
        }

        return bitmap;
    }
}
