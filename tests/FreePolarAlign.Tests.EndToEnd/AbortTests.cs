using FreePolarAlign.Core.Engine;
using FreePolarAlign.Devices;
using FreePolarAlign.Session;
using Xunit;

namespace FreePolarAlign.Tests.EndToEnd;

/// <summary>
/// D36: Abort stops the mount, drive included, and Stop sequence does not.
/// Before this the two differed only in what the window kept on screen, so the
/// red button promised an emergency stop it did not perform.
/// </summary>
public class AbortTests
{
    [Fact]
    public async Task Abort_TurnsTrackingOffAndSaysSo()
    {
        using var harness = new SessionHarness();
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);

        await harness.Session.SendAsync(new AbortSessionCommand("Aborted by operator."));

        SessionFaultedEvent? faulted = harness.Recorder.Last<SessionFaultedEvent>();
        Assert.True(faulted is not null, harness.Recorder.Trail());
        Assert.Contains("tracking included", faulted!.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, harness.Mount.Halts);

        MountPosition position = await harness.Simulated.GetPositionAsync();
        Assert.Equal(TrackingState.Stopped, position.Tracking);
    }

    /// <summary>The difference Abort exists for: Stop ends the sequence and leaves the drive running.</summary>
    [Fact]
    public async Task StopSequence_LeavesTrackingOn()
    {
        using var harness = new SessionHarness();
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);

        await harness.Session.SendAsync(new CancelSessionCommand());

        Assert.Equal(0, harness.Mount.Halts);
        MountPosition position = await harness.Simulated.GetPositionAsync();
        Assert.Equal(TrackingState.Tracking, position.Tracking);
    }

    /// <summary>
    /// A slew under way when Abort arrives ends where it has got to, rather
    /// than finishing, and the drive is off afterwards.
    /// </summary>
    [Fact]
    public async Task AbortMidSlew_StopsTheSlewShortAndTheDrive()
    {
        using var harness = new SessionHarness(slewDuration: TimeSpan.FromSeconds(30));
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);

        await harness.Recorder.WaitForAsync<SlewProposedEvent>(e => e.PointIndex == 2);
        await harness.Session.SendAsync(new CaptureNextPointCommand());
        await harness.Recorder.WaitForAsync<SlewConfirmedEvent>();
        await Task.Delay(200);

        await harness.Session.SendAsync(new AbortSessionCommand("Aborted by operator."));

        MountPosition position = await harness.Simulated.GetPositionAsync();
        Assert.False(position.IsSlewing);
        Assert.Equal(TrackingState.Stopped, position.Tracking);
    }

    /// <summary>
    /// A driver that will not turn tracking off must not be reported as stopped:
    /// someone reading "aborted" would otherwise believe the drive was off.
    /// </summary>
    [Fact]
    public async Task ADriverThatKeepsTracking_IsReportedAsSuch()
    {
        using var harness = new SessionHarness(canStopTracking: false);
        await harness.ReadyAsync();
        await harness.WaitForSampleAsync(1);

        await harness.Session.SendAsync(new AbortSessionCommand("Aborted by operator."));

        SessionFaultedEvent? faulted = harness.Recorder.Last<SessionFaultedEvent>();
        Assert.True(faulted is not null, harness.Recorder.Trail());
        Assert.Contains("tracking may still be on", faulted!.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Without a mount there is no drive to stop, and Abort is refused rather
    /// than quietly doing what Stop does; the sequence carries on.
    /// </summary>
    [Fact]
    public async Task WithoutAMount_AbortIsRefusedAndTheSequenceContinues()
    {
        using var harness = new SessionHarness(withMount: false);
        await harness.ReadyAsync(mount: false);

        await harness.Session.SendAsync(new AbortSessionCommand("Aborted by operator."));

        CommandRejectedEvent? rejected = harness.Recorder.Last<CommandRejectedEvent>();
        Assert.True(rejected is not null, harness.Recorder.Trail());
        Assert.Contains("no mount", rejected!.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(harness.Recorder.Last<SessionFaultedEvent>());
    }
}
