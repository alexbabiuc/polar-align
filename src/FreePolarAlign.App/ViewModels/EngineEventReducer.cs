using FreePolarAlign.Core.Engine;
using FreePolarAlign.Session;

namespace FreePolarAlign.App.ViewModels;

/// <summary>
/// The pure translation from (previous <see cref="UiState"/>, next <see cref="EngineEvent"/>)
/// to the next <see cref="UiState"/>. This is deliberately the entire
/// "understand the engine's event stream" logic for the app, kept free of
/// Avalonia, threading and I/O so it can be unit-tested directly -- which is
/// where the safety properties this project cares about (D11: never show a
/// stale number as current; D18: never suggest the mount is about to move when
/// it is not) actually live and get verified.
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

        UiState withLog = AppendLog(state, engineEvent);

        return engineEvent switch
        {
            DeviceConnectedEvent e => ApplyConnected(withLog, e),

            DeviceDisconnectedEvent e => ApplyDisconnected(withLog, e),

            SiteConfiguredEvent e => withLog with
            {
                SiteLatitudeDegrees = e.LatitudeDegrees,
                SiteLongitudeDegrees = e.LongitudeDegrees,
                SiteHeightMeters = e.HeightMeters,
                SiteDisagreement = e.MountReportedDisagreement,
                RejectionReason = null,
                StatusMessage = e.MountReportedDisagreement is null
                    ? "Site confirmed."
                    : "Site confirmed, but the mount disagrees -- see the note beside it.",
            },

            EquipmentConfiguredEvent e => withLog with
            {
                FocalLengthMillimetres = e.FocalLengthMillimetres,
                IsFocalLengthSolved = e.IsFocalLengthSolved,
                ScaleArcsecondsPerPixel = e.ScaleArcsecondsPerPixel,
                FieldRadiusDegrees = e.FieldRadiusDegrees,
                RejectionReason = null,
            },

            // Polled continuously, so it deliberately leaves the status line and
            // every warning alone: a status refresh is not news, and letting it
            // clear a message would make warnings vanish a second after
            // appearing.
            MountStatusEvent e => withLog with
            {
                MountRaDegrees = e.RaDegrees,
                MountDecDegrees = e.DecDegrees,
                MountTracking = e.Tracking,
                MountPierSide = e.PierSide,
            },

            // The frame arrives before any attempt to solve it, and is left in
            // place afterwards whatever the solve did: a failed solve is the
            // moment the image matters most.
            FrameCapturedEvent e => withLog with
            {
                LatestFramePath = e.FitsPath,
                LatestFrameIndex = e.PointIndex,
                StatusMessage = $"Frame {e.PointIndex} captured; solving.",
            },

            ReadoutModeChangedEvent e => withLog with
            {
                ReadoutModeIndex = e.Index,
                RejectionReason = null,
                StatusMessage = e.BitDepth is { } bits
                    ? $"Readout mode: {e.Name} ({bits}-bit)."
                    : $"Readout mode: {e.Name}.",
            },

            SessionStartedEvent e => withLog with
            {
                SessionActive = true,
                AwaitingManualAction = false,
                CurrentEstimate = null,
                WithheldReason = null,
                FaultReason = null,
                ManualInstruction = null,
                CaptureWarning = null,
                RejectionReason = null,
                Proposal = null,
                CapturedPointCount = 0,
                PlannedPointCount = 0,
                PlannedSweepDegrees = null,
                RequestedSweepDegrees = e.Configuration.RequestedSweepDegrees,
                StatusMessage = "Sequence started.",
            },

            TargetSelectedEvent e => withLog with
            {
                PlannedPointCount = e.PlannedCaptures,
                PlannedSweepDegrees = e.SweepDegrees,
                StatusMessage =
                    $"Target selected: {e.PlannedCaptures} captures at mechanical declination " +
                    $"{e.DeclinationDegrees:F1}°, sweeping {e.SweepDegrees:F0}° " +
                    $"{(e.IsWestOfMeridian ? "west" : "east")} of the meridian (D8).",
            },

            // D18: this is the engine saying "here is what I would do next" and
            // then waiting. The UI's job is to make clear that nothing has moved
            // and nothing will until it is told to.
            SlewProposedEvent e => withLog with
            {
                Proposal = new ProposalView(
                    e.PointIndex,
                    e.PlannedCaptures,
                    e.RaDegrees,
                    e.DecDegrees,
                    e.MechanicalRotationDegrees,
                    e.PredictedAltitudeDegrees,
                    e.RequiresMotion,
                    e.Instruction),
                RejectionReason = null,
                StatusMessage = e.RequiresMotion
                    ? $"Waiting for you to confirm point {e.PointIndex} of {e.PlannedCaptures}. Nothing will move until you do."
                    : $"Ready to capture point {e.PointIndex} of {e.PlannedCaptures} without moving.",
            },

            SlewConfirmedEvent e => withLog with
            {
                // A re-anchor threw away the earlier captures, so anything
                // computed from them is gone too. The AlignmentWithheldEvent
                // that accompanies it clears the estimate; this clears the
                // count, so the two cannot disagree on screen.
                CapturedPointCount = e.ReanchoredReason is null ? state.CapturedPointCount : 0,
                RejectionReason = null,
                StatusMessage = e.ReanchoredReason is null
                    ? (e.WasOverridden ? "Slewing to your coordinates." : "Slewing to the proposed coordinates.")
                    : "Sequence re-anchored -- see the note below.",
            },

            PointCapturedEvent e => withLog with
            {
                CapturedPointCount = e.Point.Index,
                AwaitingManualAction = false,
                ManualInstruction = null,
                CaptureWarning = null,
                Proposal = null,
                StatusMessage = $"Captured and solved point {e.Point.Index} of {state.PlannedPointCount}.",
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
            // capture affordance then takes the frame where it stands.
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

            // A refusal, not a fault: nothing broke and nothing was torn down,
            // so no estimate is stale and none is cleared.
            CommandRejectedEvent e => withLog with
            {
                RejectionReason = e.Reason,
                StatusMessage = "That is not possible yet -- see the note below.",
            },

            SessionFaultedEvent e => withLog with
            {
                SessionActive = false,
                AwaitingManualAction = false,
                CurrentEstimate = null,
                ManualInstruction = null,
                Proposal = null,
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
                Proposal = null,
                StatusMessage = "Session complete.",
            },

            _ => withLog,
        };
    }

    private static UiState ApplyConnected(UiState state, DeviceConnectedEvent e)
    {
        var view = new DeviceView(IsConnected: true, e.DisplayName, e.DriverInfo);

        if (e.Kind == DeviceKind.Camera)
        {
            return state with
            {
                Camera = view,
                CameraPixelSizeMicrons = e.Camera?.PixelSizeMicrons,
                CameraWidthPixels = e.Camera?.SensorWidthPixels ?? 0,
                CameraHeightPixels = e.Camera?.SensorHeightPixels ?? 0,
                ReadoutModes = e.Camera?.ReadoutModes ?? Array.Empty<CameraReadoutModeDescription>(),
                ReadoutModeIndex = e.Camera?.ReadoutModeIndex,
                RejectionReason = null,
                StatusMessage = $"Camera connected: {e.DisplayName}.",
            };
        }

        return state with
        {
            Mount = view,
            MountCanSlew = e.CanSlew,
            RejectionReason = null,
            StatusMessage = e.CanSlew
                ? $"Mount connected: {e.DisplayName}."
                : $"Mount connected: {e.DisplayName}. It reports no slew support, so use manual mode (D9/D10).",
        };
    }

    private static UiState ApplyDisconnected(UiState state, DeviceDisconnectedEvent e)
    {
        var view = new DeviceView(IsConnected: false, Problem: e.Reason);

        if (e.Kind == DeviceKind.Camera)
        {
            return state with
            {
                Camera = view,
                // Cleared with the camera: a pixel size left over from a device
                // that is no longer attached would make every plate scale shown
                // afterwards a fiction.
                CameraPixelSizeMicrons = null,
                CameraWidthPixels = 0,
                CameraHeightPixels = 0,
                ReadoutModes = Array.Empty<CameraReadoutModeDescription>(),
                ReadoutModeIndex = null,
                StatusMessage = e.Reason ?? "Camera disconnected.",
            };
        }

        return state with
        {
            Mount = view,
            MountCanSlew = false,
            MountRaDegrees = null,
            MountDecDegrees = null,
            MountTracking = MountTrackingState.Unknown,
            MountPierSide = MeridianSide.Unknown,
            StatusMessage = e.Reason ?? "Mount disconnected.",
        };
    }

    private static UiState AppendLog(UiState state, EngineEvent engineEvent)
    {
        NarratedEvent narrated = EngineEventNarrator.Describe(engineEvent);
        if (string.IsNullOrEmpty(narrated.Message))
        {
            // The narrator returns an empty message for events not worth a line
            // -- currently the continuous mount-status poll.
            return state;
        }

        string prefix = narrated.Severity switch
        {
            LogSeverity.Warning => "WARN ",
            LogSeverity.Error => "ERROR",
            _ => "     ",
        };

        var log = new List<string>(state.Log.Count + 1);
        log.AddRange(state.Log);
        log.Add($"{engineEvent.TimestampUtc.ToLocalTime():HH:mm:ss} {prefix} {narrated.Message}");

        if (log.Count > MaxLogEntries)
        {
            log.RemoveRange(0, log.Count - MaxLogEntries);
        }

        return state with { Log = log };
    }
}
