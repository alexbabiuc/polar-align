namespace FreePolarAlign.Core.Engine;

/// <summary>
/// Configuration for one alignment session. Immutable so it can cross the
/// engine boundary as part of a command without aliasing concerns (D6).
/// </summary>
/// <param name="MinimumCapturePoints">D7: never less than 3.</param>
public sealed record SessionConfiguration(
    double SiteLatitudeDegrees,
    double SiteLongitudeDegrees,
    double SiteHeightMeters,
    int MinimumCapturePoints,
    TimeSpan ExposureDuration);

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
/// Base type for everything sent into an <see cref="IAlignmentEngine"/>. Commands
/// are the only way callers (the UI, or a future remote client per D6) affect
/// engine state -- there is no other public mutator anywhere on the boundary.
/// </summary>
public abstract record EngineCommand;

public sealed record StartSessionCommand(SessionConfiguration Configuration) : EngineCommand;

/// <summary>Slew (if in automatic mode) or prompt (D10, manual mode) for the next capture point.</summary>
public sealed record CaptureNextPointCommand : EngineCommand;

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

public sealed record SessionStartedEvent(SessionConfiguration Configuration) : EngineEvent;

public sealed record PointCapturedEvent(CapturePoint Point) : EngineEvent;

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
