using FreePolarAlign.Core.Engine;
using FreePolarAlign.Devices;
using FreePolarAlign.Session;
using FreePolarAlign.Solving;
using Xunit;

namespace FreePolarAlign.Tests.EndToEnd;

/// <summary>
/// D45: Solve & sync tells the mount where it really points, between sequences
/// only, without moving it, and says which half failed when it does not.
/// </summary>
public class SolveAndSyncTests
{
    /// <summary>The solve and the mount's report, in arcseconds apart.</summary>
    private static async Task<double> ReportedVersusTruthArcsecondsAsync(SessionHarness harness)
    {
        harness.Perfect.Delay = TimeSpan.Zero;
        PlateSolveResult truth = await harness.Perfect.SolveAsync(new PlateSolveRequest("truth.fits"));
        MountPosition reported = await harness.Simulated.GetPositionAsync();
        return Separation(reported.RaDegrees, reported.DecDegrees,
            truth.Solution!.CenterRaDegrees, truth.Solution.CenterDecDegrees) * 3600.0;
    }

    private static async Task ReadyBetweenSequencesAsync(SessionHarness harness)
    {
        await harness.ConnectAsync();
        await harness.ConfirmSiteAsync();
    }

    [Fact]
    public async Task Sync_TheMountReportsWhereTheTelescopePoints_AndNothingMoves()
    {
        using var harness = new SessionHarness();
        await ReadyBetweenSequencesAsync(harness);

        // The injected misalignment and cone error put the mount's belief well
        // away from the truth, which is what makes the sync observable.
        Assert.True(await ReportedVersusTruthArcsecondsAsync(harness) > 600.0);

        await harness.Session.SendAsync(new SolveAndSyncCommand());
        MountSyncedEvent synced = await harness.Recorder.WaitForAsync<MountSyncedEvent>(poll: harness.PollAsync);

        Assert.Single(harness.Mount.Syncs);
        Assert.Empty(harness.Mount.Slews);
        Assert.True(synced.CorrectionArcminutes > 10.0, harness.Recorder.Trail());
        Assert.True(await ReportedVersusTruthArcsecondsAsync(harness) < 1.0, harness.Recorder.Trail());
    }

    /// <summary>
    /// Untracked, the sky turns past at 15″ a second while the frame is solved,
    /// so the solved position is stale by the time it is synced.
    /// </summary>
    [Fact]
    public async Task WithTrackingOff_IsRefused()
    {
        using var harness = new SessionHarness(tracking: false);
        await ReadyBetweenSequencesAsync(harness);

        await harness.Session.SendAsync(new SolveAndSyncCommand());

        CommandRejectedEvent? rejected = harness.Recorder.Last<CommandRejectedEvent>();
        Assert.True(rejected is not null, harness.Recorder.Trail());
        Assert.Contains("not tracking", rejected!.Reason);
        Assert.Null(harness.Recorder.Last<MountSyncStartedEvent>());
    }

    /// <summary>Stopped mid-solve, the sync ends at the next poll rather than when the solve returns.</summary>
    [Fact]
    public async Task TrackingStoppingDuringTheSolve_AbortsTheSync()
    {
        using var harness = new SessionHarness();
        await ReadyBetweenSequencesAsync(harness);
        harness.Perfect.Delay = TimeSpan.FromSeconds(10);

        await harness.Session.SendAsync(new SolveAndSyncCommand());
        await harness.Clock.WaitUntilAsync(() => harness.Perfect.Calls > 0, () => " The solve never started.");
        await harness.Simulated.HaltAsync();
        await harness.PollAsync();

        MountSyncFailedEvent? failed = harness.Recorder.Last<MountSyncFailedEvent>();
        Assert.True(failed is not null, harness.Recorder.Trail());
        Assert.Contains("stopped tracking", failed!.Reason);

        await harness.Clock.PassAsync(TimeSpan.FromSeconds(12), harness.PollAsync);
        Assert.Empty(harness.Mount.Syncs);
        Assert.Null(harness.Recorder.Last<MountSyncedEvent>());
    }

    [Fact]
    public async Task AFrameThatWillNotSolve_LeavesTheMountAlone()
    {
        using var harness = new SessionHarness(solver: new HopelessSolver());
        await ReadyBetweenSequencesAsync(harness);

        await harness.Session.SendAsync(new SolveAndSyncCommand());
        MountSyncFailedEvent failed = await harness.Recorder.WaitForAsync<MountSyncFailedEvent>(poll: harness.PollAsync);

        Assert.Equal(MountSyncStage.Solve, failed.Stage);
        Assert.Contains("No stars", failed.Reason);
        Assert.Empty(harness.Mount.Syncs);
    }

    [Fact]
    public async Task ADriverThatRefusesTheSync_IsReportedAsTheSyncFailing()
    {
        using var harness = new SessionHarness(canSync: false);
        await ReadyBetweenSequencesAsync(harness);

        await harness.Session.SendAsync(new SolveAndSyncCommand());
        MountSyncFailedEvent failed = await harness.Recorder.WaitForAsync<MountSyncFailedEvent>(poll: harness.PollAsync);

        Assert.Equal(MountSyncStage.Sync, failed.Stage);
        Assert.Contains("not implemented", failed.Reason);
        Assert.Null(harness.Recorder.Last<MountSyncedEvent>());
    }

