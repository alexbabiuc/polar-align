using FreePolarAlign.App.ViewModels;
using FreePolarAlign.Core.Engine;
using FreePolarAlign.Session;
using Xunit;

namespace FreePolarAlign.Tests.App;

/// <summary>
/// The live reading (D29) as the window shows it: which way to turn each
/// knob, when the figures are live, and that an unusable reading is never
/// left on screen as if it were one.
/// </summary>
public class LiveReadingPresentationTests
{
    private static AlignmentEstimate Estimate(double altitude, double azimuth, double total) =>
        new(altitude, azimuth, total, 0.2, 0.3, 0.25, 0.5);

    private static UiState After(UiState state, params EngineEvent[] events)
    {
        foreach (EngineEvent engineEvent in events)
        {
            state = EngineEventReducer.Apply(state, engineEvent);
        }

        return state;
    }

    private static UiState Live() => After(
        UiState.Initial(),
        new SessionStartedEvent(new SessionConfiguration(5, 60.0)),
        new AlignmentUpdatedEvent(Estimate(30.0, -25.0, 34.8)),
        new TrackingStartedEvent("Turn the bolts.", IsUsableGeometry: true));

    // ---- Which way to turn ----

    /// <summary>
    /// The error is the axis minus the pole, so an axis that is too high reads
    /// positive and is corrected by lowering it. Getting this backwards doubles
    /// the error with every turn, which is why it is spelled out rather than
    /// left to the sign.
    /// </summary>
    [Theory]
    [InlineData(12.34, "Lower the altitude by 12.3'")]
    [InlineData(-7.0, "Raise the altitude by 7.0'")]
    [InlineData(0.01, "Altitude: leave it where it is")]
    public void TheAltitudeCorrection_IsTheOppositeOfTheError(double error, string expected) =>
        Assert.Equal(expected, AlignmentFormatting.AltitudeCorrection(error));

    /// <summary>
    /// A positive azimuth error is the axis clockwise of the pole seen from
    /// above, which is to the right of someone facing the pole in either
    /// hemisphere -- so it is always corrected to the left, and only the compass
    /// word changes: west in the north, east in the south.
    /// </summary>
    [Theory]
    [InlineData(8.1, 45.0, "Move the azimuth left (west) by 8.1'")]
    [InlineData(-8.1, 45.0, "Move the azimuth right (east) by 8.1'")]
    [InlineData(8.1, -33.0, "Move the azimuth left (east) by 8.1'")]
    [InlineData(-8.1, -33.0, "Move the azimuth right (west) by 8.1'")]
    [InlineData(0.0, 45.0, "Azimuth: leave it where it is")]
    public void TheAzimuthCorrection_IsLeftOrRightFacingThePole(double error, double latitude, string expected) =>
        Assert.Equal(expected, AlignmentFormatting.AzimuthCorrection(error, latitude));

    // ---- The hand-over, and readings ----

    /// <summary>
    /// The sweep's answer stays on screen when the live reading takes over, and
    /// each frame then replaces it. The instruction becomes the guidance.
    /// </summary>
    [Fact]
    public void TheHandOver_KeepsTheSweepsAnswerUntilTheFirstReading()
    {
        UiState live = Live();

        Assert.True(live.IsLiveReading);
        Assert.True(live.SessionActive);
        Assert.Equal(34.8, live.CurrentEstimate!.TotalErrorArcminutes);
        Assert.Equal("Turn the bolts.", live.GuidanceInstruction);

        UiState read = After(live, new AlignmentTrackedEvent(Estimate(12.0, -9.0, 14.2), -18.0, 16.0, IsReliable: true));

        Assert.Equal(14.2, read.CurrentEstimate!.TotalErrorArcminutes);
        Assert.Equal(-18.0, read.AppliedAltitudeArcminutes);
        Assert.Equal(16.0, read.AppliedAzimuthArcminutes);
        Assert.Equal(SamplingActivity.Live, read.Sampling.Activity);
    }

