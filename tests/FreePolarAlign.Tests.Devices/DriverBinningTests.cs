using FreePolarAlign.Devices.Ascom;
using FreePolarAlign.Devices.ToupTek;
using FreePolarAlign.Devices.Zwo;
using Xunit;

namespace FreePolarAlign.Tests.Devices;

/// <summary>
/// D43 as revised: what each plugin asks its SDK for when the driver bins. The
/// native calls themselves cannot run here; these are the decisions around
/// them, which are where a wrong answer would come from.
/// </summary>
public class DriverBinningTests
{
    /// <summary>The SDK ends the list with a 0, and 1 is offered even where the list forgets it.</summary>
    [Fact]
    public void ZwoBins_StopAtTheTerminator_AndAlwaysIncludeOne()
    {
        int[] field = new int[16];
        field[0] = 2;
        field[1] = 4;
        field[2] = 0;
        field[3] = 3;

        Assert.Equal(new[] { 1, 2, 4 }, ZwoMapping.SupportedBins(field));
    }

    /// <summary>
    /// ASISetROIFormat refuses a width that is not a multiple of 8 or a height
    /// that is not a multiple of 2, so the binned frame is cut down to fit.
    /// </summary>
    [Theory]
    [InlineData(1936, 1096, 1, 1936, 1096)]
    [InlineData(1936, 1096, 2, 968, 548)]
    [InlineData(6248, 4176, 2, 3120, 2088)]
    [InlineData(3096, 2080, 3, 1032, 692)]
    [InlineData(1304, 976, 4, 320, 244)]
    public void ZwoBinnedFrames_AreSizedAsTheSdkAccepts(int width, int height, int bin, int expectedWidth, int expectedHeight)
    {
        (int w, int h) = ZwoMapping.BinnedFrameSize(width, height, bin);

        Assert.Equal((expectedWidth, expectedHeight), (w, h));
        Assert.Equal(0, w % 8);
        Assert.Equal(0, h % 2);
    }

    /// <summary>
    /// The averaging form of the option, never the plain one, which the header
    /// documents as a saturating sum: summed, a bright star's four wells clip,
    /// and the frame would no longer match what the session's own binning makes.
    /// </summary>
    [Theory]
    [InlineData(1, 0x01)]
    [InlineData(2, 0x82)]
    [InlineData(3, 0x83)]
    [InlineData(4, 0x84)]
    public void ToupTekBinning_AveragesRatherThanSums(int bin, int expected) =>
        Assert.Equal(expected, ToupTekMapping.BinningOption(bin));

    [Theory]
    [InlineData(4, 4, new[] { 1, 2, 3, 4 })]
    [InlineData(8, 8, new[] { 1, 2, 3, 4 })]
    [InlineData(2, 4, new[] { 1, 2 })]
    [InlineData(1, 1, new[] { 1 })]
    [InlineData(0, 0, new int[0])]
    public void AscomBins_RunToTheSmallerMaximum_AndNoFurtherThanFour(int maxX, int maxY, int[] expected) =>
        Assert.Equal(expected, AscomMapping.SupportedBins(maxX, maxY));
}
