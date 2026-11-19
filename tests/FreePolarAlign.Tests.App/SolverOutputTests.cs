using FreePolarAlign.App.ViewModels;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Session;
using Xunit;

namespace FreePolarAlign.Tests.App;

/// <summary>
/// D46: a solver's own output is kept whole in the log and left off the
/// panels, which say it is in the log instead. Pinned with what ASTAP printed
/// for one failed attempt on 2026-11-05, 111 lines that used to fill the
/// sampling panel.
/// </summary>
public class SolverOutputTests
{
    internal static readonly string AstapOutput = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "astap_no_match_20261105.txt")).Trim();

    private const string Sentence = "ASTAP could not match the field against its star database.";

    private static readonly SolveFailedEvent Failed =
        new(SampleTrigger.Periodic, Sentence, 3, Solver: "ASTAP", Detail: AstapOutput);

    [Fact]
    public void TheLog_KeepsTheSolversOutputWhole()
    {
        NarratedEvent narrated = EngineEventNarrator.Describe(Failed);

        Assert.StartsWith($"Solve failed (ASTAP, 3 in a row): {Sentence}", narrated.Message);
        Assert.Contains(AstapOutput, narrated.Message);
    }

    [Fact]
    public void TheSamplingPanel_ShowsTheSentenceAndPointsAtTheLog()
    {
        UiState state = EngineEventReducer.Apply(
            UiState.Initial() with { SessionActive = true }, Failed);

        string? text = AlignmentFormatting.SamplingStatus(state, DateTimeOffset.UnixEpoch);

        Assert.Equal(
            $"Last solve failed (3 in a row): {Sentence} {AlignmentFormatting.SolverOutputInLog}", text);
    }

    /// <summary>A failure with nothing more to say does not send anyone to the log for it.</summary>
    [Fact]
    public void WithoutSolverOutput_ThereIsNoPointerToTheLog()
    {
        UiState state = EngineEventReducer.Apply(
            UiState.Initial() with { SessionActive = true },
            new SolveFailedEvent(SampleTrigger.Periodic, "No stars were detected in the image.", 1));

        string? text = AlignmentFormatting.SamplingStatus(state, DateTimeOffset.UnixEpoch);

        Assert.DoesNotContain(AlignmentFormatting.SolverOutputInLog, text);
    }

    [Fact]
    public void ASyncWhoseSolveFailed_LogsTheOutputAndLeavesItOffThePanel()
    {
        var failed = new MountSyncFailedEvent(MountSyncStage.Solve, Sentence, Solver: "ASTAP", Detail: AstapOutput);

        Assert.Contains(AstapOutput, EngineEventNarrator.Describe(failed).Message);

        UiState state = EngineEventReducer.Apply(UiState.Initial(), failed);
        Assert.Equal(Sentence, state.Sync!.Detail);
        Assert.True(state.Sync.SolverOutputInLog);
    }
}
