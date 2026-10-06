using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Mail.Controls.HoverActions;

public sealed partial class HoverActionButton : Button
{
    private const double MediumSize = 32d;
    private const double LargeSize = 40d;

    [GeneratedDependencyProperty(DefaultValue = HoverActionButtonSize.Small)]
    public partial HoverActionButtonSize ButtonSize { get; set; }

    partial void OnButtonSizePropertyChanged(DependencyPropertyChangedEventArgs e) => ApplyButtonSize();

    private void ApplyButtonSize()
    {
        if (ButtonSize == HoverActionButtonSize.Small)
        {
            ClearValue(WidthProperty);
            ClearValue(HeightProperty);
            return;
        }

        var size = ButtonSize == HoverActionButtonSize.Large
            ? LargeSize
            : MediumSize;

        Width = size;
        Height = size;
    }
}
