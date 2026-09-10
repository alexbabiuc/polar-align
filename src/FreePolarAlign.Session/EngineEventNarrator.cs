using System.Globalization;
using FreePolarAlign.Core.Engine;

namespace FreePolarAlign.Session;

/// <summary>How much attention a log line deserves.</summary>
public enum LogSeverity
{
    Info,

    /// <summary>Something the user should read but that has not stopped anything.</summary>
    Warning,

    /// <summary>The sequence stopped, or an answer was withheld.</summary>
    Error
}

/// <param name="Message">Plain text, safe to show and safe to write to a file.</param>
public sealed record NarratedEvent(LogSeverity Severity, string Message);

/// <summary>
/// Turns engine events into the sentences that appear on screen and in the log
/// file.
///
/// Shared between the two on purpose. The point of a log is to answer "what
/// actually happened" the morning after a session that went wrong, and a log
/// that says less than the screen did at the time is close to useless for that.
/// Deriving both from one function is the only way to keep them from drifting
/// apart.
///
/// Numbers are formatted with invariant culture: a log read on a different
/// machine than it was written on must not change meaning because a decimal
/// separator did.
/// </summary>
public static class EngineEventNarrator
{
    public static NarratedEvent Describe(EngineEvent engineEvent)
    {
        ArgumentNullException.ThrowIfNull(engineEvent);

        return engineEvent switch
        {
            DeviceConnectedEvent e => Info(DescribeConnection(e)),

            DeviceDisconnectedEvent e => e.Reason is null
                ? Info($"{Kind(e.Kind)} disconnected.")
                : new NarratedEvent(LogSeverity.Error, $"{Kind(e.Kind)}: {e.Reason}"),

            // The site is logged in full every time it is set, because it is the
            // one input the software cannot check and the first thing to
            // re-examine when a result looks wrong.
            SiteConfiguredEvent e => e.MountReportedDisagreement is null
                ? Info(Inv(
                    $"Site confirmed: latitude {e.LatitudeDegrees:F5}°, longitude {e.LongitudeDegrees:F5}°, ",
                    $"height {e.HeightMeters:F0} m."))
                : new NarratedEvent(LogSeverity.Warning, Inv(
                    $"Site confirmed: latitude {e.LatitudeDegrees:F5}°, longitude {e.LongitudeDegrees:F5}°, ",
                    $"height {e.HeightMeters:F0} m. {e.MountReportedDisagreement}")),

            EquipmentConfiguredEvent e => Info(DescribeEquipment(e)),

            // Deliberately not logged: mount status is polled continuously, and
            // recording every poll would bury everything else. The positions
            // that matter -- the ones a capture was taken at -- are logged with
            // the capture.
            MountStatusEvent => new NarratedEvent(LogSeverity.Info, string.Empty),

            SessionStartedEvent e => Info(Inv(
                $"Sequence started: {e.Configuration.CapturePoints} captures, ",
                $"{e.Configuration.RequestedSweepDegrees:F0}° sweep requested, ",
                $"{e.Configuration.ExposureDuration.TotalSeconds:F1} s exposures.")),

            TargetSelectedEvent e => Info(Inv(
                $"Target: mechanical declination {e.DeclinationDegrees:F2}°, ",
                $"{e.PlannedCaptures} captures across {e.SweepDegrees:F0}° ",
                $"{(e.IsWestOfMeridian ? "west" : "east")} of the meridian.")),

            SlewProposedEvent e => Info(Inv(
                $"Proposed point {e.PointIndex} of {e.PlannedCaptures}: ",
                $"RA {CoordinateText.FormatRightAscension(e.RaDegrees)}, ",
                $"Dec {CoordinateText.FormatDeclination(e.DecDegrees)} ",
                $"(hour angle {e.MechanicalRotationDegrees:F1}°, altitude {e.PredictedAltitudeDegrees:F0}°). ",
                $"{(e.RequiresMotion ? "Waiting for confirmation before moving." : "No movement needed.")}")),

            SlewConfirmedEvent e => e.ReanchoredReason is not null
                ? new NarratedEvent(LogSeverity.Warning, Inv(
                    $"Slew confirmed to RA {CoordinateText.FormatRightAscension(e.RaDegrees)}, ",
                    $"Dec {CoordinateText.FormatDeclination(e.DecDegrees)}. {e.ReanchoredReason}"))
                : Info(Inv(
                    $"Slew confirmed to RA {CoordinateText.FormatRightAscension(e.RaDegrees)}, ",
                    $"Dec {CoordinateText.FormatDeclination(e.DecDegrees)}",
                    $"{(e.WasOverridden ? " (coordinates overridden by the operator)." : ".")}")),

            PointCapturedEvent e => Info(Inv(
                $"Point {e.Point.Index} solved: RA {CoordinateText.FormatRightAscension(e.Point.RaDegrees)}, ",
                $"Dec {CoordinateText.FormatDeclination(e.Point.DecDegrees)}, ",
                $"exposure midpoint {e.Point.ExposureMidpointUtc:yyyy-MM-dd HH:mm:ss}Z.")),

            AlignmentUpdatedEvent e => Info(Inv(
                $"Estimate: total {e.Estimate.TotalErrorArcminutes:F2}' ± {e.Estimate.TotalSigmaArcminutes:F2}' ",
                $"(altitude {e.Estimate.AltitudeErrorArcminutes:+0.00;-0.00}' ± {e.Estimate.AltitudeSigmaArcminutes:F2}', ",
                $"azimuth {e.Estimate.AzimuthErrorArcminutes:+0.00;-0.00}' ± {e.Estimate.AzimuthSigmaArcminutes:F2}'), ",
                $"residual RMS {e.Estimate.ResidualRmsArcseconds:F2}\".")),

            AlignmentWithheldEvent e => new NarratedEvent(LogSeverity.Error, $"Result withheld: {e.Reason}"),

            ManualActionRequiredEvent e => Info($"Manual action required: {e.Instruction}"),

            CaptureFailedEvent e => new NarratedEvent(
                LogSeverity.Warning,
                $"Capture {e.CaptureIndex} failed: {e.Reason} ({(e.WillRetry ? "will retry" : "giving up")})."),

            CommandRejectedEvent e => new NarratedEvent(LogSeverity.Warning, $"Refused: {e.Reason}"),

            SessionFaultedEvent e => new NarratedEvent(LogSeverity.Error, $"Sequence faulted: {e.Reason}"),

            SessionCompletedEvent => Info("Sequence finished."),

            _ => Info(engineEvent.GetType().Name),
        };
    }

