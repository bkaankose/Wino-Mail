using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.WinUI.Controls.ListView;

public partial class WinoMailItemContainerStyleSelector : StyleSelector
{
    public Style? ThreadStyle { get; set; }
    public Style? MailItemStyle { get; set; }
    public Style? ThreadStyleWithoutSwipe { get; set; }
    public Style? MailItemStyleWithoutSwipe { get; set; }
    public bool IsSwipeActionsEnabled { get; set; } = true;

    protected override Style SelectStyleCore(object item, DependencyObject container)
    {
        if (item is MailItemViewModel)
        {
            return ResolveStyle(
                IsSwipeActionsEnabled,
                MailItemStyle,
                MailItemStyleWithoutSwipe,
                nameof(MailItemViewModel));
        }

        if (item is ThreadMailItemViewModel)
        {
            return ResolveStyle(
                IsSwipeActionsEnabled,
                ThreadStyle,
                ThreadStyleWithoutSwipe,
                nameof(ThreadMailItemViewModel));
        }

        return base.SelectStyleCore(item, container);
    }

    private static Style ResolveStyle(bool isSwipeActionsEnabled, Style? swipeStyle, Style? noSwipeStyle, string itemTypeName)
    {
        var resolvedStyle = isSwipeActionsEnabled ? swipeStyle : noSwipeStyle;

        return resolvedStyle ?? throw new Exception($"Missing style for {itemTypeName}");
    }
}
