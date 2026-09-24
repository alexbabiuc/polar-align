using FreePolarAlign.Core.Engine;

namespace FreePolarAlign.App.ViewModels;

/// <summary>
/// One connected (or not connected) device, as the UI shows it.
/// </summary>
/// <param name="Driver">
/// Provider and driver identification. Shown because "it will not connect" is
/// answered far more often by seeing which driver was actually opened than by
/// any error message.
/// </param>
public sealed record DeviceView(
    bool IsConnected,
    string? Name = null,
    string? Driver = null,
    string? Problem = null)
{
    public static DeviceView Absent { get; } = new(IsConnected: false);
}

/// <summary>
/// The suggestion the engine is waiting on (D18), including the coordinates
/// pre-filled into the editable fields.
/// </summary>
/// <param name="RequiresMotion">
/// False when the telescope is already somewhere usable, or is being turned by
/// hand (D10). The UI then offers no slew at all, because presenting a movement
/// that is not going to happen invites the user to check the sky for an
/// obstruction that does not matter.
/// </param>
public sealed record ProposalView(
    int PointIndex,
    int PlannedCaptures,
    double RaDegrees,
    double DecDegrees,
    double MechanicalRotationDegrees,
    double PredictedAltitudeDegrees,
    bool RequiresMotion,
    string Instruction);

/// <summary>What the engine last said about getting the next sample (D26).</summary>
public enum SamplingActivity
{
    None,

    /// <summary>A solve is counting down; <see cref="SamplingView.Trigger"/> says why.</summary>
    Scheduled,

    Solving,

    /// <summary>A solve succeeded but did not become a sample; <see cref="SamplingView.Detail"/> says why.</summary>
    Skipped,

    /// <summary>The last solve failed. The count is in <see cref="SamplingView.ConsecutiveFailures"/>.</summary>
    Failed,

    Sampled,
}

/// <param name="SolveDueUtc">When a scheduled solve is due, so the countdown can be shown without another event.</param>
/// <param name="ConsecutiveFailures">
/// Kept across the reschedule that follows a failure, and cleared only by a
/// solve that worked. Failures while the mount is being turned are normal
/// (D26), so the count is what the user needs, not the latest failure alone.
/// </param>
public sealed record SamplingView(
    SamplingActivity Activity,
    SampleTrigger Trigger = SampleTrigger.Periodic,
    DateTimeOffset? SolveDueUtc = null,
    string? Detail = null,
    int ConsecutiveFailures = 0)
{
    public static SamplingView Idle { get; } = new(SamplingActivity.None);
}

/// <summary>
/// Everything the UI shows, reconstructed entirely from the engine's event
/// stream (D6: the engine exposes no gettable state, so a caller that wants to
/// know "what is going on right now" has to keep its own model of it). This is
/// that model.
///
/// Immutable and replaced wholesale on every event by <see cref="EngineEventReducer"/>
/// rather than mutated in place, so the reducer stays a pure, easily-tested
/// function from (old state, event) to new state with no hidden dependency on
/// evaluation order.
/// </summary>
public sealed record UiState
{
    public bool SessionActive { get; init; }

    public string StatusMessage { get; init; } =
        "Idle. Connect a camera, confirm your site, then start a sequence. A mount is optional.";

    /// <summary>
    /// How the running sequence reaches its samples. Meaningful only while
    /// <see cref="SessionActive"/>; decided by what is connected (D10).
    /// </summary>
    public SequenceMode SequenceMode { get; init; } = SequenceMode.Driven;

    // ---- Devices ----

    public DeviceView Camera { get; init; } = DeviceView.Absent;

    public DeviceView Mount { get; init; } = DeviceView.Absent;

    public double? CameraPixelSizeMicrons { get; init; }

    public int CameraWidthPixels { get; init; }

    public int CameraHeightPixels { get; init; }

    /// <summary>
    /// The readout modes the driver offers. Empty means it offers no choice,
    /// which is the common case and not a failure -- most cameras have one
    /// readout, and presenting a menu of one implies a decision the user does
    /// not have to make.
    /// </summary>
    public IReadOnlyList<CameraReadoutModeDescription> ReadoutModes { get; init; } =
        Array.Empty<CameraReadoutModeDescription>();

    public int? ReadoutModeIndex { get; init; }

    /// <summary>
    /// True when the connected camera's driver has a settings window this
    /// application can open. False for the simulator, and for any camera that is
    /// not reached through a driver with one.
    /// </summary>
    public bool CameraHasSetupDialog { get; init; }

    /// <summary>
    /// Which camera is connected, as the key its settings are remembered under
    /// (see <c>AppSettings.CameraKey</c>). Null when none is.
    /// </summary>
    public string? CameraSettingsKey { get; init; }

    /// <summary>
    /// The connected camera's gain, or null when it is not set from here --
    /// every ASCOM camera, whose gain belongs to its driver's own window (D23).
    /// </summary>
    public CameraGainDescription? CameraGain { get; init; }

    /// <summary>
    /// True while that window is open. Everything else must be refused until it
    /// closes: the window changes the device the next exposure comes from, and
    /// the engine is in any case blocked waiting for it, so a UI that still
    /// accepted clicks would only queue them up behind a window it could not see.
    /// </summary>
    public bool CameraSetupDialogOpen { get; init; }

