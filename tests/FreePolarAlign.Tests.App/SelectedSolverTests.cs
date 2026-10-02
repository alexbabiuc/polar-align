using FreePolarAlign.App.Services;
using FreePolarAlign.Session;
using FreePolarAlign.Solving;
using Xunit;

namespace FreePolarAlign.Tests.App;

/// <summary>
/// The solver choice in the settings window decides which solver a frame goes
/// to, blind and near separately, and the internal solver still gets whatever
/// the chosen one cannot solve.
/// </summary>
public class SelectedSolverTests
{
    private static readonly PlateSolveSolution Solution = new(10.0, 20.0, 3.9, 0.0, -0.001, 0.0, 0.0, 0.001, 50, TimeSpan.FromSeconds(1));

    private sealed class CountingSolver : ISolver
    {
        public string Name => "Watney";

        public List<PlateSolveRequest> Requests { get; } = new();

        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(PlateSolveResult.Succeeded(Solution));
        }
    }

    private static readonly PlateSolveRequest Blind = new(MissingFrame());

    private static readonly PlateSolveRequest Near = new(MissingFrame(), ApproximateRaDegrees: 10.0, ApproximateDecDegrees: 20.0);

    /// <summary>
    /// A path with nothing at it: ASTAP's adapter fails on it at once with
    /// InvalidImage, without starting a process, which is enough to see where
    /// a frame was routed.
    /// </summary>
    private static string MissingFrame() => Path.Combine(Path.GetTempPath(), $"fpa-selected-{Guid.NewGuid():N}.fits");

    private static SelectedSolver Create(CountingSolver inner, List<string>? log = null) =>
        new(inner, log is null ? null : log.Add, resolveAstap: path => path ?? "astap-not-installed");

    [Fact]
    public async Task Unconfigured_SendsEverythingToTheInternalSolver()
    {
        var inner = new CountingSolver();
        var solver = Create(inner);

        await solver.SolveAsync(Blind);
        await solver.SolveAsync(Near);

        Assert.Equal(2, inner.Requests.Count);
    }

    [Fact]
    public async Task AstapForBlind_TriesAstapFirstThenTheInternalSolver()
    {
        var inner = new CountingSolver();
        var log = new List<string>();
        var solver = Create(inner, log);
        solver.Configure(new AppSettings { BlindSolver = SolverKind.Astap });

        PlateSolveResult result = await solver.SolveAsync(Blind);

        Assert.True(result.Success);
        Assert.Single(inner.Requests);
        Assert.Contains(log, line => line.StartsWith("ASTAP did not solve the frame", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AstapForBlindOnly_LeavesNearFramesWithTheInternalSolver()
    {
        var inner = new CountingSolver();
        var log = new List<string>();
        var solver = Create(inner, log);
        solver.Configure(new AppSettings { BlindSolver = SolverKind.Astap, NearSolver = SolverKind.Internal });
        log.Clear();

        await solver.SolveAsync(Near);

        Assert.Single(inner.Requests);
        Assert.Empty(log);
    }

    [Fact]
    public async Task AstapForNear_IsUsedForHintedFrames()
    {
        var inner = new CountingSolver();
        var log = new List<string>();
        var solver = Create(inner, log);
        solver.Configure(new AppSettings { NearSolver = SolverKind.Astap });
        log.Clear();

        await solver.SolveAsync(Near);

        Assert.Contains(log, line => line.StartsWith("ASTAP did not solve the frame", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfiguredAstapPath_IsResolvedAndLogged()
    {
        var log = new List<string>();
        var solver = Create(new CountingSolver(), log);

        solver.Configure(new AppSettings { BlindSolver = SolverKind.Astap, AstapPath = "/opt/astap/astap_cli" });

        Assert.Contains(log, line => line.Contains("'/opt/astap/astap_cli'"));
    }

    [Theory]
    [InlineData(SolverKind.Asps)]
    [InlineData(SolverKind.Ps3)]
    public async Task SolverWithoutAnAdapter_FallsBackToTheInternalSolver(SolverKind kind)
    {
        var inner = new CountingSolver();
        var log = new List<string>();
        var solver = Create(inner, log);

        solver.Configure(new AppSettings { BlindSolver = kind });
        PlateSolveResult result = await solver.SolveAsync(Blind);

        Assert.True(result.Success);
        Assert.Single(inner.Requests);
        Assert.Contains(log, line => line.Contains("not available in this version"));
    }

    /// <summary>Configure runs on every settings change, including the exposure.</summary>
    [Fact]
    public void UnrelatedSettingsChange_IsNotLoggedAgain()
    {
        var log = new List<string>();
        var solver = Create(new CountingSolver(), log);
        solver.Configure(new AppSettings { BlindSolver = SolverKind.Astap });
        int logged = log.Count;

        solver.Configure(new AppSettings { BlindSolver = SolverKind.Astap, ExposureSeconds = 2.0 });

        Assert.Equal(logged, log.Count);
    }

    [Fact]
    public async Task ChangingBackToInternal_TakesEffectOnTheNextFrame()
    {
        var inner = new CountingSolver();
        var log = new List<string>();
        var solver = Create(inner, log);
        solver.Configure(new AppSettings { BlindSolver = SolverKind.Astap });
        solver.Configure(new AppSettings { BlindSolver = SolverKind.Internal });
        log.Clear();

        await solver.SolveAsync(Blind);

        Assert.Single(inner.Requests);
        Assert.Empty(log);
    }
}
