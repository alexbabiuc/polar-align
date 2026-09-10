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
/// hand (D10). The UI offers "capture" rather than "slew and capture", because
/// presenting a movement that is not going to happen invites the user to check
/// the sky for an obstruction that does not matter.
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

    public bool AwaitingManualAction { get; init; }

    public string StatusMessage { get; init; } =
        "Idle. Connect a camera and a mount, confirm your site, then start a sequence.";

    // ---- Devices ----

    public DeviceView Camera { get; init; } = DeviceView.Absent;

    public DeviceView Mount { get; init; } = DeviceView.Absent;

    public double? CameraPixelSizeMicrons { get; init; }

    public int CameraWidthPixels { get; init; }

    public int CameraHeightPixels { get; init; }

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

    /// <summary>D10: the instruction to show while <see cref="AwaitingManualAction"/> is true.</summary>
    public string? ManualInstruction { get; init; }

    /// <summary>
    /// What the engine has proposed and is waiting for confirmation of (D18).
    /// Null whenever nothing is pending, which is also the condition under which
    /// no capture affordance may be enabled.
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

    /// <summary>
    /// A prominent warning surfaced during the *last* capture attempt (D11's
    /// third named case: "Same for ... CaptureFailedEvent"). Deliberately kept
    /// separate from <see cref="CurrentEstimate"/> rather than clearing it: a
    /// failed capture does not retroactively invalidate the fit computed from
    /// points already captured, so the honest response is to flag the failure
    /// prominently alongside whatever number is still on screen, not to hide a
    /// number that is not actually stale. It is cleared as soon as a capture
    /// succeeds or a new session starts.
    /// </summary>
    public string? CaptureWarning { get; init; }

    public IReadOnlyList<string> Log { get; init; } = Array.Empty<string>();

    /// <summary>
    /// A fresh state with nothing configured. Deliberately takes no site: the
    /// site arrives by <see cref="SiteConfiguredEvent"/> only, so that there is
    /// exactly one path by which the most safety-critical number in the system
    /// can be set.
    /// </summary>
    public static UiState Initial() => new();
}
