using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FreePolarAlign.Core.Astrometry;
using FreePolarAlign.Devices;
using FreePolarAlign.Devices.Simulated;
using FreePolarAlign.Devices.Simulated.SyntheticSky;
using FreePolarAlign.Tests.Support;

namespace FreePolarAlign.Tests.EndToEnd.Scenarios;

/// <summary>
/// A camera that serves frames rendered once and kept, instead of rendering a
/// sky on every exposure.
///
/// Rendering a full simulated frame takes about 3.7 s and solving it about
/// 0.3 s, so the sweep-and-adjust tests spent nearly all their minutes drawing
/// pictures. A scenario is deterministic -- a fixed start on the test clock, a
/// sweep the engine plans from it, bolt turns fixed in advance -- so the frames
/// it needs are the same on every run, and they are rendered once and kept
/// beside the test as gzipped eight-bit FITS (about 24 KB each).
///
/// A frame is matched by where the telescope physically points when the
/// exposure ends, as the simulated camera would compute it, within
/// <see cref="MatchToleranceArcseconds"/>. With the drive running, a pointing
/// holds still on the sky apart from the slow drift a misaligned axis causes,
/// which is far below the tolerance over the moments between one run and the
/// next.
///
/// Set FPA_RECORD_FRAMES=1 to record: a frame with no match is rendered by a
/// real <see cref="SimulatedCamera"/> and added. Without it, a frame with no
/// match is an error, because a scenario that quietly re-rendered would be back
/// to minutes. Delete a scenario's folder to record it afresh.
/// </summary>
internal sealed class ScenarioCamera : ICamera
{
    /// <summary>
    /// Far below anything a solve resolves on these fields (about 8"/px), and
    /// far above the drift between two runs of one scenario.
    /// </summary>
    public const double MatchToleranceArcseconds = 3.0;

    /// <summary>The readout the frames are kept at: eight bits, which still solve and compress a hundredfold.</summary>
    private const int EightBitReadout = 1;

    private readonly SimulatedMount _mount;
    private readonly SimulatedCameraOptions _options;
    private readonly AtmosphericConditions _atmosphere;
    private readonly string _folder;
    private readonly string _manifestPath;
    private readonly string _workingDirectory;
    private readonly bool _recording;
    private readonly StarCatalog? _catalog;
    private readonly List<RecordedFrame> _frames;
    private readonly object _gate = new();
    private int _rendering;
    private int _served;

