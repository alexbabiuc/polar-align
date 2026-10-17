using FreePolarAlign.Solving;
using FreePolarAlign.Tests.Support;
using Xunit;

namespace FreePolarAlign.Tests.Solving;

/// <summary>
/// A scale from a typed focal length first, and no scale after it when nothing
/// matches, for every solver alike (D41).
/// </summary>
public class ClaimedScaleFallbackTests
{
    private static readonly PlateSolveSolution Solved = new(
        20.8, 65.5, 0.73, 0.0, -0.73 / 3600.0, 0.0, 0.0, 0.73 / 3600.0, 40, TimeSpan.Zero);

    /// <summary>Answers from a script, moving the clock on by what each try "took".</summary>
    private sealed class ScriptedSolver(TestClock clock, params (PlateSolveResult Result, TimeSpan Takes)[] script) : ISolver
    {
        private int _next;

        public List<PlateSolveRequest> Requests { get; } = new();

        public string Name => "Scripted (test)";

        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            (PlateSolveResult result, TimeSpan takes) = script[_next++];
            clock.Advance(takes);
            return Task.FromResult(result);
        }
    }

    private static PlateSolveResult NoMatch(string message = "No solution found!") =>
        PlateSolveResult.Failed(PlateSolveFailureReason.NoMatchFound, message);

    private static PlateSolveRequest Claimed => new(
        "in.fits", ApproximateScaleArcsecPerPixel: 0.7477, ScaleToleranceFraction: 0.25,
        ApproximateRaDegrees: 37.96, ApproximateDecDegrees: 63.84, SearchRadiusDegrees: 10.0,
        Timeout: TimeSpan.FromSeconds(20));

    [Fact]
    public async Task ClaimedScale_NoMatch_IsTriedAgainWithoutAScale_InWhatIsLeftOfTheTimeout()
    {
        var clock = new TestClock();
        var inner = new ScriptedSolver(clock,
            (NoMatch(), TimeSpan.FromSeconds(3)),
            (PlateSolveResult.Succeeded(Solved), TimeSpan.FromSeconds(1)));

        PlateSolveResult result = await new ClaimedScaleFallbackSolver(inner, clock).SolveAsync(Claimed);

        Assert.True(result.Success);
        Assert.Equal(2, inner.Requests.Count);
        PlateSolveRequest second = inner.Requests[1];
        Assert.Null(second.ApproximateScaleArcsecPerPixel);
        Assert.Null(second.ScaleToleranceFraction);
        Assert.Equal(TimeSpan.FromSeconds(17), second.Timeout);

        // The position is kept: only the scale was in doubt.
        Assert.Equal(37.96, second.ApproximateRaDegrees);
    }

    [Fact]
    public async Task ClaimedScale_ThatSolves_IsNotTriedAgain()
    {
        var clock = new TestClock();
        var inner = new ScriptedSolver(clock, (PlateSolveResult.Succeeded(Solved), TimeSpan.FromSeconds(1)));

        PlateSolveResult result = await new ClaimedScaleFallbackSolver(inner, clock).SolveAsync(Claimed);

        Assert.True(result.Success);
        Assert.Single(inner.Requests);
    }

    /// <summary>
    /// Once a solve has measured the focal length its scale is not second-guessed:
    /// a frame that fails with it did not fail for its scale, and a retry would
    /// only double the time a cloudy frame takes to be given up on.
    /// </summary>
    [Fact]
    public async Task MeasuredScale_NoMatch_IsNotTriedAgain()
    {
        var clock = new TestClock();
        var inner = new ScriptedSolver(clock, (NoMatch(), TimeSpan.FromSeconds(1)));

        await new ClaimedScaleFallbackSolver(inner, clock).SolveAsync(Claimed with { ScaleToleranceFraction = 0.05 });

        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task NoScale_NoMatch_IsNotTriedAgain()
    {
        var clock = new TestClock();
        var inner = new ScriptedSolver(clock, (NoMatch(), TimeSpan.FromSeconds(1)));

        await new ClaimedScaleFallbackSolver(inner, clock).SolveAsync(
            Claimed with { ApproximateScaleArcsecPerPixel = null, ScaleToleranceFraction = null });

        Assert.Single(inner.Requests);
    }

    [Theory]
    [InlineData(PlateSolveFailureReason.NoStarsDetected)]
    [InlineData(PlateSolveFailureReason.Timeout)]
    [InlineData(PlateSolveFailureReason.SolverError)]
    public async Task FailuresThatAreNotAboutScale_AreNotTriedAgain(PlateSolveFailureReason reason)
    {
        var clock = new TestClock();
        var inner = new ScriptedSolver(clock, (PlateSolveResult.Failed(reason, "no"), TimeSpan.FromSeconds(1)));

        PlateSolveResult result = await new ClaimedScaleFallbackSolver(inner, clock).SolveAsync(Claimed);

        Assert.Equal(reason, result.FailureReason);
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task NoTimeLeft_IsNotTriedAgain()
    {
        var clock = new TestClock();
        var inner = new ScriptedSolver(clock, (NoMatch(), TimeSpan.FromSeconds(20)));

        PlateSolveResult result = await new ClaimedScaleFallbackSolver(inner, clock).SolveAsync(Claimed);

        Assert.Equal(PlateSolveFailureReason.NoMatchFound, result.FailureReason);
        Assert.Single(inner.Requests);
    }

    /// <summary>A log that says what was tried is far easier to act on than "not solved".</summary>
    [Fact]
    public async Task BothFailing_SaysWhatEachTryFound()
    {
        var clock = new TestClock();
        var inner = new ScriptedSolver(clock,
            (NoMatch("with the scale"), TimeSpan.FromSeconds(1)),
            (NoMatch("without it"), TimeSpan.FromSeconds(1)));

        PlateSolveResult result = await new ClaimedScaleFallbackSolver(inner, clock).SolveAsync(Claimed);

        Assert.Equal(PlateSolveFailureReason.NoMatchFound, result.FailureReason);
        Assert.Contains("with the scale", result.Message);
        Assert.Contains("without it", result.Message);
    }
}
