using FreePolarAlign.Core.Engine;
using FreePolarAlign.Devices;
using FreePolarAlign.Solving;

namespace FreePolarAlign.Session;

/// <summary>
/// Solve and sync (D45): the next frame is solved, and the mount told that is
/// where it points. Kept apart from the sequence because it is not part of one:
/// it runs only between sequences, and it changes the coordinates the mount
/// reports, which every sample a sequence takes is read against.
///
/// Only on a tracking mount that is standing still, from the command to the
/// sync. Tracking is what keeps the telescope on the solved position for as
/// long as the solve takes; anything else means the position solved is no
/// longer where it points by the time it is synced.
/// </summary>
public sealed partial class AlignmentSession
{
    /// <summary>The solve-and-sync under way, or null. At most one at a time.</summary>
    private PendingSync? _sync;

    private sealed class PendingSync(DateTime notBeforeUtc)
    {
        /// <summary>The first frame to start at or after this is the one solved.</summary>
        public DateTime NotBeforeUtc { get; } = notBeforeUtc;

        public bool Solving { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();
    }

    private async Task BeginSyncAsync(CancellationToken cancellationToken)
    {
        string? refusal =
            _sessionActive
                ? "Solve & sync is for between sequences: a sync changes the coordinates the mount reports, and " +
                  "the samples taken so far were read against the old ones. Stop the sequence first."
            : _camera is not { IsConnected: true } ? "Connect a camera first: there is no frame to solve."
            : !MountConnected ? "Connect a mount first: there is nothing to sync."
            : _sync is not null ? "A solve and sync is already under way."
            : null;

        if (refusal is null)
        {
            // Fresh, rather than the last poll's: the drive may have been
            // switched off or a slew started since.
            await RefreshMountStatusAsync(cancellationToken).ConfigureAwait(false);
            refusal = NotSyncable();
        }

        if (refusal is not null)
        {
            _events.Publish(new CommandRejectedEvent(refusal));
            return;
        }

        // Not this frame, which may have started before the command: the next one.
        _sync = new PendingSync(UtcNow);
        _events.Publish(new MountSyncStartedEvent());
    }

    /// <summary>Why the mount cannot be synced as it stands, or null when it can.</summary>
    private string? NotSyncable() => _latestMountPosition switch
    {
        null => "The mount's position could not be read.",
        _ when _mountMoving => "The mount is moving. Sync once it has stopped and is tracking.",
        { Tracking: TrackingState.Stopped } => "The mount is not tracking. Turn tracking on, then sync.",
        { Tracking: TrackingState.Unknown } =>
            "The mount does not say whether it is tracking, and a sync needs it to be: the telescope must stay " +
            "on the solved position while the frame is solved.",
        _ => null,
    };

    /// <summary>
    /// Called with every status poll while a sync is pending. A mount that stops
    /// tracking or moves has left the position being solved, so the sync ends
    /// there rather than waiting for a solve that can only be stale.
    /// </summary>
    private void CheckSyncStillValid(MountPosition position, bool moving)
    {
        if (_sync is null)
        {
            return;
        }

        if (moving)
        {
            AbortSync("The mount moved, so the frame being solved is no longer where it points. Nothing was synced.");
        }
        else if (position.Tracking != TrackingState.Tracking)
        {
            AbortSync("The mount stopped tracking, so the frame being solved is no longer where it points. " +
                      "Nothing was synced.");
        }
    }