    private ScenarioCamera(
        SimulatedMount mount, SimulatedCameraOptions options, string folder, bool recording, StarCatalog? catalog)
    {
        _mount = mount;
        _options = options;
        _atmosphere = options.Atmosphere ?? AtmosphericConditions.Standard;
        _folder = folder;
        _manifestPath = Path.Combine(folder, "manifest.json");
        _recording = recording;
        _catalog = catalog;
        _workingDirectory = Path.Combine(Path.GetTempPath(), $"fpa-scenario-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workingDirectory);

        _frames = File.Exists(_manifestPath)
            ? JsonSerializer.Deserialize<List<RecordedFrame>>(File.ReadAllText(_manifestPath)) ?? new()
            : new();
    }

    /// <summary>Whether this run records missing frames rather than failing on them.</summary>
    public static bool Recording => FrameRecording.Enabled;

    /// <summary>
    /// Opens the named scenario's frames, kept in <c>Scenarios/&lt;name&gt;</c>
    /// beside this file in the source tree, where recording writes them.
    /// </summary>
    public static ScenarioCamera Open(
        string scenario,
        SimulatedMount mount,
        SimulatedCameraOptions options,
        Func<StarCatalog> loadCatalogForRecording)
    {
        string folder = Path.Combine(ScenariosFolder(), scenario);
        return new ScenarioCamera(mount, options, folder, Recording, Recording ? loadCatalogForRecording() : null);
    }

    /// <summary>This file's own folder in the source tree; the compiler supplies the path.</summary>
    private static string ScenariosFolder([CallerFilePath] string thisFile = "") => Path.GetDirectoryName(thisFile)!;

    /// <summary>
    /// The scenario has frames to replay. Without them, and without recording,
    /// the test cannot run; it is skipped with a message saying how to record.
    /// </summary>
    public bool HasFrames => _frames.Count > 0;

    /// <summary>True while a frame is being rendered, which takes real seconds that fake time must not pass in.</summary>
    public bool Busy => Volatile.Read(ref _rendering) > 0;

    /// <summary>How many exposures were answered from a kept frame.</summary>
    public int Served => Volatile.Read(ref _served);

    /// <summary>Exposures nothing kept matched, in replay; each is described.</summary>
    public List<string> Misses { get; } = new();

    public string Name => "Scenario Camera";

    public int SensorWidthPixels => _options.WidthPixels;

    public int SensorHeightPixels => _options.HeightPixels;

    public double PixelSizeMicrons => _options.PixelSizeMicrons;

    public bool IsConnected { get; private set; }

    public IReadOnlyList<CameraReadoutMode> ReadoutModes => Array.Empty<CameraReadoutMode>();

    public int? ReadoutModeIndex => null;

    public Task SetReadoutModeAsync(int index, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A scenario's frames are kept at one readout.");

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public void SimulateDisconnection() => IsConnected = false;

    public async Task<CapturedImage> ExposeAsync(
        TimeSpan duration, CaptureContext? context = null, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("The scenario camera is not connected.");
        }

        await Task.Delay(duration, _mount.Time, cancellationToken).ConfigureAwait(false);

        // Matched and rendered at the end of the exposure rather than its
        // midpoint, so a recording renders exactly the instant it is keyed by.
        // Half an exposure of drift is a hundredth of an arcsecond here.
        DateTime end = _mount.Time.GetUtcNow().UtcDateTime;
        (double ra, double dec) = FieldCentre(end);

        string path = Path.Combine(_workingDirectory, $"frame-{Interlocked.Increment(ref _served):D5}.fits");
        RecordedFrame? kept = Nearest(ra, dec);
        if (kept is not null)
        {
            await using FileStream source = File.OpenRead(Path.Combine(_folder, kept.File));
            await using var gzip = new GZipStream(source, CompressionMode.Decompress);
            await using FileStream target = File.Create(path);
            await gzip.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            return new CapturedImage(path, end - duration / 2, duration);
        }

        if (!_recording)
        {
            Interlocked.Decrement(ref _served);
            string miss = $"No kept frame within {MatchToleranceArcseconds}\" of RA {ra:F5}°, Dec {dec:F5}°. " +
                          $"The scenario has changed. {FrameRecording.HowToRecord}";
            lock (Misses)
            {
                Misses.Add(miss);
            }

            throw new InvalidOperationException(miss);
        }

        return await RecordAsync(ra, dec, path, end, duration).ConfigureAwait(false);
    }

    public void Dispose()
    {
        IsConnected = false;
        try
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <summary>Where the camera physically points, computed exactly as <see cref="SimulatedCamera"/> does.</summary>
    private (double Ra, double Dec) FieldCentre(DateTime utc)
    {
        HorizontalCoordinates pointing = _mount.PhysicalPointingAt(utc);
        GeodeticLocation site = _mount.Options.Site;
        return TopocentricConverter.FromAltAz(
            pointing, utc, new ObserverSite(site.LatitudeDegrees, site.LongitudeDegrees, site.HeightMeters), _atmosphere);
    }

    private RecordedFrame? Nearest(double ra, double dec)
    {
        lock (_gate)
        {
            RecordedFrame? best = null;
            double bestArcseconds = MatchToleranceArcseconds;
            foreach (RecordedFrame frame in _frames)
            {
                double arcseconds = SeparationArcseconds(ra, dec, frame.RaDegrees, frame.DecDegrees);
                if (arcseconds <= bestArcseconds)
                {
                    best = frame;
                    bestArcseconds = arcseconds;
                }
            }

            return best;
        }
    }

    /// <summary>
    /// Renders through a real simulated camera at this instant, keeps the frame
    /// and serves it. The inner exposure has no duration of its own: the wait
    /// already happened on the test clock, which is held while this renders.
    /// </summary>
    private async Task<CapturedImage> RecordAsync(double ra, double dec, string path, DateTime end, TimeSpan duration)
    {
        Interlocked.Increment(ref _rendering);
        try
        {
            using var renderer = new SimulatedCamera(_mount, _catalog!, _options);
            await renderer.ConnectAsync().ConfigureAwait(false);
            await renderer.SetReadoutModeAsync(EightBitReadout).ConfigureAwait(false);
            CapturedImage rendered = await renderer.ExposeAsync(TimeSpan.Zero).ConfigureAwait(false);

            lock (_gate)
            {
                string file = $"frame-{_frames.Count + 1:D3}.fits.gz";
                Directory.CreateDirectory(_folder);
                using (FileStream source = File.OpenRead(rendered.FitsPath))
                using (FileStream target = File.Create(Path.Combine(_folder, file)))
                using (var gzip = new GZipStream(target, CompressionLevel.SmallestSize))
                {
                    source.CopyTo(gzip);
                }

                _frames.Add(new RecordedFrame(file, ra, dec));
                File.WriteAllText(
                    _manifestPath, JsonSerializer.Serialize(_frames, new JsonSerializerOptions { WriteIndented = true }));
            }

            File.Copy(rendered.FitsPath, path, overwrite: true);
            return new CapturedImage(path, end - duration / 2, duration);
        }
        finally
        {
            Interlocked.Decrement(ref _rendering);
        }
    }

    private static double SeparationArcseconds(double ra1, double dec1, double ra2, double dec2)
    {
        const double d2r = Math.PI / 180.0;
        double dDec = (dec2 - dec1) * d2r;
        double dRa = (ra2 - ra1) * d2r;
        double h = Math.Sin(dDec / 2) * Math.Sin(dDec / 2)
                 + Math.Cos(dec1 * d2r) * Math.Cos(dec2 * d2r) * Math.Sin(dRa / 2) * Math.Sin(dRa / 2);
        return 2.0 * Math.Asin(Math.Sqrt(Math.Clamp(h, 0.0, 1.0))) / d2r * 3600.0;
    }

    /// <summary>One kept frame and the field centre it was rendered for.</summary>
    private sealed record RecordedFrame(string File, double RaDegrees, double DecDegrees);
}
