using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Wino.Core.Domain.Enums;
using Wino.Helpers;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Views.Abstract;
using Wino.Messaging.Client.Navigation;

namespace Wino.Views;

public sealed partial class WelcomeHostPage : WelcomeHostPageAbstract,
    IRecipient<BreadcrumbNavigationRequested>,
    IRecipient<BackBreadcrumNavigationRequested>
{
    public ObservableCollection<BreadcrumbNavigationItemViewModel> PageHistory { get; set; } = [];

    public WelcomeHostPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        WeakReferenceMessenger.Default.Register<BreadcrumbNavigationRequested>(this);
        WeakReferenceMessenger.Default.Register<BackBreadcrumNavigationRequested>(this);

        // Navigate to the welcome/get-started page without adding it to the wizard breadcrumb.
        // Breadcrumb steps only start after the user clicks "Get Started".
        var welcomePageType = ViewModel.NavigationService.GetPageType(WinoPage.WelcomePageV2);
        WizardFrame.Navigate(welcomePageType, null, new SuppressNavigationTransitionInfo());
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        WeakReferenceMessenger.Default.Unregister<BreadcrumbNavigationRequested>(this);
        WeakReferenceMessenger.Default.Unregister<BackBreadcrumNavigationRequested>(this);

        base.OnNavigatingFrom(e);
    }

    public void Receive(BreadcrumbNavigationRequested message)
    {
        BreadcrumbNavigationHelper.Navigate(WizardFrame, PageHistory, message, ViewModel.NavigationService.GetPageType);
    }

    public void Receive(BackBreadcrumNavigationRequested message)
    {
        GoBackFrame();
    }

    private void GoBackFrame()
    {
        BreadcrumbNavigationHelper.GoBack(WizardFrame, PageHistory, NavigationTransitionEffect.FromLeft);
    }

    private void BreadItemClicked(Microsoft.UI.Xaml.Controls.BreadcrumbBar sender, Microsoft.UI.Xaml.Controls.BreadcrumbBarItemClickedEventArgs args)
    {
        BreadcrumbNavigationHelper.NavigateTo(WizardFrame, PageHistory, args.Index);
    }
}
