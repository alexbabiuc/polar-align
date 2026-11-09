using Avalonia;
using Avalonia.Controls;

namespace FreePolarAlign.App.Controls;

/// <summary>
/// Two children on one row, the first filling whatever the second leaves, with
/// the second at the right. When the first would be left narrower than
/// <see cref="LeadingMinWidth"/>, the second moves to a row of its own below,
/// still at the right, and the first takes the whole width.
///
/// Made for the frame panel's controls: the brightness slider, and the
/// exposure and binning pickers beside it. One row is what the user asked for,
/// and at most widths it fits. At the minimum window size the panel is 312px
/// wide, which the labelled pickers alone nearly fill, and a slider squeezed
/// to nothing is not a slider. Measured, not a fixed breakpoint, so a change
/// of font or label moves the point where it wraps along with it.
/// </summary>
public sealed class FillThenWrapPanel : Panel
{
    public static readonly StyledProperty<double> LeadingMinWidthProperty =
        AvaloniaProperty.Register<FillThenWrapPanel, double>(nameof(LeadingMinWidth), 120.0);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<FillThenWrapPanel, double>(nameof(Spacing), 8.0);

    static FillThenWrapPanel()
    {
        AffectsMeasure<FillThenWrapPanel>(LeadingMinWidthProperty, SpacingProperty);
    }

    /// <summary>The narrowest the first child may be left before the second wraps below it.</summary>
    public double LeadingMinWidth
    {
        get => GetValue(LeadingMinWidthProperty);
        set => SetValue(LeadingMinWidthProperty, value);
    }

    /// <summary>The gap between the two children, across or down.</summary>
    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Whether the last arrange put the second child on a row of its own. For tests.</summary>
    public bool IsWrapped { get; private set; }

    private (Control Leading, Control Trailing)? Pair() =>
        Children.Count == 2 && Children[0] is Control leading && Children[1] is Control trailing
            ? (leading, trailing)
            : null;

    private bool Wraps(double width, Control trailing) =>
        double.IsFinite(width) && width - trailing.DesiredSize.Width - Spacing < LeadingMinWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Pair() is not var (leading, trailing))
        {
            throw new InvalidOperationException($"{nameof(FillThenWrapPanel)} lays out exactly two children.");
        }

        trailing.Measure(availableSize);

        if (Wraps(availableSize.Width, trailing))
        {
            leading.Measure(availableSize);
            return new Size(
                Math.Max(leading.DesiredSize.Width, trailing.DesiredSize.Width),
                leading.DesiredSize.Height + Spacing + trailing.DesiredSize.Height);
        }

        double leadingWidth = Math.Max(0.0, availableSize.Width - trailing.DesiredSize.Width - Spacing);
        leading.Measure(new Size(leadingWidth, availableSize.Height));
        return new Size(
            leading.DesiredSize.Width + Spacing + trailing.DesiredSize.Width,
            Math.Max(leading.DesiredSize.Height, trailing.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (leading, trailing) = Pair()!.Value;
        double trailingWidth = trailing.DesiredSize.Width;

        IsWrapped = Wraps(finalSize.Width, trailing);
        if (IsWrapped)
        {
            double leadingHeight = leading.DesiredSize.Height;
            leading.Arrange(new Rect(0, 0, finalSize.Width, leadingHeight));
            trailing.Arrange(new Rect(
                Math.Max(0.0, finalSize.Width - trailingWidth), leadingHeight + Spacing,
                Math.Min(trailingWidth, finalSize.Width), trailing.DesiredSize.Height));
            return finalSize;
        }

        double rowHeight = finalSize.Height;
        leading.Arrange(new Rect(0, 0, Math.Max(0.0, finalSize.Width - trailingWidth - Spacing), rowHeight));
        trailing.Arrange(new Rect(finalSize.Width - trailingWidth, 0, trailingWidth, rowHeight));
        return finalSize;
    }
}