    /// <summary>
    /// A position solved before the mount was moved is not where it points
    /// afterwards, so the sync ends at the poll that sees the move.
    /// </summary>
    [Fact]
    public async Task TheMountMovingDuringTheSolve_IsNotSynced()
    {
        using var harness = new SessionHarness();
        await ReadyBetweenSequencesAsync(harness);
        harness.Perfect.Delay = TimeSpan.FromSeconds(10);

        await harness.Session.SendAsync(new SolveAndSyncCommand());
        await harness.Clock.WaitUntilAsync(() => harness.Perfect.Calls > 0, () => " The solve never started.");
        await harness.MoveFromOutsideAsync(harness.MountRotationNow() + 20.0);
        await harness.PollAsync();

        MountSyncFailedEvent? failed = harness.Recorder.Last<MountSyncFailedEvent>();
        Assert.True(failed is not null, harness.Recorder.Trail());
        Assert.Equal(MountSyncStage.Sync, failed!.Stage);
        Assert.Contains("moved", failed.Reason);

        await harness.Clock.PassAsync(TimeSpan.FromSeconds(12), harness.PollAsync);
        Assert.Empty(harness.Mount.Syncs);
    }

    /// <summary>A sync changes the coordinates a sequence's samples are read against.</summary>
    [Fact]
    public async Task DuringASequence_IsRefused()
    {
        using var harness = new SessionHarness();
        await harness.ReadyAsync();

        await harness.Session.SendAsync(new SolveAndSyncCommand());

        CommandRejectedEvent? rejected = harness.Recorder.Last<CommandRejectedEvent>();
        Assert.True(rejected is not null, harness.Recorder.Trail());
        Assert.Contains("between sequences", rejected!.Reason);
        Assert.Null(harness.Recorder.Last<MountSyncStartedEvent>());
    }

    [Fact]
    public async Task WithoutAMount_IsRefused()
    {
        using var harness = new SessionHarness(withMount: false);
        await harness.ConnectAsync(mount: false);

        await harness.Session.SendAsync(new SolveAndSyncCommand());

        CommandRejectedEvent? rejected = harness.Recorder.Last<CommandRejectedEvent>();
        Assert.True(rejected is not null, harness.Recorder.Trail());
        Assert.Contains("mount", rejected!.Reason);
    }

    /// <summary>
    /// The user chose the sequence over the sync. The sync is dropped, quietly:
    /// a cancellation, not a failure, and the mount is never synced under the
    /// sequence's samples.
    /// </summary>
    [Fact]
    public async Task StartingASequenceWhileSyncing_CancelsTheSyncQuietly()
    {
        using var harness = new SessionHarness();
        await ReadyBetweenSequencesAsync(harness);
        harness.Perfect.Delay = TimeSpan.FromSeconds(10);

        await harness.Session.SendAsync(new SolveAndSyncCommand());
        await harness.Clock.WaitUntilAsync(() => harness.Perfect.Calls > 0, () => " The solve never started.");
        await harness.StartAsync();

        Assert.True(harness.Recorder.Last<SessionStartedEvent>() is not null, harness.Recorder.Trail());
        Assert.True(harness.Recorder.IndexOfLast<MountSyncCancelledEvent>() <
                    harness.Recorder.IndexOfLast<SessionStartedEvent>(), harness.Recorder.Trail());

        await harness.Clock.PassAsync(TimeSpan.FromSeconds(12), harness.PollAsync);
        Assert.Empty(harness.Mount.Syncs);
        Assert.Null(harness.Recorder.Last<MountSyncFailedEvent>());
        Assert.Null(harness.Recorder.Last<MountSyncedEvent>());
    }

    /// <summary>A start that is refused replaces nothing, so the sync carries on.</summary>
    [Fact]
    public async Task ARefusedStart_LeavesTheSyncRunning()
    {
        using var harness = new SessionHarness();
        await harness.ConnectAsync();

        await harness.Session.SendAsync(new SolveAndSyncCommand());
        await harness.StartAsync();

        Assert.Contains("site", harness.Recorder.Last<CommandRejectedEvent>()!.Reason);
        Assert.Null(harness.Recorder.Last<MountSyncCancelledEvent>());
        await harness.Recorder.WaitForAsync<MountSyncedEvent>(poll: harness.PollAsync);
    }

    private static double Separation(double ra1, double dec1, double ra2, double dec2)
    {
        const double toRadians = Math.PI / 180.0;
        double cos = Math.Sin(dec1 * toRadians) * Math.Sin(dec2 * toRadians) +
                     Math.Cos(dec1 * toRadians) * Math.Cos(dec2 * toRadians) * Math.Cos((ra1 - ra2) * toRadians);
        return Math.Acos(Math.Clamp(cos, -1.0, 1.0)) / toRadians;
    }
}
