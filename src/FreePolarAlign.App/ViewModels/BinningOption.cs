namespace FreePolarAlign.App.ViewModels;

/// <summary>
/// One entry in the binning picker (D43). Only none and 2x2: 2x2 is what the
/// frames that prompted it needed, and a choice nobody has a use for yet is one
/// more way to get a frame wrong.
/// </summary>
public sealed record BinningOption(int Factor, string Label)
{
    public static IReadOnlyList<BinningOption> All { get; } = new[]
    {
        new BinningOption(1, "1×1"),
        new BinningOption(2, "2×2"),
    };

    /// <summary>The listed option for a remembered factor, or null when there is none or it is no longer offered.</summary>
    public static BinningOption? For(int? factor) => All.FirstOrDefault(option => option.Factor == factor);
}
