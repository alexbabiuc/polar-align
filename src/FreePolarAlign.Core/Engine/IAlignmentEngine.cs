namespace FreePolarAlign.Core.Engine;

/// <summary>Which of the two devices a command or event is about.</summary>
public enum DeviceKind
{
    Camera,
    Mount
}

/// <summary>
/// Whether the mount is driving at sidereal rate. <see cref="Unknown"/> is a
/// real answer, not a placeholder: plenty of drivers do not report tracking, and
/// the UI must say "unknown" rather than imply "stopped" -- a user who believes
/// tracking is off when it is on will misread every subsequent reading.
/// </summary>
public enum MountTrackingState
{
    Unknown,
    Tracking,
    Stopped
}

/// <summary>
/// Which side of the meridian the mount reports. Mirrors
/// <c>FreePolarAlign.Devices.PierSide</c> deliberately rather than reusing it:
/// this boundary is meant to be serialisable and driveable by a remote client
/// (D6), and a plugin author implementing the device contracts should not have
/// to take a dependency on the astrometry core to do so. The Session layer maps
/// between the two.
/// </summary>
public enum MeridianSide
{
    Unknown,
    East,
    West
}

/// <summary>
/// Configuration for one alignment session. Immutable so it can cross the
/// engine boundary as part of a command without aliasing concerns (D6).
///
/// The observing site is deliberately *not* here. It arrives by its own
/// <see cref="ConfigureSiteCommand"/> and is confirmed once per session rather
/// than being restated with every start, so there is exactly one authoritative
/// copy of the most safety-critical number in the system (D19: a latitude error
/// transfers one-for-one into the reported altitude error).
///
/// Neither is the exposure, any more. It is camera state the capture loop reads
/// at every frame boundary (<see cref="SetExposureCommand"/>), because it can be
/// changed at any time, sequence or not (D22 as revised).
/// </summary>
/// <param name="CapturePoints">How many samples to take. D7: never less than 3.</param>
/// <param name="RequestedSweepDegrees">
/// Sweep to aim for. Phase 1 measured axis uncertainty falling as the *square*
/// of this against only the square root of the capture count, so it is the
/// parameter worth spending on -- but it is a request, not a guarantee: the
/// planner shrinks it rather than plan a capture below the altitude floor or
/// across the meridian, and reports what it actually achieved.
/// </param>
public sealed record SessionConfiguration(
    int CapturePoints,
    double RequestedSweepDegrees);

/// <summary>
/// How a sequence reaches its samples, decided by what is connected rather than
/// by a switch (D10 as revised).
/// </summary>
public enum SequenceMode
{
    /// <summary>A connected mount that can slew: the engine proposes, the user confirms, the engine slews.</summary>
    Driven,

    /// <summary>
    /// A connected mount that cannot slew. It is read but not driven: its
    /// reported position decides the spacing between samples and catches
    /// declination movement.
    /// </summary>
    Observed,

    /// <summary>No mount at all. Positions come from blind solves, and settling is judged by two agreeing solves (D26).</summary>
    Unconnected,
}

/// <summary>What caused a solve to be attempted (D26).</summary>
public enum SampleTrigger
{
    /// <summary>A slew ended -- the engine's own or one made from outside -- and the mount has settled.</summary>
    SlewEnded,

    /// <summary>The unconnected cadence: a fixed interval after the previous solve's result.</summary>
    Periodic,

    /// <summary>A post-slew solve failed and is being tried again on a mount that has not moved.</summary>
    Retry,

    /// <summary>The user asked for a sample now, whatever the spacing or stability.</summary>
    Forced,
}

