using FreePolarAlign.Core.Alignment;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Session;
using Xunit;

namespace FreePolarAlign.Tests.EndToEnd;

/// <summary>
/// D26 and D27: the camera runs continuously, samples are triggered by events
/// with one solve at a time, and a sample has to widen the sweep. Run against
/// the real mount mechanics with a noiseless solver; see
/// <see cref="SessionHarness"/>.
/// </summary>
public class LiveSamplingTests
{
    // ---- The camera runs; nothing is solved outside a sequence ----

    /// <summary>
    /// Focusing and choosing an exposure is what the live frame is for, and
    /// there is nothing to compute while it happens (D26).
    /// </summary>
    [Fact]
    public async Task WithoutASequence_FramesArriveAndNothingIsSolved()
    {
        using var harness = new SessionHarness();
        await harness.ConnectAsync();

        for (int i = 0; i < 4; i++)
        {
            await harness.Recorder.WaitForAsync<FrameCapturedEvent>(fromIndex: harness.Recorder.Count);
        }

        Assert.Equal(0, harness.Perfect.Calls);
    }

    /// <summary>
    /// The exposure changes from the next frame, sequence or not (D22 as
    /// revised), and the frame in progress is left to finish.
    /// </summary>
    [Fact]
    public async Task AnExposureChangeMidSequence_AppliesFromTheNextFrame()
    {
        using var harness = new SessionHarness();
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);

        await harness.Session.SendAsync(new SetExposureCommand(TimeSpan.FromMilliseconds(35)));
        DateTime changedAt = DateTime.UtcNow;

        Assert.Null(harness.Recorder.Last<CommandRejectedEvent>());
        Assert.Equal(TimeSpan.FromMilliseconds(35), harness.Recorder.Last<ExposureChangedEvent>()!.Duration);

