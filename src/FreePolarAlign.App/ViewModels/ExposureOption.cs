namespace FreePolarAlign.App.ViewModels;

/// <summary>
/// One entry in the exposure picker.
///
/// A fixed list rather than a free number box, because the useful range is
/// narrow and the failure it exists to prevent is at one end of it. The app used
/// to expose two seconds and nothing else, and on a 105 mm lens under a
/// moderately bright sky that put the background at 57% of full well: the sky
/// swamped the stars, the detector found 23 to 28 of them where a shorter
/// exposure of the same field gives well over a hundred, and solves failed. A
/// typed field would invite 30 s as readily as 0.3 s, and nothing here wants
/// 30 s -- these frames are measured for star positions, not looked at.
/// </summary>
public sealed record ExposureOption(TimeSpan Duration, string Label)
{
    /// <summary>
    /// What the picker offers, shortest first. Two seconds is the top because it
    /// was the old fixed value and nothing here wants longer; the short end is
    /// where an over-exposed sky is rescued.
    /// </summary>
    public static IReadOnlyList<ExposureOption> All { get; } = new[]
    {
        new ExposureOption(TimeSpan.FromSeconds(0.1), "0.1 s"),
        new ExposureOption(TimeSpan.FromSeconds(0.2), "0.2 s"),
        new ExposureOption(TimeSpan.FromSeconds(0.5), "0.5 s"),
        new ExposureOption(TimeSpan.FromSeconds(1.0), "1 s"),
        new ExposureOption(TimeSpan.FromSeconds(1.5), "1.5 s"),
        new ExposureOption(TimeSpan.FromSeconds(2.0), "2 s"),
    };

    /// <summary>
    /// The listed option closest to <paramref name="seconds"/>, or null if there
    /// is nothing to match. Used when reloading a remembered choice: an exact
    /// comparison against a round-tripped double is a way to silently lose the
    /// setting, and the list is coarse enough that nearest is unambiguous.
    /// </summary>
    public static ExposureOption? Nearest(double? seconds)
    {
        if (seconds is not { } wanted || !double.IsFinite(wanted) || wanted <= 0.0)
        {
            return null;
        }

        ExposureOption best = All[0];
        foreach (ExposureOption option in All)
        {
            if (Math.Abs(option.Duration.TotalSeconds - wanted) < Math.Abs(best.Duration.TotalSeconds - wanted))
            {
                best = option;
            }
        }

        return best;
    }
}
