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
///
/// The scheduling events are turned into a due time from the event's own
/// timestamp rather than from a clock, so the reducer stays pure.
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

            CameraGainChangedEvent e => withLog with
            {
                CameraGain = e.Gain,
                RejectionReason = null,
                StatusMessage = $"Gain set to {e.Gain.Percent}% ({e.Gain.Value} in the camera's own units).",
            },

            CameraSetupDialogChangedEvent e => withLog with
            {
                CameraSetupDialogOpen = e.IsOpen,
                RejectionReason = null,
                StatusMessage = e.IsOpen
                    ? "The camera driver's settings window is open. Close it to carry on."
                    : "Camera driver settings closed.",
            },

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
                MountIsMoving = e.IsMoving,
            },

            // Every frame, sequence or not (D26), so for the reason above it
            // leaves the status line alone: at a 0.1 s exposure a status that
            // followed the frames would be unreadable and would wipe every
            // message worth reading.
            FrameCapturedEvent e => withLog with
            {
                LatestFramePath = e.FitsPath,
                LatestFrameMidpointUtc = e.ExposureMidpointUtc,
            },

            ExposureChangedEvent e => withLog with
            {
                EngineExposure = e.Duration,
            },

            // D26: the scheduling events feed one line of status and nothing
            // else. A failed solve in particular is not a warning banner:
            // while the mount is being turned most frames fail, and a banner
            // that is always up is a banner nobody reads.
            SolveScheduledEvent e => withLog with
            {
                Sampling = state.Sampling with
                {
                    Activity = SamplingActivity.Scheduled,
                    Trigger = e.Trigger,
                    SolveDueUtc = e.TimestampUtc + e.Delay,
                    Detail = null,
                },
            },

            SolveStartedEvent e => withLog with
            {
                Sampling = state.Sampling with
                {
                    Activity = SamplingActivity.Solving,
                    Trigger = e.Trigger,
                    SolveDueUtc = null,
                    Detail = null,
                },
            },

            SolveFailedEvent e => withLog with
            {
                Sampling = state.Sampling with
                {
                    Activity = SamplingActivity.Failed,
                    Trigger = e.Trigger,
                    SolveDueUtc = null,
                    Detail = e.Reason,
                    ConsecutiveFailures = e.ConsecutiveFailures,
                },
            },

            // The solve worked, so the run of failures is over, whatever
            // became of the position.
            SampleSkippedEvent e => withLog with
            {
                Sampling = new SamplingView(SamplingActivity.Skipped, Detail: e.Reason),
            },

            // The samples the estimate came from are gone, so the estimate goes
            // with them, exactly as for a withheld result: a number left on
            // screen after the engine has discarded its inputs is the stale
            // figure D11 exists to prevent.
            SequenceRestartedEvent => withLog with
            {
                CurrentEstimate = null,
                CapturedPointCount = 0,
                Proposal = null,
                Sampling = SamplingView.Idle,
                StatusMessage = "Sequence restarted and the samples so far discarded -- see the log for why.",
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
                SequenceMode = e.Mode,
                CurrentEstimate = null,
                WithheldReason = null,
                FaultReason = null,
                GuidanceInstruction = null,
                Sampling = SamplingView.Idle,
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
            // and nothing will until it is told to. Without a slew to confirm,
            // the telescope is either already in place or moved by hand, and
            // saying "without moving" to someone about to turn it would be wrong.
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
                GuidanceInstruction = e.Instruction,
                RejectionReason = null,
                StatusMessage = e.RequiresMotion
                    ? $"Waiting for you to confirm point {e.PointIndex} of {e.PlannedCaptures}. Nothing will move until you do."
                    : state.SequenceMode == SequenceMode.Driven
                        ? $"Point {e.PointIndex} of {e.PlannedCaptures} is sampled without moving, once the mount is still."
                        : $"Point {e.PointIndex} of {e.PlannedCaptures}: move the telescope yourself -- see the next step.",
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

            // The instruction that led here is spent; the engine follows with
            // the next one, or finishes.
            PointCapturedEvent e => withLog with
            {
                CapturedPointCount = e.Point.Index,
                GuidanceInstruction = null,
                Proposal = null,
                Sampling = new SamplingView(SamplingActivity.Sampled),
                StatusMessage = $"Sample {e.Point.Index} of {state.PlannedPointCount} taken.",
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

            // D10: the telescope is moved by hand, or a proposal cannot be
            // reached. Either way it is the next thing the user must read.
            ManualActionRequiredEvent e => withLog with
            {
                GuidanceInstruction = e.Instruction,
                StatusMessage = "Your move -- see the next step.",
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
                CurrentEstimate = null,
                GuidanceInstruction = null,
                Proposal = null,
                Sampling = SamplingView.Idle,
                FaultReason = e.Reason,
                StatusMessage = "Session faulted -- see reason below.",
            },

            // Normal completion. The roadmap is explicit that the actual
            // figure stays visible after success, so CurrentEstimate is left
            // exactly as the last AlignmentUpdatedEvent set it.
            SessionCompletedEvent => withLog with
            {
                SessionActive = false,
                GuidanceInstruction = null,
                Proposal = null,
                Sampling = SamplingView.Idle,
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
                CameraHasSetupDialog = e.Camera?.HasSetupDialog ?? false,
                CameraSettingsKey = Session.AppSettings.CameraKey(e.ProviderName, e.Camera?.UniqueId, e.DeviceId),
                CameraGain = e.Camera?.Gain,
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
                : $"Mount connected: {e.DisplayName}. It reports no slew support, so move it from its hand controller (D9/D10).",
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
                CameraHasSetupDialog = false,
                CameraSettingsKey = null,
                CameraGain = null,
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
            MountIsMoving = false,
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
