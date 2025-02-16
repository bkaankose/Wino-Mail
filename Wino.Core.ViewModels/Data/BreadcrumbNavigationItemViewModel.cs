using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels.Data;

public partial class BreadcrumbNavigationItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string title;

    [ObservableProperty]
    private bool isActive;

    public BreadcrumbNavigationRequested Request { get; set; }

    public BreadcrumbNavigationItemViewModel(BreadcrumbNavigationRequested request, bool isActive)
    {
        Request = request;
        Title = request.PageTitle;
        IsActive = isActive;
    }
}
