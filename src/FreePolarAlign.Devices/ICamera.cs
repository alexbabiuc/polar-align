namespace FreePolarAlign.Devices;

/// <summary>
/// One completed exposure. <paramref name="ExposureMidpointUtc"/> is the
/// timestamp that matters for this project (README: "Sidereal tracking is
/// harmless... we... timestamp exposure midpoints"), not start or end time.
/// </summary>
public sealed record CapturedImage(string FitsPath, DateTime ExposureMidpointUtc, TimeSpan Duration);

/// <summary>
/// One way a camera can be read out, as the driver describes it.
/// </summary>
/// <param name="Name">
/// The driver's own name for the mode, passed through verbatim. Inventing a
/// tidier one would mean guessing at what it does; the person who chose this
/// camera recognises the driver's wording.
/// </param>
/// <param name="BitDepth">
/// Bits per pixel, when the driver actually reveals it, and null otherwise.
/// Null is a real answer: the standard device interfaces expose readout modes
/// as opaque names, and claiming a depth that was inferred from a substring
/// match would be worse than admitting it is unknown -- the depth changes how
/// far a centroid can be trusted, so a wrong one is misleading rather than
/// merely unhelpful.
/// </param>
public sealed record CameraReadoutMode(int Index, string Name, int? BitDepth = null)
{
    public string Label => BitDepth is { } bits ? $"{Name} ({bits}-bit)" : Name;
}

/// <summary>
/// The raw gain values a camera accepts, in the vendor's own units.
///
/// Deliberately not interpreted. ZWO counts in tenths of a decibel (0 to 600 on
/// an ASI290), ToupTek in percent of unity (100 to 5000 or so), and an ASCOM
/// driver in whatever it likes. Nothing here converts between them, because the
/// user compares the number against the same vendor's documentation and other
/// software for the same camera, not against another brand.
/// </summary>
public sealed record CameraGainRange(int Minimum, int Maximum)
{
    public int Clamp(int value) => Math.Clamp(value, Minimum, Maximum);
}

/// <summary>
/// What the application knows about a frame that the camera cannot, recorded in
/// the file so a frame found later can be understood on its own.
///
/// Every field is optional because every one of them can genuinely be unknown:
/// no mount is connected, no focal length has been entered yet, the capture came
/// from a test harness with no application around it. An absent keyword is an
/// honest answer; a zero would not be.
/// </summary>
/// <param name="MountRaDegrees">
/// Where the mount says it is pointing, at the moment of the exposure. The
/// mount's *belief*, not a measurement: on a misaligned mount it differs from
/// the truth by exactly the error this software exists to measure, which is why
/// it is written under keywords a plate solution never uses.
/// </param>
public sealed record CaptureContext(
    string? ApplicationName = null,
    string? ApplicationVersion = null,
    double? MountRaDegrees = null,
    double? MountDecDegrees = null,
    double? FocalLengthMillimetres = null);

/// <summary>
/// A camera device, opened via <see cref="IDeviceProvider.OpenCamera"/>.
/// Exposure is asynchronous because real cameras and subprocess/network
/// simulators alike take real time and must be cancellable mid-exposure.
/// </summary>
public interface ICamera : IDisposable
{
    string Name { get; }

    int SensorWidthPixels { get; }

    int SensorHeightPixels { get; }

    double PixelSizeMicrons { get; }

    bool IsConnected { get; }

    /// <summary>
    /// The readout modes this camera offers, or an empty list when it offers no
    /// choice. Empty is the common case and is not a failure -- most cameras
    /// have exactly one way of being read out, and a UI should say so rather
    /// than present a menu of one.
    /// </summary>
    IReadOnlyList<CameraReadoutMode> ReadoutModes { get; }

    /// <summary>
    /// Index of the mode now in use, or null if the camera has no selectable
    /// modes or will not say which is active.
    /// </summary>
    int? ReadoutModeIndex { get; }

    /// <summary>
    /// Selects a readout mode. Implementations must reject an index outside
    /// <see cref="ReadoutModes"/> rather than silently leaving the camera as it
    /// was: a user who believes they are reading out at sixteen bits when they
    /// are at eight has no way to discover it from the images.
    /// </summary>
    Task SetReadoutModeAsync(int index, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the driver has a settings window of its own that this
    /// application can put in front of the user.
    ///
    /// It matters because there are camera settings this project deliberately
    /// does not model -- gain, offset, USB bandwidth, cooling -- and on a real
    /// driver they decide whether a frame is usable at all. The alternative to
    /// opening the driver's own window is either reimplementing its settings or
    /// telling the user to go and find another program, and both are worse.
    ///
    /// Defaulted to false so that a camera which has no such window, or a test
    /// double which has no driver at all, says so by saying nothing.
    /// </summary>
    bool HasSetupDialog => false;

    /// <summary>
    /// Shows the driver's own settings window and returns when the user closes
    /// it.
    ///
    /// Blocking is the contract, not an implementation accident. The window
    /// changes the state of the device this application is about to expose
    /// frames with, so the caller has to know when it is finished -- and the
    /// caller is expected to refuse every other operation until it is.
    /// </summary>
    Task ShowSetupDialogAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"Camera '{Name}' has no driver settings window.");

    /// <summary>
    /// An identifier for this physical camera that survives unplugging, a
    /// reboot, and a second camera of the same model -- normally its serial
    /// number. Null when the camera will not say.
    ///
    /// It keys the settings remembered per camera. The device id the provider
    /// hands out at discovery is not good enough for that: for native SDKs it
    /// is an enumeration index or a USB path, and two ASI290s would share a
    /// model name.
    /// </summary>
    string? UniqueId => null;

    /// <summary>
    /// The range <see cref="SetGainAsync"/> accepts, or null when this camera's
    /// gain is not controlled from here. Null for every ASCOM camera: there the
    /// driver's own settings window owns gain (D23), and a second control
    /// fighting it over the same value would leave the user unsure which won.
    /// </summary>
    CameraGainRange? GainRange => null;

    /// <summary>The gain in use, in the same units as <see cref="GainRange"/>. Null when <see cref="GainRange"/> is.</summary>
    int? Gain => null;

    /// <summary>
    /// Sets the gain, in the camera's own units. Implementations clamp to
    /// <see cref="GainRange"/> rather than refuse, and report the value actually
    /// applied through <see cref="Gain"/>, which is what callers should read back.
    /// </summary>
    Task SetGainAsync(int value, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"Camera '{Name}' does not have its gain controlled from here.");

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts and waits out one exposure, returning the captured frame written
    /// to disk as FITS. The FITS file's own WCS keywords (if any) are provider
    /// output, not solver input; solving happens separately via ISolver.
    /// </summary>
    /// <param name="context">
    /// What the caller knows and the camera does not -- which application is
    /// capturing, where the mount believes it is pointing, what focal length is
    /// in use. Written into the frame's header alongside what the camera knows
    /// itself, so that a file recovered from a temp directory a week later can
    /// still say what it is. Optional: a caller with nothing to add passes
    /// nothing.
    /// </param>
    Task<CapturedImage> ExposeAsync(
        TimeSpan duration, CaptureContext? context = null, CancellationToken cancellationToken = default);
}
