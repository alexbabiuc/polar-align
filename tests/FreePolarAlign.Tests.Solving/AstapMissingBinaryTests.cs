using FreePolarAlign.Imaging.Fits;
using FreePolarAlign.Solving;
using Xunit;

namespace FreePolarAlign.Tests.Solving;

/// <summary>
/// ASTAP is GPL and optional (D3); almost no development or CI machine will
/// have it installed. The adapter must degrade cleanly -- a readable
/// <see cref="PlateSolveFailureReason.SolverError"/> result, never an
/// exception -- and that is true unconditionally here, so this genuinely
/// exercises the real code path with no ASTAP install required.
/// </summary>
public class AstapMissingBinaryTests
{
    private static string WriteMinimalFits()
    {
        string path = Path.Combine(Path.GetTempPath(), $"fpa-astap-missing-{Guid.NewGuid():N}.fits");
        var image = new FitsImage(8, 8, FitsBitPix.Int16, bzero: 0.0, bscale: 1.0, new double[8, 8]);
        FitsFile.Write(path, image);
        return path;
    }

    [Fact]
    public async Task MissingExecutable_ReturnsSolverErrorNotException()
    {
        string imagePath = WriteMinimalFits();
        try
        {
            var solver = new AstapPlateSolver(executablePath: "/definitely/does/not/exist/astap-" + Guid.NewGuid().ToString("N"));

            PlateSolveResult result = await solver.SolveAsync(new PlateSolveRequest(imagePath));

            Assert.False(result.Success);
            Assert.Equal(PlateSolveFailureReason.SolverError, result.FailureReason);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task InvalidImagePath_ReturnsInvalidImageNotException()
    {
        var solver = new AstapPlateSolver(executablePath: "astap-" + Guid.NewGuid().ToString("N"));

        PlateSolveResult result = await solver.SolveAsync(new PlateSolveRequest("/no/such/file.fits"));

        Assert.False(result.Success);
        Assert.Equal(PlateSolveFailureReason.InvalidImage, result.FailureReason);
    }
}