    /// <summary>
    /// A reading the geometry cannot support clears the figures and says why,
    /// rather than leaving the last good ones up while the user goes on turning
    /// by them (D11, D17). The next good reading brings them back.
    /// </summary>
    [Fact]
    public void AnUnreliableReading_ClearsTheFiguresAndSaysWhy()
    {
        UiState state = After(
            Live(),
            new AlignmentTrackedEvent(Estimate(12.0, -9.0, 14.2), -18.0, 16.0, IsReliable: true),
            new AlignmentTrackedEvent(Estimate(11.0, -8.0, 13.0), -19.0, 17.0, IsReliable: false, "Point nearer the meridian."));

        Assert.Null(state.CurrentEstimate);
        Assert.Null(state.AppliedAltitudeArcminutes);
        Assert.Equal("Point nearer the meridian.", state.WithheldReason);

        UiState recovered = After(state, new AlignmentTrackedEvent(Estimate(10.0, -7.0, 12.0), -20.0, 18.0, IsReliable: true));

        Assert.Equal(12.0, recovered.CurrentEstimate!.TotalErrorArcminutes);
        Assert.Null(recovered.WithheldReason);
    }

    /// <summary>
    /// Solving every frame must not make the sampling line cycle through
    /// "next solve" and "solving" several times a second; it says what the
    /// readings and failures say.
    /// </summary>
    [Fact]
    public void PerFrameSolves_DoNotChurnTheSamplingLine()
    {
        UiState read = After(Live(), new AlignmentTrackedEvent(Estimate(12.0, -9.0, 14.2), -18.0, 16.0, IsReliable: true));

        UiState next = After(
            read,
            new SolveScheduledEvent(SampleTrigger.Tracking, TimeSpan.Zero),
            new SolveStartedEvent(SampleTrigger.Tracking, "frame.fits"));

        Assert.Equal(SamplingActivity.Live, next.Sampling.Activity);
        Assert.Equal(read.Log.Count, next.Log.Count);
    }

    /// <summary>
    /// Stopping keeps the last figure on screen, as the roadmap asks of any
    /// result, but it is no longer live and stops saying it is.
    /// </summary>
    [Fact]
    public void Stopping_KeepsTheLastReadingButNotAsLive()
    {
        UiState state = After(
            Live(),
            new AlignmentTrackedEvent(Estimate(0.5, 0.4, 0.6), -29.5, 25.4, IsReliable: true),
            new SessionCompletedEvent());

        Assert.False(state.IsLiveReading);
        Assert.Equal(0.6, state.CurrentEstimate!.TotalErrorArcminutes);
    }

    /// <summary>A restart or a new sweep is not live: its figures come from samples again.</summary>
    [Fact]
    public void ARestartOrANewSequence_EndsTheLiveReading()
    {
        Assert.False(After(Live(), new SequenceRestartedEvent("Declination moved.")).IsLiveReading);
        Assert.False(After(Live(), new SessionStartedEvent(new SessionConfiguration(5, 60.0))).IsLiveReading);
    }

    /// <summary>
    /// Every reading is logged, because the morning after it is the record of
    /// what each bolt turn did; the solve bookkeeping around it is not.
    /// </summary>
    [Fact]
    public void Readings_AreLogged_TheirSolvesAreNot()
    {
        Assert.Contains("azimuth +16.00'", EngineEventNarrator.Describe(
            new AlignmentTrackedEvent(Estimate(12.0, -9.0, 14.2), -18.0, 16.0, IsReliable: true)).Message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, EngineEventNarrator.Describe(new SolveStartedEvent(SampleTrigger.Tracking, "f.fits")).Message);
        Assert.Equal(string.Empty, EngineEventNarrator.Describe(new SolveScheduledEvent(SampleTrigger.Tracking, TimeSpan.Zero)).Message);
        Assert.Equal(LogSeverity.Info, EngineEventNarrator.Describe(new SolveFailedEvent(SampleTrigger.Tracking, "blurred", 3)).Severity);
    }
}