/// <summary>
/// The engine's estimate of polar misalignment at a point in time. Arcminutes,
/// matching the units the UI displays (Phase 4) and the accuracy language used
/// throughout the roadmap.
///
/// Extended in Phase 1, which established that the two bolt figures alone are
/// not sufficient to drive the UI: the success indication is judged on the
/// *total* angle between the axes, which is neither of them and is not their
/// sum, and "the actual figure always visible" is only meaningful alongside how
/// well that figure is known.
/// </summary>
/// <param name="TotalErrorArcminutes">
/// Great-circle angle between the mount axis and the pole -- the figure the 10
/// arcminute success indication is judged on, since it governs field rotation
/// and drift.
/// </param>
public sealed record AlignmentEstimate(
    double AltitudeErrorArcminutes,
    double AzimuthErrorArcminutes,
    double TotalErrorArcminutes,
    double AltitudeSigmaArcminutes,
    double AzimuthSigmaArcminutes,
    double TotalSigmaArcminutes,
    double ResidualRmsArcseconds);

/// <summary>
/// One captured, solved sky position used by the circle fit.
/// </summary>
public sealed record CapturePoint(
    int Index,
    double RaDegrees,
    double DecDegrees,
    DateTime ExposureMidpointUtc);

/// <summary>
/// What is known about the connected camera. Sent with
/// <see cref="DeviceConnectedEvent"/> because the pixel pitch and sensor size
/// are the two numbers the plate scale depends on, and a user checking why a
/// solve keeps failing needs to see the values the software is actually using
/// rather than the ones on the box.
/// </summary>
/// <param name="ReadoutModes">
/// The modes the driver offers, or empty when it offers no choice. Empty is the
/// common case: most cameras have one readout, and presenting a menu of one
/// implies a decision the user does not have to make.
/// </param>
/// <param name="HasSetupDialog">
/// True when the driver has a settings window of its own. Reported at connect
/// time because it decides whether the UI can offer to open it, and there are
/// settings behind that window -- gain above all -- that this project does not
/// model and that decide whether a frame is usable.
/// </param>
/// <param name="UniqueId">
/// The camera's own identity -- normally its serial number -- or null when it
/// will not say. Keys the settings remembered per camera, so it has to be
/// something two cameras of the same model do not share.
/// </param>
/// <param name="Gain">Null when gain is not controlled from here, which is every ASCOM camera (D23).</param>
public sealed record CameraDescription(
    double PixelSizeMicrons,
    int SensorWidthPixels,
    int SensorHeightPixels,
    IReadOnlyList<CameraReadoutModeDescription>? ReadoutModes = null,
    int? ReadoutModeIndex = null,
    bool HasSetupDialog = false,
    string? UniqueId = null,
    CameraGainDescription? Gain = null);

/// <summary>
/// A camera's gain: the percentage the user sees and the raw value the camera
/// was actually given, with the range the one was mapped onto the other.
///
/// Both travel together because each answers a different question. The
/// percentage is what the control shows and what is remembered; the raw value
/// is what goes in the FITS header and what another program for the same
/// camera would call it.
/// </summary>
public sealed record CameraGainDescription(int Percent, int Value, int Minimum, int Maximum);

/// <param name="BitDepth">
/// Bits per pixel where the driver reveals it, null otherwise. Null is a real
/// answer rather than a gap -- see <c>FreePolarAlign.Devices.CameraReadoutMode</c>.
/// </param>
public sealed record CameraReadoutModeDescription(int Index, string Name, int? BitDepth = null)
{
    public string Label => BitDepth is { } bits ? $"{Name} ({bits}-bit)" : Name;
}

/// <summary>
/// Base type for everything sent into an <see cref="IAlignmentEngine"/>. Commands
/// are the only way callers (the UI, or a future remote client per D6) affect
/// engine state -- there is no other public mutator anywhere on the boundary.
/// </summary>
public abstract record EngineCommand;

/// <summary>
/// Open and connect one device. The provider name and device id are the two
/// strings <c>DeviceCatalog</c> hands out; keeping them as strings rather than
/// an object reference is what lets this command cross a transport (D6).
/// </summary>
public sealed record ConnectDeviceCommand(DeviceKind Kind, string ProviderName, string DeviceId) : EngineCommand;

/// <summary>
/// Disconnect and release one device. Refused while a session is running: a
/// half-connected sequence is not a state worth supporting, and pulling the
/// camera out from under a capture would surface as an opaque driver error
/// rather than as the deliberate act it was.
/// </summary>
public sealed record DisconnectDeviceCommand(DeviceKind Kind) : EngineCommand;

