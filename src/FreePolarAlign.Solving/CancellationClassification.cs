namespace FreePolarAlign.Solving;

/// <summary>
/// Shared timeout-vs-cancellation classification for adapters that hand a
/// backend only a single <see cref="CancellationToken"/> (Watney's library
/// call, ASTAP's subprocess): the adapter links the caller's token with an
/// internal timeout token, and once the backend reports "did not finish" this
/// decides which of the two actually fired. D3 requires the two outcomes stay
/// distinct rather than both collapsing to one failure reason.
/// </summary>
/// <remarks>
/// If both tokens happen to fire in the same instant, caller cancellation wins:
/// a caller that asked to stop should never be told it merely timed out.
/// </remarks>
internal static class CancellationClassification
{
    public static PlateSolveFailureReason Classify(CancellationToken callerToken, CancellationTokenSource timeoutCts) =>
        callerToken.IsCancellationRequested ? PlateSolveFailureReason.Cancelled : PlateSolveFailureReason.Timeout;

    public static string Message(PlateSolveFailureReason reason, string solverName) =>
        reason == PlateSolveFailureReason.Cancelled
            ? "Solve was cancelled by the caller."
            : $"{solverName} did not finish within the requested timeout.";
}
