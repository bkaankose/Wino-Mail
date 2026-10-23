using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using MailCal.Core.Domain.Enums;
using MailCal.Helpers;
using MailCal.Mail.ViewModels.Data;
using MailCal.Mail.WinUI.Views.Abstract;
using MailCal.Messaging.Client.Navigation;

namespace MailCal.Views;

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
        ResetWizard();
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();
        WeakReferenceMessenger.Default.Register<BreadcrumbNavigationRequested>(this);
        WeakReferenceMessenger.Default.Register<BackBreadcrumNavigationRequested>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();
        WeakReferenceMessenger.Default.Unregister<BreadcrumbNavigationRequested>(this);
        WeakReferenceMessenger.Default.Unregister<BackBreadcrumNavigationRequested>(this);
    }

    public void Receive(BreadcrumbNavigationRequested message)
    {
        BreadcrumbNavigationHelper.Navigate(WizardFrame, PageHistory, message, ViewModel.NavigationService.GetPageType);
    }

    public void Receive(BackBreadcrumNavigationRequested message)
    {
        GoBackFrame();
    }

    public void ResetWizard()
    {
        PageHistory.Clear();
        WizardFrame.BackStack.Clear();
        WizardFrame.ForwardStack.Clear();

        // Navigate to the welcome/get-started page without adding it to the wizard breadcrumb.
        // Breadcrumb steps only start after the user clicks "Get Started".
        var welcomePageType = ViewModel.NavigationService.GetPageType(MailCalPage.WelcomePageV2);
        WizardFrame.Navigate(welcomePageType, null, new SuppressNavigationTransitionInfo());
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