/// <summary>
/// Set the observing site. Required before a session can start, and required to
/// be an explicit act rather than something inherited silently from a stored
/// file or a driver default -- see D19.
/// </summary>
public sealed record ConfigureSiteCommand(
    double LatitudeDegrees,
    double LongitudeDegrees,
    double HeightMeters) : EngineCommand;

/// <summary>
/// Tell the engine the focal length the user believes they have. Null clears it,
/// forcing fully blind solves. A solve replaces this figure with the measured
/// one (<see cref="EquipmentConfiguredEvent.IsFocalLengthSolved"/>), which is
/// the whole point of asking for it only once.
/// </summary>
public sealed record ConfigureFocalLengthCommand(double? FocalLengthMillimetres) : EngineCommand;

/// <summary>
/// Ask the engine to poll the mount and report where it is pointing, whether
/// it is tracking and whether it is moving. Polling is still driven from
/// outside, but the engine is no longer a pure function of its commands: a poll
/// that finds a slew has ended starts a settle timer inside the engine (D26).
/// </summary>
public sealed record RefreshMountStatusCommand : EngineCommand;

/// <summary>
/// Select one of the camera's readout modes. Accepted at any time, sequence or
/// not, and applied between frames: the bit depth changes how noisy a solved
/// position is, not where it is (D22, D25 as revised).
/// </summary>
public sealed record SetReadoutModeCommand(int Index) : EngineCommand;

/// <summary>
/// Open the camera driver's own settings window, and do not return until the
/// user closes it.
///
/// The engine handles this like any other command, which means it holds the
/// command gate for as long as the window is open and nothing else runs. That
/// is the intent rather than a side effect: the window changes the device the
/// next exposure will come from, so a capture overlapping it would be taken
/// half under the old settings and half under the new.
///
/// Refused during a sequence, though no longer for the reason the readout mode
/// once was: the window can change binning and region of interest, which change
/// the plate scale the sequence has already measured (D23 as revised). Outside a
/// sequence the capture loop finishes its frame and waits for the window.
/// </summary>
/// <param name="ProviderName">
/// With <paramref name="DeviceId"/>, the camera to configure when none is
/// connected. An ASCOM driver's window is meant to be used before connecting --
/// it is where the camera is chosen and set up in the first place -- so the
/// engine opens the driver just long enough to show it. Ignored when a camera
/// is connected: then the connected one is the one being configured.
/// </param>
public sealed record OpenCameraSetupDialogCommand(string? ProviderName = null, string? DeviceId = null) : EngineCommand;

/// <summary>
/// Set the connected camera's gain, as a percentage of its own range.
///
/// A percentage rather than a raw value so the command means the same thing
/// whichever camera receives it; the engine maps it onto the camera's range.
/// Accepted at any time and applied between frames, for the reason the readout
/// mode is (D25 as revised).
/// </summary>
public sealed record SetCameraGainCommand(int Percent) : EngineCommand;

/// <summary>
/// Set the exposure used for every frame from the next one on. Not device state
/// -- it is an argument to each exposure -- so it is held by the engine, which
/// owns the capture loop. Accepted at any time (D22 as revised); the frame in
/// progress is never aborted, because drivers support that unevenly and the
/// next frame is at most a couple of seconds away.
/// </summary>
public sealed record SetExposureCommand(TimeSpan Duration) : EngineCommand;

/// <summary>
/// Plan a sequence and begin sampling. With a mount connected it is anchored
/// where the mount already points; without one it is planned from the site
/// and the clock, and the user is told where to set the telescope (D10). Moves
/// nothing (D18).
/// </summary>
public sealed record StartSessionCommand(SessionConfiguration Configuration) : EngineCommand;

/// <summary>
/// Solve the next frame that *starts* after this command and use it as a
/// sample, whatever the spacing (D27) or stability (D26). The frame already on
/// screen is never used, because it may have been exposed while the telescope
/// was still moving. D11's checks still apply to the fit.
/// </summary>
public sealed record RecordSampleCommand : EngineCommand;