    private static string DescribeConnection(DeviceConnectedEvent e)
    {
        string basics = $"{Kind(e.Kind)} connected: {e.DisplayName} [{e.DriverInfo}]";

        if (e.Camera is { } camera)
        {
            return Inv(
                $"{basics}, {camera.SensorWidthPixels}×{camera.SensorHeightPixels} pixels of ",
                $"{camera.PixelSizeMicrons:F2} µm.");
        }

        return e.CanSlew
            ? $"{basics}."
            : $"{basics}. This mount reports no absolute-slew support, so the sequence has to be driven by hand (D9/D10).";
    }

    private static string DescribeEquipment(EquipmentConfiguredEvent e)
    {
        if (e.FocalLengthMillimetres is not { } focalLength)
        {
            return "Focal length cleared. Solves will be fully blind until one is set or measured.";
        }

        string provenance = e.IsFocalLengthSolved ? "measured from a solve" : "as entered";
        string scale = e.ScaleArcsecondsPerPixel is { } arcsec
            ? Inv($", {arcsec:F2}\"/pixel")
            : string.Empty;
        string field = e.FieldRadiusDegrees is { } radius
            ? Inv($", field radius {radius:F2}°")
            : string.Empty;

        return Inv($"Focal length {focalLength:F1} mm ({provenance}){scale}{field}.");
    }

    private static string Kind(DeviceKind kind) => kind == DeviceKind.Camera ? "Camera" : "Mount";

    private static NarratedEvent Info(string message) => new(LogSeverity.Info, message);

    /// <summary>
    /// Invariant interpolation. Named rather than overloaded on both
    /// <c>string</c> and <see cref="FormattableString"/>: an interpolated string
    /// binds to the <c>string</c> overload in preference, which would quietly
    /// undo the whole point of formatting invariantly.
    /// </summary>
    private static string Inv(params FormattableString[] parts) =>
        string.Concat(parts.Select(p => p.ToString(CultureInfo.InvariantCulture)));
}
