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
    [NotifyPropertyChangedFor(nameof(ShowPurchaseState))]
    [NotifyPropertyChangedFor(nameof(ShowUsageSummary))]
    public partial bool IsPurchased { get; set; }

    public ICommand? PurchaseCommand { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoadingState))]
    [NotifyPropertyChangedFor(nameof(ShowPurchaseState))]
    [NotifyPropertyChangedFor(nameof(ShowUsageSummary))]
    [NotifyPropertyChangedFor(nameof(ShowErrorState))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsPurchaseInProgress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowErrorState))]
    [NotifyPropertyChangedFor(nameof(ShowUsageSummary))]
    public partial bool HasUsageData { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowErrorState))]
    [NotifyPropertyChangedFor(nameof(ShowUsageSummary))]
    public partial string ErrorText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int UsageCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUsageSummary))]
    public partial int UsageLimit { get; set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUsageSummary))]
    public partial double UsagePercentage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUsageSummary))]
    public partial string RenewalText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUsageSummary))]
    public partial string UsageResetText { get; set; } = string.Empty;

    public bool ShowLoadingState => IsLoading;

    public bool ShowPurchaseState => !IsLoading && string.IsNullOrWhiteSpace(ErrorText);

    public bool ShowUsageSummary => ProductType == WinoAddOnProductType.AI_PACK &&
                                    IsPurchased &&
                                    !IsLoading &&
                                    string.IsNullOrWhiteSpace(ErrorText) &&
                                    HasUsageData;

    public bool ShowErrorState => !IsLoading && !string.IsNullOrWhiteSpace(ErrorText);

    public WinoAddOnItemViewModel(WinoAddOnProductType productType)
    {
        ProductType = productType;
    }
}
