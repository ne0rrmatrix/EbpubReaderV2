namespace DisplayBook.App.Views;

/// <summary>
/// Computes how many columns a cover grid should show, based on available width and a fixed
/// target column width. Columns are always sized close to <see cref="TargetItemWidth"/> so a
/// cover box built for that width (see <see cref="CoverBoxHeight"/>) keeps a correct book-cover
/// aspect ratio instead of being stretched into whatever shape a fixed column count produces.
/// </summary>
internal static class ResponsiveGridSpan
{
    /// <summary>Target width, in device-independent units, for a single grid column.</summary>
    public const double TargetItemWidth = 150;

    /// <summary>Cover box height that keeps a ~5:6 book-cover aspect ratio at <see cref="TargetItemWidth"/>.</summary>
    public const double CoverBoxHeight = 180;

    private const double ItemSpacing = 10;
    private const double AssumedPagePadding = 32;

    public static int Compute(double width)
    {
        if (width <= 0)
        {
            return 1;
        }

        var idiom = DeviceInfo.Idiom;
        var minColumns = idiom == DeviceIdiom.Phone ? 1 : 2;

        var usableWidth = Math.Max(width - AssumedPagePadding, TargetItemWidth);
        var columns = (int)((usableWidth + ItemSpacing) / (TargetItemWidth + ItemSpacing));
        return Math.Max(columns, minColumns);
    }
}
