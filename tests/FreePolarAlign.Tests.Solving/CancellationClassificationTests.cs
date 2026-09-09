using FreePolarAlign.Solving;
using Xunit;

namespace FreePolarAlign.Tests.Solving;

/// <summary>
/// Watney (and ASTAP's process) get a single linked token combining the
/// caller's <see cref="CancellationToken"/> with an internal timeout token.
/// D3 requires "the request's timeout" and "the caller cancelling" to remain
/// distinct, reachable <see cref="PlateSolveFailureReason"/> values rather
/// than collapsing into one -- this is the decision that keeps them apart.
/// </summary>
public class CancellationClassificationTests
{
    [Fact]
    public void CallerCancelled_ClassifiesAsCancelled()
    {
        using var callerCts = new CancellationTokenSource();
        using var timeoutCts = new CancellationTokenSource();
        callerCts.Cancel();

        PlateSolveFailureReason reason = CancellationClassification.Classify(callerCts.Token, timeoutCts);

        Assert.Equal(PlateSolveFailureReason.Cancelled, reason);
    }

    [Fact]
    public void TimeoutFired_WithoutCallerCancellation_ClassifiesAsTimeout()
    {
        using var callerCts = new CancellationTokenSource();
        using var timeoutCts = new CancellationTokenSource();
        timeoutCts.Cancel();

        PlateSolveFailureReason reason = CancellationClassification.Classify(callerCts.Token, timeoutCts);

        Assert.Equal(PlateSolveFailureReason.Timeout, reason);
    }

    [Fact]
    public void BothFired_CallerCancellationTakesPriority()
    {
        // A caller that asked to stop should never be told it merely timed out.
        using var callerCts = new CancellationTokenSource();
        using var timeoutCts = new CancellationTokenSource();
        callerCts.Cancel();
        timeoutCts.Cancel();

        PlateSolveFailureReason reason = CancellationClassification.Classify(callerCts.Token, timeoutCts);

        Assert.Equal(PlateSolveFailureReason.Cancelled, reason);
    }

    [Theory]
    [InlineData(PlateSolveFailureReason.Cancelled, "Solve was cancelled by the caller.")]
    [InlineData(PlateSolveFailureReason.Timeout, "SolverName did not finish within the requested timeout.")]
    public void Message_IsReasonSpecific(PlateSolveFailureReason reason, string expected)
    {
        Assert.Equal(expected, CancellationClassification.Message(reason, "SolverName"));
    }
}