/// <summary>
/// Accept the engine's own proposal for the next point unedited: slew there,
/// and sample once the mount has settled (D26). Equivalent to
/// <see cref="ConfirmSlewCommand"/> with the proposed coordinates, and kept
/// separate only so a caller that is not overriding anything need not echo
/// numbers back.
/// </summary>
public sealed record CaptureNextPointCommand : EngineCommand;

/// <summary>
/// Slew to coordinates the user supplied -- possibly the proposed ones, possibly
/// not -- and sample there once settled.
///
/// The override exists because the engine cannot see the sky: trees, a
/// neighbour's roof and cloud in one quadrant are all invisible to it, and a
/// planner that insists on its own choice would simply be wrong more often than
/// the user. What the engine can do is notice when an override moves the mount
/// in *declination*, which invalidates the small-circle fit the whole method
/// rests on, and re-anchor rather than quietly average incompatible points
/// together (D18).
/// </summary>
public sealed record ConfirmSlewCommand(double RaDegrees, double DecDegrees) : EngineCommand;

public sealed record CancelSessionCommand : EngineCommand;

/// <summary>An operator- or safety-triggered abort, distinct from a plain cancel (carries a reason for the log/UI).</summary>
public sealed record AbortSessionCommand(string Reason) : EngineCommand;

