using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using FreePolarAlign.Devices.Simulated;
using FreePolarAlign.Devices.Simulated.SyntheticSky;
using FreePolarAlign.Imaging.Fits;
using FreePolarAlign.Imaging.Wcs;

namespace FreePolarAlign.Tests.Support;

/// <summary>
/// Rendered star fields, rendered once and kept (D38).
///
/// A render is fixed by its inputs -- the field, the frame size, the conditions
/// and the seed -- so a test asking for the same one gets the kept frame, and
/// only a test asking for a new one renders. A full frame takes seconds to
/// render and a fraction of one to solve, so this is most of a solving test's
/// time.
///
/// Frames are kept as the simulated camera's eight-bit readout delivers them,
/// gzipped, unless a test asks for sixteen: about 24 KB against about 2 MB for
/// a 1684 × 1263 frame. Eight bits loses the faint stars, which costs nothing
/// in a rich field and everything in a sparse one: measured, the 1.2° field at
/// 400 mm and three of four ASTAP fields stopped solving. Those keep sixteen. The catalogue is not part of the key; it is the committed
/// fixture, and a change to it, or to the renderer, means deleting
/// <c>Renders/</c> and recording again.
/// </summary>
public static class RecordedRenders
{
    /// <summary>What <see cref="SkyRenderer.Render"/> would return for these inputs.</summary>
    /// <param name="catalog">Loaded only when a frame has to be rendered.</param>
    /// <param name="eightBit">False keeps the sixteen-bit render, for a field too sparse to survive eight.</param>
    public static FitsImage Render(
        Func<StarCatalog> catalog,
        TanWcsSolution wcs,
        int width,
        int height,
        ObservingConditions conditions,
        int seed,
        bool eightBit = true)
    {
        string inputs = $"{wcs}|{width}x{height}|{conditions}|seed {seed}|{(eightBit ? 8 : 16)}-bit";
        string path = Path.Combine(Folder(), Key(inputs) + ".fits.gz");

        if (File.Exists(path))
        {
            using FileStream file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            gzip.CopyTo(buffer);
            buffer.Position = 0;
            return FitsFile.Read(buffer);
        }

        if (!FrameRecording.Enabled)
        {
            throw new InvalidOperationException(
                $"No kept render for {inputs}. {FrameRecording.HowToRecord}");
        }

        FitsImage rendered = SkyRenderer.Render(catalog(), wcs, width, height, conditions, new Random(seed));
        if (eightBit)
        {
            rendered = SimulatedCamera.QuantiseToEightBit(rendered);
        }

        Directory.CreateDirectory(Folder());
        using (FileStream file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
        {
            FitsFile.Write(gzip, rendered);
        }

        return rendered;
    }

    private static string Key(string inputs) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputs)))[..16].ToLowerInvariant();

    /// <summary><c>Renders/</c> beside this file in the source tree, where recording writes.</summary>
    private static string Folder([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "Renders");
}
