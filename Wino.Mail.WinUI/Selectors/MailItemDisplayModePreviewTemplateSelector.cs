using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;

namespace Wino.Selectors;

/// <summary>
/// Template selector for previewing mail item display modes in Settings->Personalization page.
/// </summary>
public partial class MailItemDisplayModePreviewTemplateSelector : DataTemplateSelector
{
    public DataTemplate? CompactTemplate { get; set; }
    public DataTemplate? MediumTemplate { get; set; }
    public DataTemplate? SpaciousTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is MailListDisplayMode mode)
        {
            switch (mode)
            {
                case MailListDisplayMode.Spacious:
                    return SpaciousTemplate ?? throw new ArgumentException(nameof(SpaciousTemplate));
                case MailListDisplayMode.Medium:
                    return MediumTemplate ?? throw new ArgumentException(nameof(MediumTemplate));
                case MailListDisplayMode.Compact:
                    return CompactTemplate ?? throw new ArgumentException(nameof(CompactTemplate));
            }
        }

        return base.SelectTemplateCore(item, container);
    }
}