    /// <summary>Starts the sync's solve on the first frame to start after the request.</summary>
    private void OfferFrameToSync(CapturedImage frame, DateTime startedUtc)
    {
        if (_sync is not { Solving: false } sync || startedUtc < sync.NotBeforeUtc)
        {
            return;
        }

        sync.Solving = true;
        IDisposable hold = _frameStore.Hold(frame.FitsPath);
        PlateSolveRequest request = BuildSolveRequest(frame, _latestMountPosition);
        CancellationToken token = sync.Cancellation.Token;

        _ = Task.Run(async () =>
        {
            PlateSolveResult result;
            try
            {
                result = await _solver.SolveAsync(request, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = PlateSolveResult.Failed(PlateSolveFailureReason.Cancelled, "The sync was cancelled.");
            }
            catch (Exception ex)
            {
                result = PlateSolveResult.Failed(PlateSolveFailureReason.SolverError, ex.Message);
            }

            bool ran = await RunSerialAsync(
                () => OnSyncSolvedAsync(sync, frame, result, hold), CancellationToken.None)
                .ConfigureAwait(false);

            if (!ran)
            {
                hold.Dispose();
            }
        });
    }

    private async Task OnSyncSolvedAsync(PendingSync sync, CapturedImage frame, PlateSolveResult result, IDisposable hold)
    {
        try
        {
            // Already reported: aborted while the frame was out.
            if (!ReferenceEquals(_sync, sync))
            {
                return;
            }

            _sync = null;
            sync.Cancellation.Dispose();

            if (!result.Success)
            {
                string? kept = _frameStore.KeepFailed(frame.FitsPath, UtcNow);
                _events.Publish(new MountSyncFailedEvent(
                    MountSyncStage.Solve,
                    result.Message ?? $"The frame did not solve ({result.FailureReason}).",
                    kept,
                    SolverOf(result),
                    result.Detail));
                return;
            }

            PlateSolveSolution solution = result.Solution!;
            LearnFocalLength(solution, frame.Binning);
            await SyncToSolutionAsync(solution, SolverOf(result)).ConfigureAwait(false);
        }
        finally
        {
            hold.Dispose();
        }
    }

    private async Task SyncToSolutionAsync(PlateSolveSolution solution, string solver)
    {
        void Fail(string reason) =>
            _events.Publish(new MountSyncFailedEvent(MountSyncStage.Sync, reason, Solver: solver));

        if (!MountConnected)
        {
            Fail("The mount was disconnected while the frame was being solved.");
            return;
        }

        // The polls between the frame and now would have aborted the sync, but
        // the last of them may be two seconds old.
        MountPosition before;
        try
        {
            before = await _mount!.GetPositionAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail($"The mount could not be read before syncing: {ex.Message}");
            return;
        }

        if (before.IsSlewing ||
            (_latestMountPosition is { } last && HasMoved(last, before)))
        {
            Fail("The mount moved while the frame was being solved, so the solved position is no longer where it " +
                 "points. Nothing was synced.");
            return;
        }

        if (before.Tracking != TrackingState.Tracking)
        {
            Fail("The mount stopped tracking while the frame was being solved, so the solved position is no longer " +
                 "where it points. Nothing was synced.");
            return;
        }

        double ra = solution.CenterRaDegrees;
        double dec = solution.CenterDecDegrees;

        try
        {
            await _mount.SyncToCoordinatesAsync(ra, dec).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail($"The mount did not accept the sync: {ex.Message}");
            return;
        }

        // Read back, as for a site (D35): a driver may take the call and keep
        // something else, and only what it reports afterwards is worth saying.
        MountPosition after;
        try
        {
            after = await _mount.GetPositionAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail($"The sync was sent, but the mount could not be read back, so whether it took it is unknown: {ex.Message}");
            return;
        }

        // The report jumped because it was told to, not because anything
        // moved: taken as the reference the next poll is compared against.
        _latestMountPosition = after;
        _mountMoving = false;
        _events.Publish(new MountStatusEvent(
            after.RaDegrees, after.DecDegrees, MapTracking(after.Tracking), MapPierSide(after.PierSide)));

        double missArcminutes = SeparationDegrees(after.RaDegrees, after.DecDegrees, ra, dec) * 60.0;
        if (missArcminutes > StillToleranceArcminutes)
        {
            Fail($"The mount accepted the sync but reports {CoordinateText.FormatRightAscension(after.RaDegrees)}, " +
                 $"{CoordinateText.FormatDegrees(after.DecDegrees)}, {missArcminutes:F1}' from the solved position, " +
                 "so it did not take it. Some drivers refuse a sync too far from where they believe they point.");
            return;
        }

        double correction = SeparationDegrees(before.RaDegrees, before.DecDegrees, ra, dec) * 60.0;
        _events.Publish(new MountSyncedEvent(ra, dec, correction, solver));
    }

    /// <summary>Ends a pending sync, cancelling its solve if one is running.</summary>
    private void AbortSync(string reason, MountSyncStage stage = MountSyncStage.Sync)
    {
        if (_sync is not { } sync)
        {
            return;
        }

        _sync = null;
        sync.Cancellation.Cancel();
        _events.Publish(new MountSyncFailedEvent(stage, reason));
    }

    /// <summary>
    /// A sequence is starting, which the user chose over the sync. Said in the
    /// log, not as a failure: nothing went wrong.
    /// </summary>
    private void CancelSyncForSequence()
    {
        if (_sync is not { } sync)
        {
            return;
        }

        _sync = null;
        sync.Cancellation.Cancel();
        _events.Publish(new MountSyncCancelledEvent("A sequence was started."));
    }

    /// <summary>
    /// The mount, or the camera that was to supply the frame, has gone. A
    /// solve already under way on a frame the camera handed over is left to
    /// finish, since it no longer needs the camera.
    /// </summary>
    private void AbandonSync(DeviceKind lost)
    {
        if (lost == DeviceKind.Camera)
        {
            if (_sync is { Solving: false })
            {
                AbortSync("The camera was disconnected before a frame could be solved.", MountSyncStage.Solve);
            }

            return;
        }

        AbortSync("The mount was disconnected, so nothing was synced.");
    }
}
