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

    private static UiState Fresh() => UiState.Initial();

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
    /// A failed solve is not a failed session, and the UI must not present it
    /// as one. In the live loop it is not even unusual (D26): most failures are
    /// frames taken while the mount was being turned, so it neither faults
    /// anything nor clears an estimate that the failure did not touch.
    /// </summary>
    [Fact]
    public void SolveFailure_IsNeitherAFaultNorAReasonToDropTheEstimate()
    {
        UiState state = After(
            Fresh(),
            new SessionStartedEvent(new SessionConfiguration(6, 70.0), SequenceMode.Unconnected),
            new AlignmentUpdatedEvent(Estimate(4.0, 3.0, 5.0)),
            new SolveFailedEvent(SampleTrigger.Periodic, "Could not solve (NoMatchFound).", ConsecutiveFailures: 1));

        Assert.Null(state.FaultReason);
        Assert.NotNull(state.CurrentEstimate);
        Assert.True(state.SessionActive);
        Assert.Equal(SamplingActivity.Failed, state.Sampling.Activity);
    }

    // ---- Guidance ----

    /// <summary>
    /// Without a mount the instruction is the whole of the guidance (D10), and
    /// it is spent once the sample it asked for has been taken.
    /// </summary>
    [Fact]
    public void AManualInstruction_IsShownUntilItsSampleIsTaken()
    {
        UiState prompted = After(
            Fresh(),
            new SessionStartedEvent(new SessionConfiguration(6, 70.0), SequenceMode.Unconnected),
            new ManualActionRequiredEvent("Rotate the mount in RA to about 40° west. Do not touch declination.", 40.0));

        Assert.Contains("declination", prompted.GuidanceInstruction!, StringComparison.OrdinalIgnoreCase);

        UiState proceeded = After(prompted, new PointCapturedEvent(new CapturePoint(1, 10.0, 70.0, DateTime.UtcNow)));
        Assert.Null(proceeded.GuidanceInstruction);
    }

    /// <summary>
    /// Whichever of the two instruction events spoke last is the one shown. A
    /// driven sequence says what to do only through its proposal.
    /// </summary>
    [Fact]
    public void AProposal_IsGuidanceToo()
    {
        UiState state = After(
            Fresh(),
            new SessionStartedEvent(new SessionConfiguration(6, 70.0)),
            new ManualActionRequiredEvent("Set declination to 60°.", 0.0),
            new SlewProposedEvent(2, 6, 160.0, 68.0, 26.0, 75.0, RequiresMotion: true, "Slew to 26° east."));

        Assert.Equal("Slew to 26° east.", state.GuidanceInstruction);
    }

    /// <summary>
    /// A proposal that needs no slew is not "without moving" when there is no
    /// mount to slew: the user is about to turn the telescope by hand, and being
    /// told nothing will move is exactly wrong.
    /// </summary>
    [Fact]
    public void AnUnconnectedProposal_DoesNotSayNothingWillMove()
    {
        UiState state = After(
            Fresh(),
            new SessionStartedEvent(new SessionConfiguration(6, 70.0), SequenceMode.Unconnected),
            new SlewProposedEvent(1, 6, 150.0, 60.0, -35.0, 50.0, RequiresMotion: false, "Set declination to 60°."));

        Assert.DoesNotContain("without moving", state.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSequenceModeIsRecordedAtStart()
    {
        UiState state = After(
            Fresh(),
            new SessionStartedEvent(new SessionConfiguration(6, 70.0), SequenceMode.Observed));

        Assert.Equal(SequenceMode.Observed, state.SequenceMode);
    }

    // ---- Restarts ----

    /// <summary>
    /// A restart discarded the samples, and a withheld result disowned the fit.
    /// Either way the number on screen no longer stands for anything, and
    /// leaving it there is the stale-figure failure D11 exists to prevent,
    /// carried into the UI.
    /// </summary>
    [Theory]
    [MemberData(nameof(EventsThatInvalidateTheEstimate))]
    public void AnEventThatInvalidatesTheFit_ClearsTheEstimate(EngineEvent invalidating)
    {
        UiState state = After(
            Fresh(),
            new SessionStartedEvent(new SessionConfiguration(6, 70.0)),
            new PointCapturedEvent(new CapturePoint(3, 150.0, 68.0, DateTime.UtcNow)),
            new AlignmentUpdatedEvent(Estimate(4.0, 3.0, 5.0)),
            invalidating);

        Assert.Null(state.CurrentEstimate);
    }

    public static TheoryData<EngineEvent> EventsThatInvalidateTheEstimate() => new()
    {
        new SequenceRestartedEvent("The mount moved 3.2' in declination."),
        new AlignmentWithheldEvent("Declination probably moved between captures."),
    };

    /// <summary>
    /// And the count goes with the samples. "3 / 6" beside a restarted sequence
    /// would imply the discarded samples still count towards the answer.
    /// </summary>
    [Fact]
    public void ARestart_ResetsTheSampleCount_AndIsLoggedAsAWarning()
    {
        UiState state = After(
            Fresh(),
            new SessionStartedEvent(new SessionConfiguration(6, 70.0)),
            new PointCapturedEvent(new CapturePoint(3, 150.0, 68.0, DateTime.UtcNow)),
            new SequenceRestartedEvent("The mount moved 3.2' in declination."));

        Assert.Equal(0, state.CapturedPointCount);
        Assert.True(state.SessionActive);
        Assert.Contains(state.Log, entry => entry.Contains("WARN", StringComparison.Ordinal) &&
                                            entry.Contains("3.2' in declination", StringComparison.Ordinal));
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
            new SessionStartedEvent(new SessionConfiguration(6, 70.0)),
            new TargetSelectedEvent(70.0, true, 6, 70.0),
            new PointCapturedEvent(new CapturePoint(1, 10.0, 70.0, DateTime.UtcNow)),
            new AlignmentWithheldEvent("Meridian crossed."),
            new SessionCompletedEvent());

        Assert.Equal(5, state.Log.Count);
        Assert.Contains(state.Log, entry => entry.Contains("Result withheld: Meridian crossed.", StringComparison.Ordinal));

        // Severity is on the line, so the file and the screen can both be
        // skimmed for the entries that matter without reading every one.
        Assert.Contains(state.Log, entry => entry.Contains("ERROR", StringComparison.Ordinal));
    }

    /// <summary>
    /// The site is logged with every digit it was given. It is the one input the
    /// software cannot check for itself, and the first thing to re-examine the
    /// morning after a result that looked wrong -- so a log that rounded it to
    /// two decimal places would have thrown away the evidence.
    /// </summary>
    [Fact]
    public void TheSiteIsLoggedPrecisely_BecauseItCannotBeRecoveredLater()
    {
        UiState state = After(Fresh(), new SiteConfiguredEvent(51.47733, -0.00139, 47.0));

        Assert.Contains(state.Log, entry => entry.Contains("51.47733", StringComparison.Ordinal));
        Assert.Contains(state.Log, entry => entry.Contains("-0.00139", StringComparison.Ordinal));
    }

    /// <summary>
    /// Polling the mount happens every couple of seconds, so it must not put a
    /// line in the log -- an all-night session would otherwise bury every event
    /// that mattered under thousands of position reports.
    /// </summary>
    [Fact]
    public void MountStatusPolling_DoesNotFillTheLog()
    {
        UiState state = Fresh();

        for (int i = 0; i < 50; i++)
        {
            state = EngineEventReducer.Apply(
                state, new MountStatusEvent(10.0, 70.0, MountTrackingState.Tracking, MeridianSide.West));
        }

        Assert.Empty(state.Log);
        Assert.Equal(70.0, state.MountDecDegrees);
    }

    // ---- Devices ----

    [Fact]
    public void ConnectingACamera_ShowsWhatTheSoftwareIsActuallyUsing()
    {
        UiState state = After(Fresh(), new DeviceConnectedEvent(
            DeviceKind.Camera, "ASCOM", "ASCOM.Simulator.Camera", "Camera Simulator", "ASCOM / v2",
            new CameraDescription(3.76, 6248, 4176)));

        Assert.True(state.Camera.IsConnected);
        Assert.Equal("Camera Simulator", state.Camera.Name);
        Assert.Equal(3.76, state.CameraPixelSizeMicrons);
        Assert.Equal(6248, state.CameraWidthPixels);
    }

    /// <summary>
    /// Disconnecting the camera clears its pixel size. A pitch left over from a
    /// camera that is no longer attached would make every plate scale shown
    /// afterwards a fiction, and the scale is what the solver is hinted with.
    /// </summary>
    [Fact]
    public void DisconnectingACamera_ClearsItsPixelSize()
    {
        UiState state = After(
            Fresh(),
            new DeviceConnectedEvent(
                DeviceKind.Camera, "ASCOM", "cam", "Camera", "driver", new CameraDescription(3.76, 6248, 4176)),
            new DeviceDisconnectedEvent(DeviceKind.Camera));

        Assert.False(state.Camera.IsConnected);
        Assert.Null(state.CameraPixelSizeMicrons);
        Assert.Equal(0, state.CameraWidthPixels);
    }

    /// <summary>
    /// A mount that dropped off the bus carries its reason, and loses its stale
    /// position: a coordinate readout that keeps showing where the mount was when
    /// it vanished is indistinguishable from one showing where it is.
    /// </summary>
    [Fact]
    public void AMountThatDropsOut_LosesItsStalePosition()
    {
        UiState state = After(
            Fresh(),
            new DeviceConnectedEvent(DeviceKind.Mount, "ASCOM", "mount", "Mount", "driver", CanSlew: true),
            new MountStatusEvent(120.0, 45.0, MountTrackingState.Tracking, MeridianSide.West),
            new DeviceDisconnectedEvent(DeviceKind.Mount, "Lost contact with the mount: the port closed."));

        Assert.False(state.Mount.IsConnected);
        Assert.Contains("port closed", state.Mount.Problem!, StringComparison.Ordinal);
        Assert.Null(state.MountRaDegrees);
        Assert.Equal(MountTrackingState.Unknown, state.MountTracking);
        Assert.False(state.MountCanSlew);
    }

    /// <summary>
    /// Tracking has three states, not two. A driver that does not report it must
    /// read as unknown: someone who believes the drive is stopped when it is
    /// running will misread every number that follows.
    /// </summary>
    [Fact]
    public void EveryTrackingState_ReadsAsSomethingDifferent()
    {
        string[] texts = Enum.GetValues<MountTrackingState>()
            .Select(AlignmentFormatting.Tracking)
            .ToArray();

        Assert.DoesNotContain(texts, string.IsNullOrWhiteSpace);

        // In particular "unknown" must not collapse onto "stopped": they are
        // indistinguishable in a boolean and mean very different things to
        // someone deciding whether the numbers they are watching should be
        // changing on their own.
        Assert.Equal(texts.Length, texts.Distinct(StringComparer.Ordinal).Count());
    }

    // ---- Site ----

    /// <summary>
    /// The site is null until confirmed rather than defaulting to zero, which
    /// would put the arithmetic in the Gulf of Guinea and produce a latitude
    /// error of the site's full latitude (D19).
    /// </summary>
    [Fact]
    public void ASiteIsNotAssumed_UntilItIsConfirmed()
    {
        Assert.False(Fresh().IsSiteConfigured);
        Assert.Null(Fresh().SiteLatitudeDegrees);

        UiState configured = After(Fresh(), new SiteConfiguredEvent(51.5, -0.1, 50.0));
        Assert.True(configured.IsSiteConfigured);
        Assert.Equal(51.5, configured.SiteLatitudeDegrees);
    }

    [Fact]
    public void ADisagreeingMountSite_IsSurfacedRatherThanResolved()
    {
        UiState state = After(Fresh(), new SiteConfiguredEvent(
            51.5, -0.1, 50.0, "The mount reports its site as 0.0000°, 0.0000°, 0 m."));

        // The confirmed figure is the one in use, and the disagreement is shown
        // alongside it rather than replacing it.
        Assert.Equal(51.5, state.SiteLatitudeDegrees);
        Assert.NotNull(state.SiteDisagreement);
    }

    // ---- Proposals (D18) ----

    /// <summary>
    /// A proposal that needs no movement must not be presented as one that does.
    /// Offering to slew when nothing is going to move sends the user out to check
    /// the sky for an obstruction that cannot matter.
    /// </summary>
    [Fact]
    public void AProposalNeedingNoMovement_IsNotPresentedAsASlew()
    {
        UiState state = After(Fresh(), new SlewProposedEvent(
            1, 6, 150.0, 68.0, 12.0, 80.0, RequiresMotion: false, "Capture here first."));

        Assert.NotNull(state.Proposal);
        Assert.False(state.Proposal!.RequiresMotion);
        Assert.Contains("without moving", state.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And one that does need movement has to say plainly that nothing has
    /// happened yet, since the entire point of D18 is that the user gets to look
    /// up before the telescope swings.
    /// </summary>
    [Fact]
    public void AProposalNeedingMovement_SaysNothingHasMovedYet()
    {
        UiState state = After(Fresh(), new SlewProposedEvent(
            2, 6, 160.0, 68.0, 26.0, 75.0, RequiresMotion: true, "Slew to 26° west."));

        Assert.True(state.Proposal!.RequiresMotion);
        Assert.Contains("nothing will move", state.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The proposal is cleared once its capture lands, so a stale suggestion
    /// cannot be confirmed twice.
    /// </summary>
    [Fact]
    public void CapturingAPoint_ClearsTheProposalItCameFrom()
    {
        UiState state = After(
            Fresh(),
            new SlewProposedEvent(2, 6, 160.0, 68.0, 26.0, 75.0, true, "Slew."),
            new PointCapturedEvent(new CapturePoint(2, 160.1, 68.0, DateTime.UtcNow)));

        Assert.Null(state.Proposal);
    }

    [Fact]
    public void FaultingOrFinishing_ClearsAnyPendingProposal()
    {
        UiState faulted = After(
            Fresh(),
            new SlewProposedEvent(2, 6, 160.0, 68.0, 26.0, 75.0, true, "Slew."),
            new SessionFaultedEvent("The mount disconnected."));

        UiState finished = After(
            Fresh(),
            new SlewProposedEvent(2, 6, 160.0, 68.0, 26.0, 75.0, true, "Slew."),
            new SessionCompletedEvent());

        Assert.Null(faulted.Proposal);
        Assert.Null(finished.Proposal);
    }

    /// <summary>
    /// A re-anchor discarded the earlier captures, so the captured count must go
    /// back to zero with them. Leaving "3 / 6 captured" on screen beside a
    /// restarted sequence would misrepresent how much work remains and, worse,
    /// imply the discarded frames still count towards the answer.
    /// </summary>
    [Fact]
    public void ReanchoringResetsTheCapturedCount()
    {
        UiState state = After(
            Fresh(),
            new SessionStartedEvent(new SessionConfiguration(6, 70.0)),
            new PointCapturedEvent(new CapturePoint(3, 150.0, 68.0, DateTime.UtcNow)),
            new SlewConfirmedEvent(150.0, 60.0, WasOverridden: true, ReanchoredReason: "Different declination."));

        Assert.Equal(0, state.CapturedPointCount);
    }

    /// <summary>
    /// An ordinary confirmation must not reset it, or the progress readout would
    /// jump back to zero on every point.
    /// </summary>
    [Fact]
    public void AnOrdinaryConfirmation_LeavesTheCapturedCountAlone()
    {
        UiState state = After(
            Fresh(),
            new PointCapturedEvent(new CapturePoint(3, 150.0, 68.0, DateTime.UtcNow)),
            new SlewConfirmedEvent(160.0, 68.0, WasOverridden: false));

        Assert.Equal(3, state.CapturedPointCount);
    }

    // ---- Refusals ----

    /// <summary>
    /// A refused command is not a fault and does not invalidate anything already
    /// measured. Clearing the estimate here would throw away a good answer
    /// because a button was pressed at the wrong moment.
    /// </summary>
    [Fact]
    public void ARefusedCommand_DoesNotDiscardAGoodEstimate()
    {
        UiState state = After(
            Fresh(),
            new AlignmentUpdatedEvent(Estimate(2.0, 1.5, 2.5)),
            new CommandRejectedEvent("Cannot disconnect the camera while a sequence is running."));

        Assert.NotNull(state.CurrentEstimate);
        Assert.Null(state.FaultReason);
        Assert.NotNull(state.RejectionReason);
    }

    [Fact]
    public void ARefusal_IsClearedByTheNextThingThatActuallyHappens()
    {
        UiState state = After(
            Fresh(),
            new CommandRejectedEvent("Confirm the observing site first."),
            new SiteConfiguredEvent(51.5, -0.1, 50.0));

        Assert.Null(state.RejectionReason);
    }

    // ---- Equipment ----

    /// <summary>
    /// A measured focal length and an entered one must not read the same. When a
    /// solve keeps failing, "1000 mm (as entered)" points at the number to
    /// double-check and "1000 mm (measured)" rules it out.
    /// </summary>
    [Fact]
    public void AMeasuredFocalLength_ReadsDifferentlyFromAnEnteredOne()
    {
        Assert.NotEqual(
            AlignmentFormatting.FocalLength(1000.0, isSolved: true),
            AlignmentFormatting.FocalLength(1000.0, isSolved: false));

        Assert.Contains("measured", AlignmentFormatting.FocalLength(1000.0, true), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClearingTheFocalLength_SaysWhatThatCosts()
    {
        UiState state = After(Fresh(), new EquipmentConfiguredEvent(null, false, null, null));

        Assert.Null(state.FocalLengthMillimetres);
        Assert.Contains(state.Log, entry => entry.Contains("blind", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A solve replaces the user's figure and says so, which is the point of
    /// asking for it only once.
    /// </summary>
    [Fact]
    public void ASolveReplacesTheEnteredFocalLength_AndMarksItMeasured()
    {
        UiState state = After(
            Fresh(),
            new EquipmentConfiguredEvent(1000.0, false, 0.78, 0.5),
            new EquipmentConfiguredEvent(1023.4, true, 0.76, 0.51));

        Assert.Equal(1023.4, state.FocalLengthMillimetres);
        Assert.True(state.IsFocalLengthSolved);
    }

    // ---- The captured frame ----

    /// <summary>
    /// The frame stays the latest when the solve that followed it failed. That
    /// is the case the preview exists for: "no stars detected" is answered by
    /// looking at the picture, not by re-reading the message.
    /// </summary>
    [Fact]
    public void AFrameSurvivesTheFailureOfTheSolveThatFollowedIt()
    {
        UiState state = After(
            Fresh(),
            new FrameCapturedEvent("/tmp/capture-0001.fits", DateTime.UtcNow, TimeSpan.FromSeconds(2)),
            new SolveFailedEvent(SampleTrigger.SlewEnded, "Could not solve (NoStarsDetected).", 1));

        Assert.Equal("/tmp/capture-0001.fits", state.LatestFramePath);
        Assert.NotNull(state.LatestFrameMidpointUtc);
    }

    /// <summary>
    /// The path of a frame that was solved is logged. It is the only way to find
    /// the image again afterwards, and "which frame was point 3" is the first
    /// question anyone asks about a sequence that went wrong.
    /// </summary>
    [Fact]
    public void ASolvedFramesPathIsLogged_SoTheImageCanBeFoundAgain()
    {
        UiState state = After(
            Fresh(),
            new SolveStartedEvent(SampleTrigger.SlewEnded, "/tmp/fpa/capture-0003.fits"));

        Assert.Contains(state.Log, entry => entry.Contains("capture-0003.fits", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every frame, though, is neither logged nor put on the status line. The
    /// camera runs continuously (D26), and at 0.1 s a line per frame would bury
    /// the log and wipe every status message a tenth of a second after it
    /// appeared.
    /// </summary>
    [Fact]
    public void ContinuousFrames_TouchNeitherTheLogNorTheStatusLine()
    {
        UiState before = After(Fresh(), new CommandRejectedEvent("Confirm the observing site first."));
        UiState state = before;

        for (int i = 0; i < 50; i++)
        {
            state = EngineEventReducer.Apply(
                state, new FrameCapturedEvent($"/tmp/frame-{i}.fits", DateTime.UtcNow, TimeSpan.FromSeconds(0.1)));
        }

        Assert.Equal(before.Log.Count, state.Log.Count);
        Assert.Equal(before.StatusMessage, state.StatusMessage);
        Assert.Equal(before.RejectionReason, state.RejectionReason);
        Assert.Equal("/tmp/frame-49.fits", state.LatestFramePath);
    }

    [Fact]
    public void TheEnginesExposureIsRecorded()
    {
        UiState state = After(Fresh(), new ExposureChangedEvent(TimeSpan.FromSeconds(0.5)));

        Assert.Equal(TimeSpan.FromSeconds(0.5), state.EngineExposure);
    }

    // ---- Sampling status (D26) ----

    private static UiState Running(SequenceMode mode = SequenceMode.Unconnected) =>
        After(Fresh(), new SessionStartedEvent(new SessionConfiguration(6, 70.0), mode));

    private static readonly DateTimeOffset Noon = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OutsideASequence_ThereIsNoSamplingStatus()
    {
        UiState state = After(Fresh(), new SolveScheduledEvent(SampleTrigger.Periodic, TimeSpan.FromSeconds(5)) { TimestampUtc = Noon });

        Assert.Null(AlignmentFormatting.SamplingStatus(state, Noon));
    }

    /// <summary>
    /// The countdown is read from the event's own timestamp, so it counts down
    /// between events rather than sitting at the delay it was scheduled with.
    /// </summary>
    [Fact]
    public void AScheduledSolve_CountsDown()
    {
        UiState state = After(
            Running(),
            new SolveScheduledEvent(SampleTrigger.Periodic, TimeSpan.FromSeconds(5)) { TimestampUtc = Noon });

        Assert.Contains("in 5 s", AlignmentFormatting.SamplingStatus(state, Noon), StringComparison.Ordinal);
        Assert.Contains("in 2 s", AlignmentFormatting.SamplingStatus(state, Noon.AddSeconds(3)), StringComparison.Ordinal);
        Assert.Contains("now", AlignmentFormatting.SamplingStatus(state, Noon.AddSeconds(6)), StringComparison.Ordinal);
    }

    [Fact]
    public void ASolveAfterASlew_IsShownAsSettling()
    {
        UiState state = After(
            Running(SequenceMode.Driven),
            new SolveScheduledEvent(SampleTrigger.SlewEnded, TimeSpan.FromSeconds(2)) { TimestampUtc = Noon });

        Assert.StartsWith("Settling", AlignmentFormatting.SamplingStatus(state, Noon), StringComparison.Ordinal);
    }

    [Fact]
    public void ASolveInProgress_IsShownAsSolving()
    {
        UiState state = After(Running(), new SolveStartedEvent(SampleTrigger.Periodic, "/tmp/f.fits"));

        Assert.Equal(SamplingActivity.Solving, state.Sampling.Activity);
        Assert.StartsWith("Solving", AlignmentFormatting.SamplingStatus(state, Noon), StringComparison.Ordinal);
    }

    /// <summary>
    /// The engine's own words for why a solve did not become a sample, since
    /// they say how far the telescope has turned and how far it must (D27):
    /// the one thing a user turning it by hand needs to know.
    /// </summary>
    [Fact]
    public void ASkippedSample_SaysWhy()
    {
        const string reason = "The telescope has turned 5.2° from the nearest sample; samples need to be at least 14° apart.";
        UiState state = After(Running(), new SampleSkippedEvent(reason));

        Assert.Equal(reason, AlignmentFormatting.SamplingStatus(state, Noon));
    }

    /// <summary>
    /// The run of failures survives the reschedule that follows each one, so
    /// the count is still on screen while the next solve counts down, and a
    /// solve that works ends it.
    /// </summary>
    [Fact]
    public void ConsecutiveFailures_AreCountedAcrossTheReschedule_AndEndedByASuccess()
    {
        UiState failing = After(
            Running(),
            new SolveFailedEvent(SampleTrigger.Periodic, "Could not solve (NoMatchFound).", 3),
            new SolveScheduledEvent(SampleTrigger.Periodic, TimeSpan.FromSeconds(5)) { TimestampUtc = Noon });

        Assert.Contains("3 in a row", AlignmentFormatting.SamplingStatus(failing, Noon), StringComparison.Ordinal);

        UiState recovered = After(failing, new SampleSkippedEvent("Solved. Waiting for a second solve in the same place."));
        Assert.Equal(0, recovered.Sampling.ConsecutiveFailures);
        Assert.DoesNotContain("in a row", AlignmentFormatting.SamplingStatus(recovered, Noon), StringComparison.Ordinal);
    }

    /// <summary>
    /// Any motion cancels a pending solve (D26), so a countdown shown while the
    /// mount is moving would count down to nothing.
    /// </summary>
    [Fact]
    public void AMovingMount_OverridesTheCountdown()
    {
        UiState state = After(
            Running(SequenceMode.Observed),
            new SolveScheduledEvent(SampleTrigger.SlewEnded, TimeSpan.FromSeconds(2)) { TimestampUtc = Noon },
            new MountStatusEvent(150.0, 60.0, MountTrackingState.Tracking, MeridianSide.West, IsMoving: true));

        Assert.True(state.MountIsMoving);
        Assert.StartsWith("Mount moving", AlignmentFormatting.SamplingStatus(state, Noon), StringComparison.Ordinal);
    }

    [Fact]
    public void ATakenSample_IsCountedAndSaidSo()
    {
        UiState state = After(
            Running(),
            new TargetSelectedEvent(60.0, false, 6, 70.0),
            new PointCapturedEvent(new CapturePoint(2, 150.0, 60.0, DateTime.UtcNow), SampleTrigger.Periodic));

        Assert.Equal(2, state.CapturedPointCount);
        Assert.Equal(SamplingActivity.Sampled, state.Sampling.Activity);
        Assert.Equal("Sample taken.", AlignmentFormatting.SamplingStatus(state, Noon));
    }

    // ---- Readout modes ----

    /// <summary>
    /// A camera offering no readout modes leaves the list empty, so the UI can
    /// hide the picker rather than present a menu of one.
    /// </summary>
    [Fact]
    public void ACameraWithOneReadout_OffersNoChoice()
    {
        UiState state = After(Fresh(), new DeviceConnectedEvent(
            DeviceKind.Camera, "ASCOM", "cam", "Camera", "driver", new CameraDescription(3.76, 100, 100)));

        Assert.Empty(state.ReadoutModes);
        Assert.Null(state.ReadoutModeIndex);
    }

    [Fact]
    public void ReadoutModesAndTheActiveOne_AreReportedOnConnect()
    {
        UiState state = After(Fresh(), new DeviceConnectedEvent(
            DeviceKind.Camera, "Simulator", "sim-camera", "Simulated Camera", "driver",
            new CameraDescription(
                3.8, 2737, 2053,
                new[]
                {
                    new CameraReadoutModeDescription(0, "High dynamic range", 16),
                    new CameraReadoutModeDescription(1, "Fast", 8),
                },
                ReadoutModeIndex: 0)));

        Assert.Equal(2, state.ReadoutModes.Count);
        Assert.Equal(0, state.ReadoutModeIndex);
    }

    /// <summary>
    /// The index moves only when the camera confirms it, so the picker cannot
    /// sit showing a mode the driver refused -- which would leave the user
    /// believing they are reading out at a depth they are not.
    /// </summary>
    [Fact]
    public void TheActiveModeFollowsTheCamerasConfirmation()
    {
        UiState state = After(
            Fresh(),
            new DeviceConnectedEvent(
                DeviceKind.Camera, "Simulator", "sim-camera", "Simulated Camera", "driver",
                new CameraDescription(
                    3.8, 2737, 2053,
                    new[]
                    {
                        new CameraReadoutModeDescription(0, "High dynamic range", 16),
                        new CameraReadoutModeDescription(1, "Fast", 8),
                    },
                    ReadoutModeIndex: 0)),
            new ReadoutModeChangedEvent(1, "Fast", 8));

        Assert.Equal(1, state.ReadoutModeIndex);
        Assert.Contains("8-bit", state.StatusMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// A driver that will not say the bit depth reads as a bare name rather than
    /// a guessed depth. A wrong depth is worse than an unknown one, because the
    /// depth is what decides how far a centroid can be trusted.
    /// </summary>
    [Fact]
    public void AModeWithNoKnownBitDepth_DoesNotClaimOne()
    {
        var withDepth = new CameraReadoutModeDescription(0, "Mode A", 16);
        var withoutDepth = new CameraReadoutModeDescription(1, "Mode B");

        Assert.Contains("16-bit", withDepth.Label, StringComparison.Ordinal);
        Assert.Equal("Mode B", withoutDepth.Label);
        Assert.DoesNotContain("bit", withoutDepth.Label, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Disconnecting clears the mode list with the camera. A list left over from
    /// a device that is no longer attached would let a selection be sent to
    /// whatever connects next, where the same index means something else.
    /// </summary>
    [Fact]
    public void DisconnectingACamera_ClearsItsReadoutModes()
    {
        UiState state = After(
            Fresh(),
            new DeviceConnectedEvent(
                DeviceKind.Camera, "Simulator", "sim-camera", "Simulated Camera", "driver",
                new CameraDescription(
                    3.8, 2737, 2053,
                    new[] { new CameraReadoutModeDescription(0, "Fast", 8) },
                    ReadoutModeIndex: 0)),
            new DeviceDisconnectedEvent(DeviceKind.Camera));

        Assert.Empty(state.ReadoutModes);
        Assert.Null(state.ReadoutModeIndex);
    }
}
