using FreePolarAlign.Core.Engine;
using FreePolarAlign.Session;

namespace FreePolarAlign.App.ViewModels;

/// <summary>
/// The pure translation from (previous <see cref="UiState"/>, next <see cref="EngineEvent"/>)
/// to the next <see cref="UiState"/>. This is deliberately the entire
/// "understand the engine's event stream" logic for the app, kept free of
/// Avalonia, threading and I/O so it can be unit-tested directly -- which is
/// where the safety properties this project cares about (D11: never show a
/// stale number as current) actually live and get verified.
/// </summary>
public static class EngineEventReducer
{
    /// <summary>
    /// Bounded so a long-running session (or an all-night one, which is the
    /// actual use case) does not grow the log without limit.
    /// </summary>
    internal const int MaxLogEntries = 1000;

    public static UiState Apply(UiState state, EngineEvent engineEvent)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(engineEvent);

        UiState withLog = AppendLog(state, Describe(engineEvent));

        return engineEvent switch
        {
            SessionStartedEvent e => withLog with
            {
                SessionActive = true,
                AwaitingManualAction = false,
                CurrentEstimate = null,
                WithheldReason = null,
                FaultReason = null,
                ManualInstruction = null,
                CaptureWarning = null,
                CapturedPointCount = 0,
                PlannedPointCount = 0,
                SiteLatitudeDegrees = e.Configuration.SiteLatitudeDegrees,
                StatusMessage = "Session started.",
            },

            TargetSelectedEvent e => withLog with
            {
                PlannedPointCount = e.PlannedCaptures,
                StatusMessage =
                    $"Target selected: {e.PlannedCaptures} captures planned at declination {e.DeclinationDegrees:F1}°, " +
                    $"sweeping {(e.IsWestOfMeridian ? "west" : "east")} of the meridian (D8).",
            },

            PointCapturedEvent e => withLog with
            {
                CapturedPointCount = e.Point.Index,
                AwaitingManualAction = false,
                ManualInstruction = null,
                CaptureWarning = null,
                StatusMessage = $"Captured point {e.Point.Index} of {state.PlannedPointCount}.",
            },

            // The two bolt figures plus the total (which the 10' success
            // indication is actually judged on -- see "What 10 arcminutes
            // means") all come from the same estimate, so there is nothing to
            // reconcile here: this is simply the latest trusted fit.
            AlignmentUpdatedEvent e => withLog with
            {
                CurrentEstimate = e.Estimate,
                WithheldReason = null,
                FaultReason = null,
                StatusMessage = "Alignment estimate updated.",
            },

            // D11: the engine itself has determined this fit cannot be
            // trusted (residuals, conditioning, or curvature identifiability).
            // The reason is surfaced prominently and the numeric estimate is
            // cleared rather than left on screen under a banner.
            AlignmentWithheldEvent e => withLog with
            {
                CurrentEstimate = null,
                WithheldReason = e.Reason,
                StatusMessage = "Result withheld -- see reason below.",
            },

            // D10: manual mode. The operator turns the mount by hand; the
            // "Continue" affordance re-sends CaptureNextPointCommand, which is
            // exactly what AlignmentSession is waiting for.
            ManualActionRequiredEvent e => withLog with
            {
                AwaitingManualAction = true,
                ManualInstruction = e.Instruction,
                StatusMessage = "Manual action required -- see instruction below.",
            },

            // A single failed capture is not a session fault (clouds pass),
            // so the prior estimate is not stale and is left alone -- but the
            // failure itself must not be silent, so it gets its own
            // prominent, separately-tracked warning (see UiState.CaptureWarning).
            CaptureFailedEvent e => withLog with
            {
                CaptureWarning = e.WillRetry
                    ? $"Capture {e.CaptureIndex} failed: {e.Reason} (will retry)."
                    : $"Capture {e.CaptureIndex} failed: {e.Reason} (giving up).",
                StatusMessage = e.WillRetry ? "A capture failed and will be retried." : "A capture failed; the session is stopping.",
            },

            SessionFaultedEvent e => withLog with
            {
                SessionActive = false,
                AwaitingManualAction = false,
                CurrentEstimate = null,
                ManualInstruction = null,
                FaultReason = e.Reason,
                StatusMessage = "Session faulted -- see reason below.",
            },

            // Normal completion. The roadmap is explicit that the actual
            // figure stays visible after success, so CurrentEstimate is left
            // exactly as the last AlignmentUpdatedEvent set it.
            SessionCompletedEvent => withLog with
            {
                SessionActive = false,
                AwaitingManualAction = false,
                ManualInstruction = null,
                StatusMessage = "Session complete.",
            },

            _ => withLog,
        };
    }

    private static UiState AppendLog(UiState state, string line)
    {
        var log = new List<string>(state.Log.Count + 1);
        log.AddRange(state.Log);
        log.Add(line);

        if (log.Count > MaxLogEntries)
        {
            log.RemoveRange(0, log.Count - MaxLogEntries);
        }

        return state with { Log = log };
    }

    private static string Describe(EngineEvent e) => e switch
    {
        SessionStartedEvent => $"{Timestamp(e)} Session started.",
        TargetSelectedEvent t => $"{Timestamp(e)} Target selected: dec {t.DeclinationDegrees:F1}°, " +
                                  $"{t.PlannedCaptures} captures, {t.SweepDegrees:F0}° sweep {(t.IsWestOfMeridian ? "west" : "east")}.",
        PointCapturedEvent p => $"{Timestamp(e)} Point {p.Point.Index} captured: RA {p.Point.RaDegrees:F3}°, Dec {p.Point.DecDegrees:F3}°.",
        AlignmentUpdatedEvent a => $"{Timestamp(e)} Estimate updated: total {a.Estimate.TotalErrorArcminutes:F2}' " +
                                    $"± {a.Estimate.TotalSigmaArcminutes:F2}'.",
        AlignmentWithheldEvent w => $"{Timestamp(e)} WITHHELD: {w.Reason}",
        ManualActionRequiredEvent m => $"{Timestamp(e)} Manual action required: {m.Instruction}",
        CaptureFailedEvent c => $"{Timestamp(e)} Capture {c.CaptureIndex} failed: {c.Reason} (retry: {c.WillRetry}).",
        SessionFaultedEvent f => $"{Timestamp(e)} FAULT: {f.Reason}",
        SessionCompletedEvent => $"{Timestamp(e)} Session completed.",
        _ => $"{Timestamp(e)} {e.GetType().Name}",
    };

    private static string Timestamp(EngineEvent e) => e.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
}
