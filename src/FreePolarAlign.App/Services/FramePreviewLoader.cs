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
/// <remarks>
/// The stretch's statistics and the source dimensions used to travel with this
/// and be printed under the picture. They are not carried any more: nothing
/// displays them, and a preview that reported numbers nobody reads is a preview
/// pretending to be a measurement. <see cref="ImageStretch"/> still computes
/// them for the transform, so bringing them back is a field, not a rewrite.
/// </remarks>
public sealed record FramePreview(Bitmap? Bitmap, string? Problem);

/// <summary>
/// The frame on screen, held in memory whole.
///
/// Held because the file it came from does not outlive it: the engine deletes a
/// frame once a newer one supersedes it (D26), which at a short exposure is a
/// fraction of a second later. So the brightness control re-stretches
/// <see cref="Image"/> rather than re-reading the file, and Save frame writes
/// <see cref="FitsBytes"/> rather than copying it.
/// </summary>
/// <param name="FitsBytes">
/// The file exactly as the camera path wrote it, header and all. Saved as-is
/// rather than re-encoded from <see cref="Image"/>: a saved frame is evidence,
/// and a re-encoding would be this application's opinion of the frame.
/// </param>
/// <param name="Image">Null when the bytes could not be parsed; they are still worth saving.</param>
/// <param name="Problem">Why <see cref="Image"/> is null.</param>
public sealed record DisplayedFrame(
    string SourcePath,
    DateTime ExposureMidpointUtc,
    byte[] FitsBytes,
    FitsImage? Image,
    string? Problem = null);

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

    /// <summary>
    /// Reads the frame into memory, or returns null when the file is already
    /// gone.
    ///
    /// Gone is not an error. The engine deletes a frame once a newer one has
    /// been published (D26), so a file missing by the time its turn comes means
    /// only that a newer frame has overtaken it -- and that one is already on
    /// its way.
    /// </summary>
    public static DisplayedFrame? Read(string path, DateTime exposureMidpointUtc)
    {
        byte[] bytes;
        try
        {
            // Shared for delete as well as write, so that on Windows the engine
            // can delete a superseded frame while it is still being read here
            // rather than failing and retrying later.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DisplayedFrame(
                path, exposureMidpointUtc, Array.Empty<byte>(), null,
                $"Could not read '{Path.GetFileName(path)}': {ex.Message}");
        }

        try
        {
            using var parse = new MemoryStream(bytes, writable: false);
            return new DisplayedFrame(path, exposureMidpointUtc, bytes, FitsFile.Read(parse));
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidDataException
                                   or ArgumentException)
        {
            return new DisplayedFrame(
                path, exposureMidpointUtc, bytes, null,
                $"Could not read '{Path.GetFileName(path)}': {ex.Message}");
        }
    }

    /// <param name="targetBackground">
    /// Where the sky lands in the output range: the brightness control, and the
    /// only display choice there is. The stretch itself is not optional -- an
    /// unstretched astronomical exposure is a black rectangle, so the toggle
    /// that used to offer one was offering a broken picture as a feature.
    /// </param>
    public static FramePreview Render(FitsImage image, double targetBackground)
    {
        try
        {
            ImagePreview preview = ImageStretch.Create(image, targetBackground, MaximumPreviewWidth, MaximumPreviewHeight);
            return new FramePreview(ToBitmap(preview), Problem: null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return new FramePreview(null, $"Could not build a preview: {ex.Message}");
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
