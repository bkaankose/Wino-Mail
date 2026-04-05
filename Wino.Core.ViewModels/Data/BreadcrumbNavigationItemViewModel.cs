using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels.Data;

public partial class BreadcrumbNavigationItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string title;

    [ObservableProperty]
    private bool isActive;

    public int StepNumber { get; set; }

    public int BackStackDepth { get; set; }

    public BreadcrumbNavigationRequested Request { get; set; }

    public BreadcrumbNavigationItemViewModel(BreadcrumbNavigationRequested request, bool isActive, int stepNumber = 0, int backStackDepth = 0)
    {
        Request = request;
        Title = request.PageTitle;
        IsActive = isActive;
        StepNumber = stepNumber;
        BackStackDepth = backStackDepth;
    }
}
