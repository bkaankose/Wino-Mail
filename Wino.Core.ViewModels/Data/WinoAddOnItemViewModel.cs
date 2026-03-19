#nullable enable
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Input;
using Wino.Core.Domain.Enums;

namespace Wino.Core.ViewModels.Data;

public partial class WinoAddOnItemViewModel : ObservableObject
{
    public WinoAddOnProductType ProductType { get; }

    public string NameKey => $"WinoAddOn_{ProductType}_Name";
    public string DescriptionKey => $"WinoAddOn_{ProductType}_Description";
    public string KeywordsKey => $"WinoAddOn_{ProductType}_Keywords";

    public string IconGlyph => ProductType switch
    {
        WinoAddOnProductType.AI_PACK => "\uE945",
        WinoAddOnProductType.UNLIMITED_ACCOUNTS => "\uE716",
        _ => "\uE10F"
    };

    [ObservableProperty]
    public partial bool IsPurchased { get; set; }

    public ICommand? PurchaseCommand { get; set; }

    public ICommand? ManageCommand { get; set; }

    [ObservableProperty]
    public partial bool IsPurchaseInProgress { get; set; }

    [ObservableProperty]
    public partial int UsageCount { get; set; }

    [ObservableProperty]
    public partial int UsageLimit { get; set; } = 1;

    [ObservableProperty]
    public partial double UsagePercentage { get; set; }

    [ObservableProperty]
    public partial string RenewalText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UsageResetText { get; set; } = string.Empty;

    public WinoAddOnItemViewModel(WinoAddOnProductType productType)
    {
        ProductType = productType;
    }
}
