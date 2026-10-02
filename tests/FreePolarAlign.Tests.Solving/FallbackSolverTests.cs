using FreePolarAlign.Solving;
using Xunit;

namespace FreePolarAlign.Tests.Solving;

/// <summary>
/// The guarantee: a frame the chosen solver cannot solve still gets the
/// internal solver's attempt, whatever the reason it failed -- except when the
/// user stopped the sequence.
/// </summary>
public class FallbackSolverTests
{
    private static readonly PlateSolveSolution Solution = new(10.0, 20.0, 3.9, 0.0, -0.001, 0.0, 0.0, 0.001, 50, TimeSpan.FromSeconds(1));

    private sealed class ScriptedSolver : ISolver
    {
        private readonly PlateSolveResult _result;

        public ScriptedSolver(string name, PlateSolveResult result)
        {
            Name = name;
            _result = result;
        }

        public string Name { get; }

        public int Calls { get; private set; }

        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private static readonly PlateSolveRequest Request = new("frame.fits");

    [Fact]
    public async Task PrimarySolves_FallbackIsNotAsked()
    {
        var primary = new ScriptedSolver("ASTAP", PlateSolveResult.Succeeded(Solution));
        var fallback = new ScriptedSolver("Watney", PlateSolveResult.Succeeded(Solution));

        PlateSolveResult result = await new FallbackSolver(primary, fallback).SolveAsync(Request);

        Assert.True(result.Success);
        Assert.Equal(0, fallback.Calls);
    }

    [Theory]
    [InlineData(PlateSolveFailureReason.NoMatchFound)]
    [InlineData(PlateSolveFailureReason.NoStarsDetected)]
    [InlineData(PlateSolveFailureReason.Timeout)]
    [InlineData(PlateSolveFailureReason.SolverError)]
    [InlineData(PlateSolveFailureReason.InvalidImage)]
    public async Task PrimaryFails_FallbackSolves(PlateSolveFailureReason reason)
    {
        var primary = new ScriptedSolver("ASTAP", PlateSolveResult.Failed(reason, "no"));
        var fallback = new ScriptedSolver("Watney", PlateSolveResult.Succeeded(Solution));
        var log = new List<string>();

        PlateSolveResult result = await new FallbackSolver(primary, fallback, log.Add).SolveAsync(Request);

        Assert.True(result.Success);
        Assert.Equal(1, fallback.Calls);
        Assert.Contains(log, line => line.Contains("Trying Watney"));
    }

    [Fact]
    public async Task PrimaryCancelled_FallbackIsNotAsked()
    {
        var primary = new ScriptedSolver("ASTAP", PlateSolveResult.Failed(PlateSolveFailureReason.Cancelled, "stopped"));
        var fallback = new ScriptedSolver("Watney", PlateSolveResult.Succeeded(Solution));

        PlateSolveResult result = await new FallbackSolver(primary, fallback).SolveAsync(Request);

        Assert.Equal(PlateSolveFailureReason.Cancelled, result.FailureReason);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task BothFail_ReportsTheFallbacksReasonAndBothMessages()
    {
        var primary = new ScriptedSolver("ASTAP", PlateSolveResult.Failed(PlateSolveFailureReason.SolverError, "Could not start ASTAP."));
        var fallback = new ScriptedSolver("Watney", PlateSolveResult.Failed(PlateSolveFailureReason.NoMatchFound, "No match."));

        PlateSolveResult result = await new FallbackSolver(primary, fallback).SolveAsync(Request);

        Assert.Equal(PlateSolveFailureReason.NoMatchFound, result.FailureReason);
        Assert.Contains("Could not start ASTAP.", result.Message);
        Assert.Contains("No match.", result.Message);
    }

    [Fact]
    public async Task MissingAstap_FallsBackToTheOtherSolver()
    {
        string imagePath = Path.Combine(Path.GetTempPath(), $"fpa-fallback-{Guid.NewGuid():N}.fits");
        Imaging.Fits.FitsFile.Write(imagePath, new Imaging.Fits.FitsImage(8, 8, Imaging.Fits.FitsBitPix.Int16, 0.0, 1.0, new double[8, 8]));
        try
        {
            var astap = new AstapPlateSolver("/definitely/not/astap-" + Guid.NewGuid().ToString("N"));
            var fallback = new ScriptedSolver("Watney", PlateSolveResult.Succeeded(Solution));

            PlateSolveResult result = await new FallbackSolver(astap, fallback).SolveAsync(new PlateSolveRequest(imagePath));

            Assert.True(result.Success);
            Assert.Equal(1, fallback.Calls);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }
}
