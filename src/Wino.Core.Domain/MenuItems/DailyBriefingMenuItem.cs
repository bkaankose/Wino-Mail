using CommunityToolkit.Mvvm.ComponentModel;

namespace Wino.Core.Domain.MenuItems;

public partial class DailyBriefingMenuItem : MenuItemBase
{
    [ObservableProperty]
    public partial bool HasUnseenContent { get; set; }
}