/// <summary>
/// Base type for everything the engine emits. Events are the only way the
/// engine communicates state changes outward (D6: "All engine state changes
/// must be expressible as serialisable events") -- there is no shared mutable
/// state, and no property on the engine that reflects "current" state; callers
/// reconstruct whatever state they need by observing the stream.
/// </summary>
public abstract record EngineEvent
{
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <param name="Camera">Populated only when <paramref name="Kind"/> is <see cref="DeviceKind.Camera"/>.</param>
/// <param name="CanSlew">
/// Populated only for a mount. False means automatic mode is unavailable (D9)
/// and the sequence has to be driven by hand (D10) -- worth knowing at connect
/// time rather than discovering at the first slew.
/// </param>
public sealed record DeviceConnectedEvent(
    DeviceKind Kind,
    string ProviderName,
    string DeviceId,
    string DisplayName,
    string DriverInfo,
    CameraDescription? Camera = null,
    bool CanSlew = false) : EngineEvent;

/// <summary>
/// The camera driver's settings window has opened or closed.
///
/// Published either side of a blocking call, so a UI can refuse every other
/// operation while it is open. Without it the application would sit there
/// looking idle and accepting clicks while a modal window belonging to another
/// process's UI stack had the device.
/// </summary>
public sealed record CameraSetupDialogChangedEvent(bool IsOpen) : EngineEvent;

/// <summary>
/// The camera's gain after a change, read back from the camera rather than
/// echoed from the request -- a camera clamps an out-of-range value, and the UI
/// should show what the frames will actually be taken at.
/// </summary>
public sealed record CameraGainChangedEvent(CameraGainDescription Gain) : EngineEvent;

/// <param name="Reason">Null for a deliberate disconnect; a message when the device dropped or failed to open.</param>
public sealed record DeviceDisconnectedEvent(DeviceKind Kind, string? Reason = null) : EngineEvent;

/// <param name="MountReportedDisagreement">
/// Non-null when the mount's own site differs from the confirmed one by enough
/// to matter. Not an error and not overridden -- the user's confirmed figure
/// wins (D19) -- but it is very often the first sign that one of the two is a
/// leftover from somewhere else entirely, so it is surfaced rather than
/// resolved silently.
/// </param>
public sealed record SiteConfiguredEvent(
    double LatitudeDegrees,
    double LongitudeDegrees,
    double HeightMeters,
    string? MountReportedDisagreement = null) : EngineEvent;

/// <param name="IsFocalLengthSolved">
/// False while this is still the user's own figure, true once a solve measured
/// it. The distinction drives how wide a scale hint the solver is given, and
/// the UI shows it because "1000 mm (measured)" and "1000 mm (as entered)" mean
/// quite different things when a solve is failing.
/// </param>
public sealed record EquipmentConfiguredEvent(
    double? FocalLengthMillimetres,
    bool IsFocalLengthSolved,
    double? ScaleArcsecondsPerPixel,
    double? FieldRadiusDegrees) : EngineEvent;

/// <summary>
/// Where the mount says it is and what it is doing. The coordinates are the
/// mount's own belief, not a solved position -- on a misaligned mount those are
/// different things, and the difference is exactly what this software measures,
/// so the UI labels this as reported rather than as truth.
/// </summary>
/// <param name="IsMoving">
/// The engine's judgement, not only the driver's: the driver reports slewing,
/// or the reported position changed since the last poll. Both, because whether
/// a driver reports hand-controller motion as slewing is unknown (D26).
/// </param>
public sealed record MountStatusEvent(
    double RaDegrees,
    double DecDegrees,
    MountTrackingState Tracking,
    MeridianSide PierSide,
    bool IsMoving = false) : EngineEvent;

public sealed record SessionStartedEvent(SessionConfiguration Configuration, SequenceMode Mode = SequenceMode.Driven) : EngineEvent;

/// <summary>The exposure every frame from the next one on will be taken at.</summary>
public sealed record ExposureChangedEvent(TimeSpan Duration) : EngineEvent;

/// <param name="SweepDegrees">
/// The sweep actually planned, which may be less than requested: the planner
/// shrinks rather than plan a capture below the altitude floor or across the
/// meridian. Since uncertainty scales as the inverse square of this, a shrunk
/// sweep is a materially worse measurement and the UI says so.
/// </param>
public sealed record TargetSelectedEvent(
    double DeclinationDegrees,
    bool IsWestOfMeridian,
    int PlannedCaptures,
    double SweepDegrees) : EngineEvent;

/// <summary>
/// The engine's suggestion for where to point next, and the reason the sequence
/// is now waiting rather than moving (D18). Nothing turns until a
/// <see cref="CaptureNextPointCommand"/> or <see cref="ConfirmSlewCommand"/>
/// arrives.
/// </summary>
/// <param name="RaDegrees">
/// Resolved for roughly now. The engine re-resolves at the instant of the slew,
/// because a fixed sky coordinate does not hold the mechanical declination
/// constant as the sky rotates (see <c>TargetSelection.ResolveCommand</c>), so
/// this figure is what to *show*, not what will necessarily be commanded.
/// </param>
/// <param name="RequiresMotion">
/// False when the telescope is already somewhere usable -- the first point of a
/// sequence anchored where it was already pointing -- or when it is being turned
/// by hand (D10). A caller must not offer to slew in that case: presenting a
/// movement that will not happen invites the user to go and check the sky for an
/// obstruction that does not matter.
/// </param>
public sealed record SlewProposedEvent(
    int PointIndex,
    int PlannedCaptures,
    double RaDegrees,
    double DecDegrees,
    double MechanicalRotationDegrees,
    double PredictedAltitudeDegrees,
    bool RequiresMotion,
    string Instruction) : EngineEvent;

/// <param name="WasOverridden">True when the coordinates were not the ones proposed.</param>
/// <param name="ReanchoredReason">
/// Non-null when the override moved the mount in declination and the sequence
/// therefore restarted from this point, discarding earlier captures. Points at
/// different declinations do not lie on one small circle, so fitting them
/// together would produce a confident wrong answer -- the failure mode D11
/// exists to prevent (D18).
/// </param>
public sealed record SlewConfirmedEvent(
    double RaDegrees,
    double DecDegrees,
    bool WasOverridden,
    string? ReanchoredReason = null) : EngineEvent;

/// <summary>
/// A frame has been exposed and written to disk. The camera runs continuously
/// while connected (D26), so this arrives every frame, sequence or not; whether
/// a frame is then solved is announced separately by
/// <see cref="SolveStartedEvent"/>.
///
/// Published before any solve, on purpose. The moment a user most needs to see
/// the frame is when the solve has just failed -- "no stars detected" is
/// answered by looking at the image, where a lens cap, cloud, a wildly wrong
/// focus or a tracking runaway are all obvious and none of them are
/// distinguishable from the message alone.
/// </summary>
/// <param name="FitsPath">
/// A local filesystem path, which is the one thing on this boundary that does
/// not survive a transport (D6). The file lives until the next frame replaces
/// it on screen, and longer only while it is being solved (D26); a reader that
/// finds it gone has simply been overtaken by a newer frame.
/// </param>
/// <param name="GainValue">The camera's raw gain for this frame, where the engine controls it; null otherwise (D25).</param>
public sealed record FrameCapturedEvent(
    string FitsPath,
    DateTime ExposureMidpointUtc,
    TimeSpan Duration,
    int? GainValue = null) : EngineEvent;

/// <summary>A solve will be attempted after <paramref name="Delay"/>, unless the mount moves first (D26).</summary>
public sealed record SolveScheduledEvent(SampleTrigger Trigger, TimeSpan Delay) : EngineEvent;

/// <summary>This frame is being solved. At most one solve runs at a time (D26).</summary>
public sealed record SolveStartedEvent(SampleTrigger Trigger, string FitsPath) : EngineEvent;

/// <summary>
/// A solve failed. Not a session fault, and in the live loop not a reason to
/// give up either: most failures are frames taken while the mount was still
/// being moved, so a count of them measures the user's pace rather than a
/// fault (D26).
/// </summary>
/// <param name="KeptFramePath">Where the frame was kept for diagnosis (D26: the last 20), or null if it could not be.</param>
public sealed record SolveFailedEvent(
    SampleTrigger Trigger,
    string Reason,
    int ConsecutiveFailures,
    string? KeptFramePath = null) : EngineEvent;

/// <summary>
/// A solve succeeded but did not become a sample: the mount has not turned far
/// enough since the last one (D27), or has not yet been seen to settle (D26).
/// Said rather than silent, so a user turning the mount by hand can see how
/// much further to go.
/// </summary>
public sealed record SampleSkippedEvent(string Reason) : EngineEvent;

/// <summary>
/// The samples taken so far were discarded and the sequence restarted from the
/// current position, because the mount moved in declination. Samples at two
/// declinations lie on two circles, and fitting them together gives a
/// confident wrong answer (D11, D18).
/// </summary>
public sealed record SequenceRestartedEvent(string Reason) : EngineEvent;

public sealed record ReadoutModeChangedEvent(int Index, string Name, int? BitDepth) : EngineEvent;

public sealed record PointCapturedEvent(CapturePoint Point, SampleTrigger Trigger = SampleTrigger.SlewEnded) : EngineEvent;

/// <summary>
/// Emitted after every capture point once >= 3 points are available (D7). The
/// engine re-fits and re-emits after each new point, which is also the
/// mechanism behind freeze-and-track mode (Phase 4).
/// </summary>
public sealed record AlignmentUpdatedEvent(AlignmentEstimate Estimate) : EngineEvent;

/// <summary>
/// A likely declination drift or meridian flip was detected from fit residuals
/// (D11) and the result is being withheld rather than trusted.
/// </summary>
public sealed record AlignmentWithheldEvent(string Reason) : EngineEvent;

public sealed record SessionFaultedEvent(string Reason) : EngineEvent;

public sealed record SessionCompletedEvent : EngineEvent;

/// <summary>
/// The message-shaped engine boundary (D6). The UI (or, later, a remote
/// client on a transport -- D6's "engine on a mini-PC, UI on a tablet") never
/// calls into engine internals: it sends <see cref="EngineCommand"/>s and
/// observes <see cref="EngineEvent"/>s. No engine state is exposed as a
/// gettable property, and no type on this boundary is mutable, so there is
/// nothing to alias across the boundary.
/// </summary>
public interface IAlignmentEngine
{
    /// <summary>
    /// The engine's full output. A plain BCL type (no reactive extensions
    /// dependency) so multiple observers (UI, logger, diagnostic recorder) can
    /// subscribe independently.
    /// </summary>
    IObservable<EngineEvent> Events { get; }

    ValueTask SendAsync(EngineCommand command, CancellationToken cancellationToken = default);
}