    /// <summary>
    /// The most recently exposed frame, on disk. The camera runs continuously
    /// (D26), so this changes every frame, and the file it names is deleted
    /// once a newer one supersedes it: it says what to load next, never what
    /// can still be read later.
    /// </summary>
    public string? LatestFramePath { get; init; }

    public DateTime? LatestFrameMidpointUtc { get; init; }

    /// <summary>
    /// The exposure the engine says frames are being taken at. Null until it
    /// has said; the picker is compared against it, not driven by it.
    /// </summary>
    public TimeSpan? EngineExposure { get; init; }

    /// <summary>
    /// False when the connected mount cannot perform absolute slews (D9), which
    /// makes automatic mode unavailable and manual mode (D10) the only option.
    /// Worth showing at connect time rather than at the first slew.
    /// </summary>
    public bool MountCanSlew { get; init; }

    /// <summary>
    /// Where the mount believes it is. Its belief, not a solved position: on a
    /// misaligned mount those differ by exactly the error being measured, so the
    /// UI labels it as reported.
    /// </summary>
    public double? MountRaDegrees { get; init; }

    public double? MountDecDegrees { get; init; }

    public MountTrackingState MountTracking { get; init; } = MountTrackingState.Unknown;

    public MeridianSide MountPierSide { get; init; } = MeridianSide.Unknown;

    /// <summary>
    /// The engine's judgement that the mount is being moved. Any motion
    /// cancels a pending solve (D26), so while this is true nothing will be
    /// sampled, and the sampling status says so.
    /// </summary>
    public bool MountIsMoving { get; init; }

    // ---- Site and equipment ----

    /// <summary>
    /// Null until the site has been explicitly confirmed. Null is the honest
    /// value: a session cannot start without it, and defaulting it to zero would
    /// put the Gulf of Guinea into the arithmetic (D19).
    /// </summary>
    public double? SiteLatitudeDegrees { get; init; }

    public double? SiteLongitudeDegrees { get; init; }

    public double? SiteHeightMeters { get; init; }

    public bool IsSiteConfigured => SiteLatitudeDegrees is not null;

    /// <summary>Non-null when the mount's own site disagrees with the confirmed one by enough to matter.</summary>
    public string? SiteDisagreement { get; init; }

    public double? FocalLengthMillimetres { get; init; }

    public bool IsFocalLengthSolved { get; init; }

    public double? ScaleArcsecondsPerPixel { get; init; }

    public double? FieldRadiusDegrees { get; init; }

    // ---- The sequence ----

    /// <summary>
    /// The most recent estimate the engine still stands behind. Null whenever
    /// the last thing the engine said about the fit was "don't trust this" --
    /// see <see cref="WithheldReason"/> and <see cref="FaultReason"/>, which are
    /// mutually exclusive with this being non-null by construction in
    /// <see cref="EngineEventReducer"/>. A confident stale number surviving a
    /// withheld or faulted event is exactly the failure mode D11 exists to
    /// prevent, so the reducer clears it rather than leaving the last good
    /// value on screen under a banner.
    /// </summary>
    public AlignmentEstimate? CurrentEstimate { get; init; }

    /// <summary>Non-null exactly when the last relevant event was <see cref="AlignmentWithheldEvent"/>.</summary>
    public string? WithheldReason { get; init; }

    /// <summary>Non-null exactly when the session last faulted, until a new session starts.</summary>
    public string? FaultReason { get; init; }

    /// <summary>
    /// What the user should do next, from whichever of
    /// <see cref="ManualActionRequiredEvent"/> and <see cref="SlewProposedEvent"/>
    /// spoke last. Without a mount (D10) this is the whole of the guidance:
    /// which declination to set, then how far to turn.
    /// </summary>
    public string? GuidanceInstruction { get; init; }

    public SamplingView Sampling { get; init; } = SamplingView.Idle;

    /// <summary>
    /// What the engine has proposed (D18). Null whenever nothing is pending.
    /// Only a proposal that <see cref="ProposalView.RequiresMotion"/> is waiting
    /// on the user; the others are carried out by the user at the mount and
    /// sampled when it settles (D26).
    /// </summary>
    public ProposalView? Proposal { get; init; }

    /// <summary>
    /// The most recent refusal -- a command the engine declined because the
    /// state was wrong for it. Cleared by the next event that changes anything,
    /// because a refusal describes a moment rather than a condition.
    /// </summary>
    public string? RejectionReason { get; init; }

    public int CapturedPointCount { get; init; }

    public int PlannedPointCount { get; init; }

    /// <summary>
    /// The sweep the planner actually achieved, and the one that was asked for.
    /// Uncertainty scales as the inverse square of the sweep, so a plan that had
    /// to shrink is a materially worse measurement and the UI says so rather
    /// than reporting only the number of points.
    /// </summary>
    public double? PlannedSweepDegrees { get; init; }

    public double? RequestedSweepDegrees { get; init; }

    public IReadOnlyList<string> Log { get; init; } = Array.Empty<string>();

    /// <summary>
    /// A fresh state with nothing configured. Deliberately takes no site: the
    /// site arrives by <see cref="SiteConfiguredEvent"/> only, so that there is
    /// exactly one path by which the most safety-critical number in the system
    /// can be set.
    /// </summary>
    public static UiState Initial() => new();
}
