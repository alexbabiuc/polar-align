using FreePolarAlign.App.Services;
using FreePolarAlign.Session;
using FreePolarAlign.Solving;
using Xunit;

namespace FreePolarAlign.Tests.App;

/// <summary>
/// The solver choice in the settings window decides which solver a frame goes
/// to, blind and near separately, and that solver alone: a frame it cannot
/// solve is reported unsolved, never quietly handed to another (D31).
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
    /// A path with nothing at it: every external adapter fails on it at once with
    /// InvalidImage, without starting a process, which is enough to see where
    /// a frame was routed.
    /// </summary>
    private static string MissingFrame() => Path.Combine(Path.GetTempPath(), $"fpa-selected-{Guid.NewGuid():N}.fits");

    private static SelectedSolver Create(CountingSolver inner, List<string>? log = null, bool windows = true) =>
        new(inner, log is null ? null : log.Add, resolveExecutable: (_, path) => path ?? "solver-not-installed", canRunWindowsSolvers: windows);

    [Fact]
    public async Task Unconfigured_SendsEverythingToTheInternalSolver()
    {
        var inner = new CountingSolver();
        var solver = Create(inner);

        await solver.SolveAsync(Blind);
        await solver.SolveAsync(Near);

        Assert.Equal(2, inner.Requests.Count);
    }

    /// <summary>
    /// The defect behind this test: with ASTAP chosen, frames ASTAP refused went
    /// to Watney, and a live reading built from two solvers' answers moved in
    /// azimuth while only the altitude bolt was turned. The chosen solver's
    /// failure is now the answer.
    /// </summary>
    [Fact]
    public async Task AstapForBlind_ItsFailureIsReported_NotHandedToTheInternalSolver()
    {
        var inner = new CountingSolver();
        var solver = Create(inner);
        solver.Configure(new AppSettings { BlindSolver = SolverKind.Astap });

        PlateSolveResult result = await solver.SolveAsync(Blind);

        Assert.False(result.Success);
        Assert.Empty(inner.Requests);
        Assert.Equal("ASTAP", result.SolverName);
    }

    [Fact]
    public async Task AstapForBlindOnly_LeavesNearFramesWithTheInternalSolver()
    {
        var inner = new CountingSolver();
        var solver = Create(inner);
        solver.Configure(new AppSettings { BlindSolver = SolverKind.Astap, NearSolver = SolverKind.Internal });

        PlateSolveResult result = await solver.SolveAsync(Near);

        Assert.True(result.Success);
        Assert.Single(inner.Requests);
        Assert.Equal("Watney", result.SolverName);
    }

    [Fact]
    public async Task AstapForNear_IsUsedForHintedFrames()
    {
        var inner = new CountingSolver();
        var solver = Create(inner);
        solver.Configure(new AppSettings { NearSolver = SolverKind.Astap });

        PlateSolveResult near = await solver.SolveAsync(Near);
        PlateSolveResult blind = await solver.SolveAsync(Blind);

        Assert.Equal("ASTAP", near.SolverName);
        Assert.Equal("Watney", blind.SolverName);
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
    [InlineData(SolverKind.Asps, "ASPS")]
    [InlineData(SolverKind.Ps3, "PlateSolve3")]
    public async Task WindowsSolver_IsTheOnlySolverAsked(SolverKind kind, string name)
    {
        var inner = new CountingSolver();
        var solver = Create(inner, windows: true);
        solver.Configure(new AppSettings { BlindSolver = kind });

        PlateSolveResult result = await solver.SolveAsync(Blind);

        Assert.False(result.Success);
        Assert.Empty(inner.Requests);
        Assert.Equal(name, result.SolverName);
    }

    /// <summary>
    /// Chosen where it cannot run, a Windows solver is still the one asked --
    /// substituting another is exactly what was removed -- and the log says once
    /// why every frame will fail.
    /// </summary>
    [Theory]
    [InlineData(SolverKind.Asps, "ASPS")]
    [InlineData(SolverKind.Ps3, "PlateSolve3")]
    public async Task WindowsSolver_Elsewhere_IsStillTheOneAsked_AndTheLogSaysWhy(SolverKind kind, string name)
    {
        var inner = new CountingSolver();
        var log = new List<string>();
        var solver = Create(inner, log, windows: false);

        solver.Configure(new AppSettings { NearSolver = kind });
        PlateSolveResult result = await solver.SolveAsync(Near);

        Assert.False(result.Success);
        Assert.Empty(inner.Requests);
        Assert.Contains(log, line => line.Contains($"{name}, runs only on Windows"));
    }

    [Theory]
    [InlineData(SolverKind.Asps)]
    [InlineData(SolverKind.Ps3)]
    public void ChangedSolverPath_IsAppliedAndLogged(SolverKind kind)
    {
        var log = new List<string>();
        var solver = Create(new CountingSolver(), log);
        solver.Configure(new AppSettings { BlindSolver = kind });
        log.Clear();

        solver.Configure(kind == SolverKind.Asps
            ? new AppSettings { BlindSolver = kind, AspsPath = @"D:\Tools\PlateSolver.exe" }
            : new AppSettings { BlindSolver = kind, Ps3Path = @"D:\Tools\PlateSolve3.80.exe" });

        Assert.Contains(log, line => line.Contains(@"D:\Tools\"));
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
