using FreePolarAlign.Devices;
using Xunit;

namespace FreePolarAlign.Tests.Devices;

/// <summary>
/// The mapping between a camera's own gain units and the 0-100 control.
///
/// The ranges used are the real ones this was written for: an ASI290 reports
/// 0 to 600 (tenths of a decibel), a ToupTek camera something like 100 to 5000
/// (percent of unity gain). The mapping has to hit both ends of each exactly --
/// "maximum gain" that is one unit short of the camera's maximum is a
/// promise the control does not keep.
/// </summary>
public class GainScaleTests
{
    private static readonly CameraGainRange Asi290 = new(0, 600);
    private static readonly CameraGainRange ToupTek = new(100, 5000);

    [Fact]
    public void TheEndsOfTheControlAreTheEndsOfTheCamerasRange()
    {
        Assert.Equal(0, GainScale.ToRaw(0, Asi290));
        Assert.Equal(600, GainScale.ToRaw(100, Asi290));
        Assert.Equal(100, GainScale.ToRaw(0, ToupTek));
        Assert.Equal(5000, GainScale.ToRaw(100, ToupTek));
    }

    [Fact]
    public void TheMiddleIsTheMiddle() => Assert.Equal(300, GainScale.ToRaw(50, Asi290));

    /// <summary>
    /// Every whole percent survives the round trip on a range wider than 100
    /// units, which both vendors' are. The control would otherwise jump from
    /// the value typed to a neighbour after the camera answered.
    /// </summary>
    [Fact]
    public void EveryPercentSurvivesTheRoundTripOnARealRange()
    {
        foreach (CameraGainRange range in new[] { Asi290, ToupTek })
        {
            for (int percent = 0; percent <= 100; percent++)
            {
                Assert.Equal(percent, GainScale.ToPercent(GainScale.ToRaw(percent, range), range));
            }
        }
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(250, 600)]
    public void APercentOutsideTheControlIsClamped(int percent, int expectedRaw) =>
        Assert.Equal(expectedRaw, GainScale.ToRaw(percent, Asi290));

    [Fact]
    public void ARawValueOutsideTheRangeReadsAsTheNearestEnd()
    {
        Assert.Equal(100, GainScale.ToPercent(900, Asi290));
        Assert.Equal(0, GainScale.ToPercent(-10, Asi290));
    }

    /// <summary>A camera reporting one legal value must not divide by zero.</summary>
    [Fact]
    public void ADegenerateRangeReadsAsZero() =>
        Assert.Equal(0, GainScale.ToPercent(120, new CameraGainRange(120, 120)));
}
