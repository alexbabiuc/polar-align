using FreePolarAlign.Solving;
using FreePolarAlign.Tests.Support;
using Xunit;

namespace FreePolarAlign.Tests.Solving;

/// <summary>
/// D46: a failed solve carries one short sentence for the screen and the
/// solver's own output, whole, for the log. Pinned against what ASTAP actually
/// printed for an ATR2600M frame on 2026-11-05: 111 lines for one attempt, which
/// went on screen in full, twice, before this.
/// </summary>
public class FailureMessageTests
{
    private static readonly string AstapOutput = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "astap_no_match_20261105.txt")).Trim();

    /// <summary>Short enough for a panel that is about fifty characters wide.</summary>
    private const int ScreenSentenceLimit = 200;

    private static void AssertFitForScreen(string? message)
    {
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.True(message!.Length <= ScreenSentenceLimit, $"{message.Length} characters: {message}");
        Assert.DoesNotContain('\n', message);
    }

    [Fact]
    public void AstapsConsoleOutput_GoesToTheDetailAndNotTheMessage()
    {
        PlateSolveResult result = AstapPlateSolver.ClassifyExitCode(1, AstapOutput);

        Assert.Equal(PlateSolveFailureReason.NoMatchFound, result.FailureReason);
        AssertFitForScreen(result.Message);
        Assert.DoesNotContain("ASTAP solver version", result.Message);
        Assert.Equal(AstapOutput, result.Detail);
    }

    /// <summary>ASTAP's one-line reason is what a user can act on, so it stays on screen.</summary>
    [Fact]
    public void AstapsOneLineReason_IsQuotedInTheMessage()
    {
        PlateSolveResult result = AstapPlateSolver.ClassifyExitCode(2, "Not enough stars.");

        Assert.Equal("ASTAP did not detect enough stars in the image (Not enough stars).", result.Message);
    }

    /// <summary>The install hint used to come after the pasted output; it now comes first.</summary>
    [Fact]
    public void AMissingStarDatabase_KeepsItsInstallHintOnScreen()
    {
        PlateSolveResult result = AstapPlateSolver.ClassifyExitCode(32, AstapOutput);

        AssertFitForScreen(result.Message);
        Assert.Contains("D05", result.Message);
        Assert.Equal(AstapOutput, result.Detail);
    }

    /// <summary>
    /// The case seen in the field: the entered focal length's scale, then none,
    /// both failing. One sentence on screen; both transcripts, labelled, in the log.
    /// </summary>
    [Fact]
    public async Task BothScaleTriesFailing_GiveOneSentenceAndBothTranscripts()
    {
        var clock = new TestClock();
        var inner = new AlwaysAstapNoMatch();
        var request = new PlateSolveRequest(
            "frame.fits", ApproximateScaleArcsecPerPixel: 1.0, ScaleToleranceFraction: 0.3,
            Timeout: TimeSpan.FromMinutes(1));

        PlateSolveResult result = await new ClaimedScaleFallbackSolver(inner, clock).SolveAsync(request);

        Assert.Equal(2, inner.Calls);
        AssertFitForScreen(result.Message);
        Assert.Contains("without a scale", result.Message);
        Assert.Contains("At the entered focal length's scale:", result.Detail);
        Assert.Contains("Without a scale:", result.Detail);
        Assert.Equal(2, CountOf(result.Detail!, AstapOutput));
    }

    [Fact]
    public void TheDetail_SurvivesBeingAttributedToASolver()
    {
        PlateSolveResult result = AstapPlateSolver.ClassifyExitCode(1, AstapOutput).From("ASTAP");

        Assert.Equal(AstapOutput, result.Detail);
    }

    [Theory]
    [InlineData("Not enough stars.", "Not enough stars.")]
    [InlineData("  padded  ", "padded")]
    [InlineData("two\nlines", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void QuotableLine_TakesOneShortLineOnly(string? text, string? expected) =>
        Assert.Equal(expected, PlateSolveResult.QuotableLine(text));

    [Fact]
    public void QuotableLine_RefusesALongLine() =>
        Assert.Null(PlateSolveResult.QuotableLine(new string('x', 121)));

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private sealed class AlwaysAstapNoMatch : ISolver
    {
        public int Calls { get; private set; }

        public string Name => "ASTAP";

        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(AstapPlateSolver.ClassifyExitCode(1, AstapOutput));
        }
    }
}
