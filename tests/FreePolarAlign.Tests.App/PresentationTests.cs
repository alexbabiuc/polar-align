using FreePolarAlign.App.ViewModels;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Session;
using Xunit;

namespace FreePolarAlign.Tests.App;

/// <summary>
/// The UI's decisions, tested where they live: a pure reducer over the engine's
/// event stream, plus pure formatting. The window itself is not tested and
/// cannot be here — but nothing that decides *what* the user is told lives in
/// the window.
///
/// These are not cosmetic checks. The two ways this UI could seriously mislead
/// someone are judging success on the wrong number, and leaving a stale estimate
/// on screen after the engine has withheld one, so both are pinned down.
/// </summary>
public class PresentationTests
{
    private static AlignmentEstimate Estimate(
        double altitude, double azimuth, double total, double residualRms = 0.5) =>
        new(altitude, azimuth, total, 0.2, 0.3, 0.25, residualRms);

    private static UiState Fresh() => UiState.Initial(45.0);

    private static UiState After(UiState state, params EngineEvent[] events)
    {
        foreach (EngineEvent engineEvent in events)
        {
            state = EngineEventReducer.Apply(state, engineEvent);
        }

        return state;
    }

    // ---- Success indication ----

    /// <summary>
    /// Success is judged on the total error alone. Neither bolt figure is the
    /// quantity the threshold refers to, and their sum is not either — the total
    /// is a great-circle angle, and the azimuth figure is an angle about the
    /// vertical that subtends less on the sky (D12).
    /// </summary>
    [Fact]
    public void SuccessIsJudgedOnTheTotal_NotTheComponents()
    {
        // Both components under ten, total over it: not good enough.
        Assert.False(AlignmentFormatting.IsGoodEnoughToStop(Estimate(9.0, 9.0, 12.7)));

        // A component over ten, total under it: good enough.
        Assert.True(AlignmentFormatting.IsGoodEnoughToStop(Estimate(2.0, 14.0, 9.5)));

        // And the sum is not the criterion either.
        Assert.True(AlignmentFormatting.IsGoodEnoughToStop(Estimate(6.0, 6.0, 8.0)));
    }

    [Theory]
    [InlineData(9.99, true)]
    [InlineData(10.0, false)]
    [InlineData(10.01, false)]
    public void ThresholdIsStrictlyBelowTenArcminutes(double total, bool expected)
    {
        Assert.Equal(expected, AlignmentFormatting.IsGoodEnoughToStop(Estimate(1.0, 1.0, total)));
    }

    /// <summary>
    /// The roadmap requires the actual figure to stay visible, including after
    /// success. A tick that replaces the number would hide exactly the
    /// information a user needs to decide whether to keep going.
    /// </summary>
    [Fact]
    public void SuccessfulEstimate_IsStillAvailableToDisplay()
    {
        UiState state = After(Fresh(), new AlignmentUpdatedEvent(Estimate(1.0, 1.5, 2.1)));

        Assert.NotNull(state.CurrentEstimate);
        Assert.True(AlignmentFormatting.IsGoodEnoughToStop(state.CurrentEstimate!));
        Assert.Equal(2.1, state.CurrentEstimate!.TotalErrorArcminutes, precision: 6);
    }

    // ---- Withholding: the D11 case ----

    /// <summary>
    /// When the engine withholds a result the previous estimate must not remain
    /// on screen as though it were current. A confident wrong number the user
    /// acts on is the worst outcome this project can produce, and a stale one is
    /// exactly that.
    /// </summary>
    [Fact]
    public void WithheldResult_ClearsTheEstimateAndKeepsTheReason()
    {
        UiState state = After(
            Fresh(),
            new AlignmentUpdatedEvent(Estimate(30.0, -25.0, 34.8)),
            new AlignmentWithheldEvent("Declination probably moved between captures."));

        Assert.Null(state.CurrentEstimate);
        Assert.Equal("Declination probably moved between captures.", state.WithheldReason);
    }

