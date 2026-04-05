using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.ViewModels.Data;

namespace Wino.Selectors;

public partial class WinoAddOnTemplateSelector : DataTemplateSelector
{
    public DataTemplate? NotPurchasedTemplate { get; set; }
    public DataTemplate? AiPackPurchasedTemplate { get; set; }
    public DataTemplate? UnlimitedAccountsPurchasedTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is not WinoAddOnItemViewModel addOn)
            throw new ArgumentException(nameof(item));

        if (!addOn.IsPurchased)
            return NotPurchasedTemplate ?? throw new ArgumentException(nameof(NotPurchasedTemplate));

        return addOn.ProductType switch
        {
            WinoAddOnProductType.AI_PACK => AiPackPurchasedTemplate ?? throw new ArgumentException(nameof(AiPackPurchasedTemplate)),
            WinoAddOnProductType.UNLIMITED_ACCOUNTS => UnlimitedAccountsPurchasedTemplate ?? throw new ArgumentException(nameof(UnlimitedAccountsPurchasedTemplate)),
            _ => NotPurchasedTemplate ?? throw new ArgumentException(nameof(NotPurchasedTemplate))
        };
    }
}
