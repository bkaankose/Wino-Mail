namespace Wino.Mail.Controls.Core.Layout;

/// <summary>A rectangle in top-down (flipped) coordinates, independent of any UI framework.</summary>
public readonly record struct WinoLayoutFrame(double X, double Y, double Width, double Height);

/// <summary>Where a flow layout put its items and how tall the result is.</summary>
public sealed record WinoFlowLayoutResult(IReadOnlyList<WinoLayoutFrame> Frames, double Height);

/// <summary>
/// The wrapping row of the native chip strips (WinoFlowView on AppKit and UIKit): items keep their fitting
/// size and run left to right, and an item that does not fit after another one starts a new line.
/// </summary>
public static class WinoFlowLayout
{
    /// <summary>
    /// Arranges items of the given fitting sizes in a row <paramref name="width"/> wide. An item wider than the row
    /// is clipped to it. The height is 0 without items.
    /// </summary>
    public static WinoFlowLayoutResult Arrange(IReadOnlyList<(double Width, double Height)> sizes, double width, double spacing, double lineSpacing)
    {
        ArgumentNullException.ThrowIfNull(sizes);

        var frames = new WinoLayoutFrame[sizes.Count];
        double x = 0, y = 0, line = 0;
        for (int index = 0; index < sizes.Count; index++)
        {
            var (itemWidth, itemHeight) = sizes[index];
            if (x > 0 && x + itemWidth > width)
            {
                x = 0;
                y += line + lineSpacing;
                line = 0;
            }

            frames[index] = new WinoLayoutFrame(x, y, Math.Min(itemWidth, width), itemHeight);
            x += itemWidth + spacing;
            line = Math.Max(line, itemHeight);
        }

        return new WinoFlowLayoutResult(frames, sizes.Count == 0 ? 0 : y + line);
    }
}

/// <summary>
/// The grid of round colour swatches (WinoColorSwatchPicker): fixed-size swatches in as many columns as fit,
/// offset by a small inset so the selection ring is not clipped.
/// </summary>
public static class WinoSwatchGridLayout
{
    public const double SwatchSize = 22;
    public const double Spacing = 8;
    public const double Inset = 2;

    /// <summary>Width used to estimate the height before the picker has a frame (nine swatches).</summary>
    public const double DefaultWidth = 9 * (SwatchSize + Spacing);

    public static int Columns(double width)
        => Math.Max(1, (int)((Math.Max(SwatchSize, width) + Spacing) / (SwatchSize + Spacing)));

    public static int Rows(int count, int columns)
        => Math.Max(1, (int)Math.Ceiling(count / (double)Math.Max(1, columns)));

    /// <summary>The picker height for <paramref name="count"/> swatches in a picker <paramref name="width"/> wide.</summary>
    public static double Height(int count, double width)
    {
        int rows = Rows(count, Columns(width));
        return rows * SwatchSize + (rows - 1) * Spacing + 2 * Inset;
    }

    public static WinoLayoutFrame Frame(int index, int columns)
    {
        int safeColumns = Math.Max(1, columns);
        int row = index / safeColumns, column = index % safeColumns;
        return new WinoLayoutFrame(column * (SwatchSize + Spacing) + Inset, row * (SwatchSize + Spacing) + Inset, SwatchSize, SwatchSize);
    }
}

/// <summary>Aspect-fill placement for wallpapers and illustrations.</summary>
public static class WinoAspectFill
{
    /// <summary>
    /// The frame that scales an image of <paramref name="imageWidth"/> × <paramref name="imageHeight"/> to cover the
    /// bounds, centred; null when either size is empty.
    /// </summary>
    public static WinoLayoutFrame? Frame(double imageWidth, double imageHeight, double boundsWidth, double boundsHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || boundsWidth <= 0 || boundsHeight <= 0) return null;

        double scale = Math.Max(boundsWidth / imageWidth, boundsHeight / imageHeight);
        double width = imageWidth * scale, height = imageHeight * scale;
        return new WinoLayoutFrame((boundsWidth - width) / 2, (boundsHeight - height) / 2, width, height);
    }
}