    [Fact]
    public void FreshEstimate_ClearsAPreviousWithheldReason()
    {
        UiState state = After(
            Fresh(),
            new AlignmentWithheldEvent("Meridian crossed."),
            new AlignmentUpdatedEvent(Estimate(4.0, 3.0, 5.0)));

        Assert.NotNull(state.CurrentEstimate);
        Assert.Null(state.WithheldReason);
    }

    [Fact]
    public void SessionFault_IsSurfacedWithItsReason()
    {
        UiState state = After(Fresh(), new SessionFaultedEvent("The mount disconnected."));

        Assert.Equal("The mount disconnected.", state.FaultReason);
        Assert.False(state.SessionActive);
    }

    /// <summary>
    /// A failed capture is not a failed session, and the UI must not present it
    /// as one — the sequence usually continues.
    /// </summary>
    [Fact]
    public void CaptureFailure_IsDistinctFromASessionFault()
    {
        UiState state = After(
            Fresh(),
            new SessionStartedEvent(new SessionConfiguration(45.0, 15.0, 200.0, 3, TimeSpan.FromSeconds(2))),
            new CaptureFailedEvent(2, "Could not solve (NoMatchFound).", WillRetry: true));

        Assert.Null(state.FaultReason);
        Assert.NotNull(state.CaptureWarning);
        Assert.True(state.SessionActive);
    }

    // ---- Manual mode ----

    [Fact]
    public void ManualAction_IsSurfacedAndCleared()
    {
        UiState prompted = After(
            Fresh(),
            new SessionStartedEvent(new SessionConfiguration(45.0, 15.0, 200.0, 3, TimeSpan.FromSeconds(2))),
            new ManualActionRequiredEvent("Rotate the mount in RA to about 40° west. Do not touch declination.", 40.0));

        Assert.True(prompted.AwaitingManualAction);
        Assert.Contains("declination", prompted.ManualInstruction!, StringComparison.OrdinalIgnoreCase);

        UiState proceeded = After(prompted, new PointCapturedEvent(new CapturePoint(1, 10.0, 70.0, DateTime.UtcNow)));
        Assert.False(proceeded.AwaitingManualAction);
    }

    // ---- Formatting and hemisphere ----

    [Fact]
    public void SignedFigures_KeepTheirSign_BecauseItSaysWhichWayToTurn()
    {
        string positive = AlignmentFormatting.FormatSignedWithSigma(12.3, 0.4);
        string negative = AlignmentFormatting.FormatSignedWithSigma(-12.3, 0.4);

        Assert.NotEqual(positive, negative);
        Assert.Contains("12.3", positive);
        Assert.Contains("12.3", negative);
    }

    [Fact]
    public void TotalIsFormattedUnsigned_SinceAMagnitudeHasNoDirection()
    {
        Assert.Equal(
            AlignmentFormatting.FormatMagnitudeWithSigma(12.3, 0.4),
            AlignmentFormatting.FormatMagnitudeWithSigma(-12.3, 0.4));
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(0.1)]
    [InlineData(-33.9)]
    [InlineData(-55.0)]
    public void HemisphereIsReportedForBothSidesOfTheEquator(double latitude)
    {
        string hemisphere = AlignmentFormatting.Hemisphere(latitude);

        Assert.False(string.IsNullOrWhiteSpace(hemisphere));
        Assert.Equal(latitude >= 0, hemisphere.Contains("north", StringComparison.OrdinalIgnoreCase));
    }

    // ---- The log ----

    [Fact]
    public void EveryEventIsLogged_SoASequenceCanBeReconstructed()
    {
        UiState state = After(
            Fresh(),
            new SessionStartedEvent(new SessionConfiguration(45.0, 15.0, 200.0, 3, TimeSpan.FromSeconds(2))),
            new TargetSelectedEvent(70.0, true, 6, 70.0),
            new PointCapturedEvent(new CapturePoint(1, 10.0, 70.0, DateTime.UtcNow)),
            new AlignmentWithheldEvent("Meridian crossed."),
            new SessionCompletedEvent());

        Assert.Equal(5, state.Log.Count);
        Assert.Contains(state.Log, entry => entry.Contains("WITHHELD", StringComparison.Ordinal));
    }
}
