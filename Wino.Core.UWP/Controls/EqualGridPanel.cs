using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Wino.Core.UWP.Controls;

public partial class EqualGridPanel : Panel
{
    public int Rows
    {
        get { return (int)GetValue(RowsProperty); }
        set { SetValue(RowsProperty, value); }
    }

    public static readonly DependencyProperty RowsProperty =
        DependencyProperty.Register(
            nameof(Rows),
            typeof(int),
            typeof(EqualGridPanel),
            new PropertyMetadata(1, OnLayoutPropertyChanged));

    public int Columns
    {
        get { return (int)GetValue(ColumnsProperty); }
        set { SetValue(ColumnsProperty, value); }
    }

    public static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.Register(
            nameof(Columns),
            typeof(int),
            typeof(EqualGridPanel),
            new PropertyMetadata(1, OnLayoutPropertyChanged));

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is EqualGridPanel panel)
        {
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Rows <= 0 || Columns <= 0)
        {
            return new Size(0, 0);
        }

        double cellWidth = availableSize.Width / Columns;
        double cellHeight = availableSize.Height / Rows;

        foreach (UIElement child in Children)
        {
            child.Measure(new Size(cellWidth, cellHeight));
        }

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Rows <= 0 || Columns <= 0)
        {
            return new Size(0, 0);
        }

        double cellWidth = finalSize.Width / Columns;
        double cellHeight = finalSize.Height / Rows;

        for (int i = 0; i < Children.Count; i++)
        {
            int row = i / Columns;
            int column = i % Columns;

            double x = column * cellWidth;
            double y = row * cellHeight;

            Rect rect = new Rect(x, y, cellWidth, cellHeight);
            Children[i].Arrange(rect);
        }

        return finalSize;
    }
}
