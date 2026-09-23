using FreePolarAlign.Imaging.Fits;
using FreePolarAlign.Solving;
using Xunit;

namespace FreePolarAlign.Tests.Solving;

/// <summary>
/// The second attempt: when a solver cannot match a frame, it is offered the
/// same stars redrawn as a clean picture before the failure is reported.
///
/// What is tested here is the decision-making around that -- when it fires,
/// when it must not, what it reports, and what it writes to the log -- with a
/// stand-in solver, so none of it depends on a quad database or on which sky
/// the frame happens to show. That the redraw can actually rescue a real frame
/// is a separate claim, measured against real data rather than asserted here.
/// </summary>
public class RestampFallbackTests
{
    private sealed class ScriptedSolver : ISolver
    {
        private readonly Queue<PlateSolveResult> _results;

        public ScriptedSolver(params PlateSolveResult[] results) => _results = new Queue<PlateSolveResult>(results);

        public string Name => "Scripted";

        public List<string> Paths { get; } = new();

        public int Calls => Paths.Count;

        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
        {
            Paths.Add(request.ImagePath);
            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : Failure(PlateSolveFailureReason.NoMatchFound));
        }
    }

    private static PlateSolveResult Failure(PlateSolveFailureReason reason) =>
        PlateSolveResult.Failed(reason, $"{reason} for the test");

    private static PlateSolveResult Success() =>
        PlateSolveResult.Succeeded(new PlateSolveSolution(
            CenterRaDegrees: 355.013,
            CenterDecDegrees: 72.773,
            PixelScaleArcsecPerPixel: 5.71,
            RotationDegrees: 94.0,
            Cd1_1: -0.0015,
            Cd1_2: 0.0,
            Cd2_1: 0.0,
            Cd2_2: 0.0015,
            MatchedStarCount: 60,
            SolveDuration: TimeSpan.FromMilliseconds(150)));

    /// <summary>
    /// A frame with enough stars in it to be worth redrawing: a flat background
    /// and a grid of small Gaussians, which the detector finds and the redraw
    /// can reproduce.
    /// </summary>
    private static string WriteFrameWithStars(string directory, int starCount = 40)
    {
        const int width = 400;
        const int height = 300;
        var pixels = new double[height, width];
        var random = new Random(4);

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            pixels[y, x] = 500.0 + (random.NextDouble() * 4.0);
        }

        for (int i = 0; i < starCount; i++)
        {
            int cx = 20 + (i * 37 % (width - 40));
            int cy = 20 + (i * 53 % (height - 40));
            double peak = 3000.0 + (i * 400.0);
            for (int dy = -3; dy <= 3; dy++)
            for (int dx = -3; dx <= 3; dx++)
            {
                pixels[cy + dy, cx + dx] += peak * Math.Exp(-((dx * dx) + (dy * dy)) / 3.0);
            }
        }

        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"frame-{Guid.NewGuid():N}.fits");
        FitsFile.Write(path, FitsImage.ForCapturedFrame(width, height, pixels, bitsPerPixel: 16));
        return path;
    }

    private static string TempDirectory() =>
        Path.Combine(Path.GetTempPath(), $"fpa-restamp-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task ASolveThatSucceedsIsNotRedrawn()
    {
        string directory = TempDirectory();
        try
        {
            var inner = new ScriptedSolver(Success());
            var log = new List<string>();
            using var solver = new RestampFallbackSolver(inner, directory, log.Add);

            PlateSolveResult result = await solver.SolveAsync(new PlateSolveRequest(WriteFrameWithStars(directory)));

            Assert.True(result.Success);
            Assert.Equal(1, inner.Calls);
            Assert.Empty(log);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The case this exists for: the original will not match, the redraw does,
    /// and the caller is handed the solve rather than the failure.
    /// </summary>
    [Theory]
    [InlineData(PlateSolveFailureReason.NoMatchFound)]
    [InlineData(PlateSolveFailureReason.NoStarsDetected)]
    public async Task AFrameThatWillNotMatchIsRedrawnAndCanSolve(PlateSolveFailureReason reason)
    {
        string directory = TempDirectory();
        try
        {
            var inner = new ScriptedSolver(Failure(reason), Success());
            var log = new List<string>();
            using var solver = new RestampFallbackSolver(inner, directory, log.Add);

            string original = WriteFrameWithStars(directory);
            PlateSolveResult result = await solver.SolveAsync(new PlateSolveRequest(original));

            Assert.True(result.Success);
            Assert.Equal(2, inner.Calls);
            Assert.NotEqual(original, inner.Paths[1]);

            // Both actions logged: that a second attempt was made, and what it did.
            Assert.Equal(2, log.Count);
            Assert.Contains("Retrying", log[0]);
            Assert.Contains("solved", log[1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// When the redraw does not solve either, the failure reported is the
    /// original one. The redraw's failure describes a picture the user never
    /// took, and reporting it would send them looking at the wrong thing.
    /// </summary>
    [Fact]
    public async Task WhenTheRedrawAlsoFailsTheOriginalFailureIsReported()
    {
        string directory = TempDirectory();
        try
        {
            var inner = new ScriptedSolver(
                Failure(PlateSolveFailureReason.NoStarsDetected),
                Failure(PlateSolveFailureReason.NoMatchFound));
            var log = new List<string>();
            using var solver = new RestampFallbackSolver(inner, directory, log.Add);

            PlateSolveResult result = await solver.SolveAsync(new PlateSolveRequest(WriteFrameWithStars(directory)));

            Assert.False(result.Success);
            Assert.Equal(PlateSolveFailureReason.NoStarsDetected, result.FailureReason);
            Assert.Equal(2, inner.Calls);
            Assert.Equal(2, log.Count);
            Assert.Contains("did not solve either", log[1]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Failures that describe something other than the star pattern are not
    /// worth a second picture of the same stars, and redrawing would spend the
    /// time of a user already waiting.
    /// </summary>
    [Theory]
    [InlineData(PlateSolveFailureReason.Timeout)]
    [InlineData(PlateSolveFailureReason.InvalidImage)]
    [InlineData(PlateSolveFailureReason.SolverError)]
    [InlineData(PlateSolveFailureReason.Cancelled)]
    public async Task FailuresARedrawCannotFixAreNotRedrawn(PlateSolveFailureReason reason)
    {
        string directory = TempDirectory();
        try
        {
            var inner = new ScriptedSolver(Failure(reason), Success());
            var log = new List<string>();
            using var solver = new RestampFallbackSolver(inner, directory, log.Add);

            PlateSolveResult result = await solver.SolveAsync(new PlateSolveRequest(WriteFrameWithStars(directory)));

            Assert.False(result.Success);
            Assert.Equal(reason, result.FailureReason);
            Assert.Equal(1, inner.Calls);
            Assert.Empty(log);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A frame with nothing in it has nothing to redraw, and drawing an empty
    /// picture only spends a second solve to say so.
    /// </summary>
    [Fact]
    public async Task AFrameWithTooFewStarsIsNotRedrawn()
    {
        string directory = TempDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            var blank = new double[64, 64];
            for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                blank[y, x] = 500.0;
            }

            string path = Path.Combine(directory, "blank.fits");
            FitsFile.Write(path, FitsImage.ForCapturedFrame(64, 64, blank, bitsPerPixel: 16));

            var inner = new ScriptedSolver(Failure(PlateSolveFailureReason.NoStarsDetected), Success());
            var log = new List<string>();
            using var solver = new RestampFallbackSolver(inner, directory, log.Add);

            PlateSolveResult result = await solver.SolveAsync(new PlateSolveRequest(path));

            Assert.False(result.Success);
            Assert.Equal(1, inner.Calls);
            Assert.Contains(log, line => line.Contains("too few stars", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// An unreadable frame must not turn a failed solve into a failed session:
    /// the fallback swallows its own problems and reports what the solver said.
    /// </summary>
    [Fact]
    public async Task AFrameThatCannotBeRedrawnReportsTheOriginalFailure()
    {
        string directory = TempDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "not-a-fits.fits");
            File.WriteAllText(path, "this is not a FITS file");

            var inner = new ScriptedSolver(Failure(PlateSolveFailureReason.NoMatchFound));
            var log = new List<string>();
            using var solver = new RestampFallbackSolver(inner, directory, log.Add);

            PlateSolveResult result = await solver.SolveAsync(new PlateSolveRequest(path));

            Assert.False(result.Success);
            Assert.Equal(PlateSolveFailureReason.NoMatchFound, result.FailureReason);
            Assert.Contains(log, line => line.Contains("Could not redraw", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The redrawn frame is a temporary: it must not survive the solve that used it.</summary>
    [Fact]
    public async Task TheRedrawnFrameIsCleanedUp()
    {
        string directory = TempDirectory();
        try
        {
            var inner = new ScriptedSolver(Failure(PlateSolveFailureReason.NoMatchFound), Success());
            using var solver = new RestampFallbackSolver(inner, directory);

            string original = WriteFrameWithStars(directory);
            await solver.SolveAsync(new PlateSolveRequest(original));

            Assert.Equal(2, inner.Calls);
            Assert.False(File.Exists(inner.Paths[1]), "the redrawn frame was left behind");
            Assert.True(File.Exists(original), "the original frame must not be touched");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