        await harness.Recorder.WaitForAsync<FrameCapturedEvent>(e => e.Duration == TimeSpan.FromMilliseconds(35));
        Assert.All(
            harness.Camera.Frames.Where(f => f.StartUtc > changedAt),
            f => Assert.Equal(TimeSpan.FromMilliseconds(35), f.Duration));
        Assert.True(harness.Recorder.Last<SessionStartedEvent>() is not null && harness.Recorder.Last<SessionFaultedEvent>() is null);
    }

    // ---- Triggers (D26) ----

    /// <summary>
    /// A slew made from outside the engine -- a hand controller, another program
    /// -- is sampled when it ends, and the engine commands nothing to make that
    /// happen.
    /// </summary>
    [Fact]
    public async Task ASlewFromOutside_IsSampledWhenItEnds()
    {
        using var harness = new SessionHarness();
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);

        await harness.MoveFromOutsideAsync(harness.MountRotationNow() + 15.0);
        await harness.WaitForSampleAsync(2);

        Assert.Empty(harness.Mount.Slews);
        Assert.Contains(harness.Recorder.All<SolveScheduledEvent>(), e => e.Trigger == SampleTrigger.SlewEnded);
    }

    /// <summary>
    /// D27: a move that does not widen the sweep by the planned spacing is not a
    /// sample. It is said, so a user can see how much further to go.
    /// </summary>
    [Fact]
    public async Task AMoveShorterThanTheSpacing_IsSkippedAndSaysHowFar()
    {
        using var harness = new SessionHarness();
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);
        int callsAfterFirst = harness.Perfect.Calls;

        await harness.MoveFromOutsideAsync(harness.MountRotationNow() + 5.0);

        SampleSkippedEvent skipped = await harness.Recorder.WaitForAsync<SampleSkippedEvent>(poll: harness.PollAsync);
        Assert.Contains("15", skipped.Reason, StringComparison.Ordinal);

        await Task.Delay(200);
        Assert.Single(harness.Recorder.All<PointCapturedEvent>());
        Assert.Equal(callsAfterFirst, harness.Perfect.Calls);
    }

    /// <summary>
    /// Motion before the settle delay has run out cancels the solve that was
    /// counting down, because the frame it would have used is of a moving sky.
    /// </summary>
    [Fact]
    public async Task MotionDuringTheSettleDelay_CancelsThePendingSolve()
    {
        using var harness = new SessionHarness(settleDelay: TimeSpan.FromMilliseconds(400));
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);
        double rotation = harness.MountRotationNow();

        await harness.MoveFromOutsideAsync(rotation + 15.0);
        int from = harness.Recorder.Count;
        await harness.Recorder.WaitForAsync<SolveScheduledEvent>(fromIndex: from, poll: harness.PollAsync);
        int calls = harness.Perfect.Calls;

        // Moving again inside the 400 ms window.
        await harness.MoveFromOutsideAsync(rotation + 30.0);
        await harness.PollAsync();
        Assert.True(harness.Recorder.Last<MountStatusEvent>()!.IsMoving);

        await Task.Delay(500);
        Assert.Equal(calls, harness.Perfect.Calls);

        // And once it stops, the new position is sampled instead.
        await harness.WaitForSampleAsync(2);
        Assert.Equal(rotation + 30.0, harness.MountRotationNow(), tolerance: 0.1);
    }

    /// <summary>
    /// Never two solves at once (D26): a slow solver, and every trigger the user
    /// and the mount can produce while it runs.
    /// </summary>
    [Fact]
    public async Task TriggersWhileASolveRuns_NeverStartASecondOne()
    {
        using var harness = new SessionHarness();
        harness.Perfect.Delay = TimeSpan.FromMilliseconds(150);
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);
        double rotation = harness.MountRotationNow();

        for (int i = 0; i < 5; i++)
        {
            await harness.Session.SendAsync(new RecordSampleCommand());
            await harness.MoveFromOutsideAsync(rotation + 11.0 * (i + 1));
            await harness.PollAsync();
            await Task.Delay(40);
            await harness.PollAsync();
        }

        await Task.Delay(600);
        Assert.Equal(1, harness.Perfect.MostAtOnce);
    }

    /// <summary>
    /// A forced sample skips the spacing rule (D27) -- the user can see the sky
    /// and the engine cannot -- but is taken from a frame that *starts* after
    /// the press, never the one already on screen (D26).
    /// </summary>
    [Fact]
    public async Task RecordingASample_IgnoresTheSpacingButUsesAFreshFrame()
    {
        using var harness = new SessionHarness();
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);

        DateTime pressed = DateTime.UtcNow;
        await harness.Session.SendAsync(new RecordSampleCommand());

        PointCapturedEvent second = await harness.WaitForSampleAsync(2, poll: false);
        Assert.Equal(SampleTrigger.Forced, second.Trigger);

        string solvedPath = harness.Recorder.Last<SolveStartedEvent>()!.FitsPath;
        var frame = harness.Camera.Frames.Single(f => f.Path == solvedPath);
        Assert.True(frame.StartUtc >= pressed, "the forced sample used a frame exposed before the press");
    }

    /// <summary>
    /// D18 as revised: declination moved on a connected mount restarts the
    /// sequence, automatically and with the reason, because the samples already
    /// taken lie on another circle.
    /// </summary>
    [Fact]
    public async Task ADeclinationNudge_RestartsTheSequenceAndSaysWhy()
    {
        using var harness = new SessionHarness();
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);
        await harness.Session.SendAsync(new RecordSampleCommand());
        await harness.WaitForSampleAsync(2, poll: false);

        harness.Simulated.NudgeDeclination(3.0);

        SequenceRestartedEvent restarted = await harness.Recorder.WaitForAsync<SequenceRestartedEvent>(poll: harness.PollAsync);
        Assert.Contains("declination", restarted.Reason, StringComparison.OrdinalIgnoreCase);

        // Counting from one again, at the new declination.
        await harness.WaitForSampleAsync(1);
        Assert.Null(harness.Recorder.Last<SessionFaultedEvent>());
    }

    /// <summary>
    /// Below the threshold nothing restarts: a mount's report is not exact, and a
    /// restart every few polls would make the sequence impossible to finish.
    /// Anything real and smaller is D11's to catch.
    /// </summary>
    [Fact]
    public async Task ADeclinationWobbleUnderAnArcminute_DoesNotRestart()
    {
        using var harness = new SessionHarness();
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);

        harness.Simulated.NudgeDeclination(0.5);
        for (int i = 0; i < 5; i++)
        {
            await harness.PollAsync();
            await Task.Delay(20);
        }

        Assert.Null(harness.Recorder.Last<SequenceRestartedEvent>());
    }

    /// <summary>
    /// A failed post-slew solve is retried while the mount stays where it is --
    /// a passing cloud is the usual reason -- and never faults the sequence.
    /// </summary>
    [Fact]
    public async Task FailedSolves_AreRetriedWithoutGivingUp()
    {
        var hopeless = new HopelessSolver();
        using var harness = new SessionHarness(solver: hopeless);
        await harness.ReadyAsync();

        await harness.Recorder.WaitForAsync<SolveFailedEvent>(e => e.ConsecutiveFailures >= 5);

        Assert.Null(harness.Recorder.Last<SessionFaultedEvent>());
        Assert.Contains(harness.Recorder.All<SolveScheduledEvent>(), e => e.Trigger == SampleTrigger.Retry);
    }

    // ---- Unconnected (D10 as revised) ----

    /// <summary>
    /// With no mount, starting is accepted, the user is told the declination to
    /// set, and nothing is commanded.
    /// </summary>
    [Fact]
    public async Task WithoutAMount_TheSequenceStartsAndSaysWhereToSetDeclination()
    {
        using var harness = new SessionHarness(withMount: false);
        await harness.ReadyAsync(mount: false);

        Assert.Equal(SequenceMode.Unconnected, harness.Recorder.Last<SessionStartedEvent>()!.Mode);

        ManualActionRequiredEvent instruction = harness.Recorder.Last<ManualActionRequiredEvent>()!;
        Assert.Contains("declination", instruction.Instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("east", instruction.Instruction, StringComparison.OrdinalIgnoreCase);

        await harness.Session.SendAsync(new CaptureNextPointCommand());
        Assert.True(harness.Recorder.Last<CommandRejectedEvent>() is not null, harness.Recorder.Trail());
    }

    /// <summary>
    /// Unconnected, a single solve is never a sample: a frame smeared by a turn
    /// in progress can still solve, to the average of where it passed through.
    /// Two in a row that agree mean the telescope has stopped (D26).
    /// </summary>
    [Fact]
    public async Task Unconnected_ASampleNeedsTwoAgreeingSolves()
    {
        using var harness = new SessionHarness(withMount: false);
        await harness.ReadyAsync(mount: false);

        PointCapturedEvent first = await harness.WaitForSampleAsync(1, poll: false);

        Assert.True(harness.Perfect.Calls >= 2, $"sampled after {harness.Perfect.Calls} solve(s)");
        Assert.Equal(SampleTrigger.Periodic, first.Trigger);
        Assert.All(harness.Perfect.Requests, r => Assert.Null(r.ApproximateRaDegrees));
    }

    /// <summary>
    /// The whole unconnected sequence, turned by hand exactly as instructed: the
    /// engine never learns the mount exists, and the injected misalignment comes
    /// back. The accuracy version, through a real solver, is the Phase 4d exit
    /// criterion.
    ///
    /// Following the instructions to the letter is the point. The plan's last
    /// point sits on the meridian margin, and an unconnected sample's rotation is
    /// measured about the nominal pole -- off by the misalignment itself -- so a
    /// next point stepped on from the measured position fell inside the margin
    /// and the sequence ended a sample short.
    /// </summary>
    [Fact]
    public async Task Unconnected_FollowingTheInstructionsCompletesAndRecoversTheMisalignment()
    {
        using var harness = new SessionHarness(withMount: false);
        await harness.ConnectAsync(mount: false);
        await harness.ConfirmSiteAsync();
        await harness.StartAsync();

        double declination = harness.Recorder.Last<TargetSelectedEvent>()!.DeclinationDegrees;

        for (int point = 1; point <= 5; point++)
        {
            SlewProposedEvent instruction = await harness.Recorder.WaitForAsync<SlewProposedEvent>(e => e.PointIndex == point);
            await harness.MoveFromOutsideAsync(instruction.MechanicalRotationDegrees, declination);
            await harness.WaitForSampleAsync(point, poll: false);
        }

        await harness.Recorder.WaitForAsync<SessionCompletedEvent>();

        AlignmentEstimate estimate = harness.Recorder.Last<AlignmentUpdatedEvent>()!.Estimate;
        Assert.Equal(5, harness.Recorder.All<PointCapturedEvent>().Count());
        Assert.Equal(SessionHarness.Injected.AltitudeErrorArcminutes, estimate.AltitudeErrorArcminutes, tolerance: 0.5);
        Assert.Equal(SessionHarness.Injected.AzimuthErrorArcminutes, estimate.AzimuthErrorArcminutes, tolerance: 0.5);
    }

    /// <summary>
    /// Unconnected, failures are part of turning the mount by hand (D26): they
    /// are counted and dismissed, and solving carries on.
    /// </summary>
    [Fact]
    public async Task Unconnected_FailuresAreDismissedAndSolvingCarriesOn()
    {
        using var harness = new SessionHarness(withMount: false, solver: new HopelessSolver());
        await harness.ReadyAsync(mount: false);

        await harness.Recorder.WaitForAsync<SolveFailedEvent>(e => e.ConsecutiveFailures >= 4);

        Assert.Null(harness.Recorder.Last<SessionFaultedEvent>());

        // Kept for diagnosis, under the prefix D26 names.
        Assert.NotEmpty(Directory.GetFiles(harness.FailedSolvesDirectory, $"{FrameStore.FailedFramePrefix}*"));
    }
}
