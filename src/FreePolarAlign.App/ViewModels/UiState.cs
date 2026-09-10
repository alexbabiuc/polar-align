using FreePolarAlign.Core.Engine;

namespace FreePolarAlign.App.ViewModels;

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

    public string StatusMessage { get; init; } = "Idle. Press Start to begin an alignment session.";

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

    public int CapturedPointCount { get; init; }

    public int PlannedPointCount { get; init; }

    public double SiteLatitudeDegrees { get; init; }

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

    public static UiState Initial(double siteLatitudeDegrees) => new()
    {
        SiteLatitudeDegrees = siteLatitudeDegrees,
    };
}
