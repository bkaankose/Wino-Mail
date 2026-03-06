using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using MoreLinq;
using Wino.Core.Domain.Enums;
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
        var pageType = ViewModel.NavigationService.GetPageType(message.PageType);
        if (pageType == null) return;

        WizardFrame.Navigate(pageType, message.Parameter, new SlideNavigationTransitionInfo
        {
            Effect = SlideNavigationTransitionEffect.FromRight
        });

        PageHistory.ForEach(a => a.IsActive = false);
        PageHistory.Add(new BreadcrumbNavigationItemViewModel(message, isActive: true, stepNumber: PageHistory.Count + 1));
    }

    public void Receive(BackBreadcrumNavigationRequested message)
    {
        GoBackFrame();
    }

    private void GoBackFrame()
    {
        if (!WizardFrame.CanGoBack) return;

        PageHistory.RemoveAt(PageHistory.Count - 1);
        WizardFrame.GoBack(new SlideNavigationTransitionInfo
        {
            Effect = SlideNavigationTransitionEffect.FromLeft
        });

        if (PageHistory.Count > 0)
        {
            PageHistory.ForEach(a => a.IsActive = false);
            PageHistory[PageHistory.Count - 1].IsActive = true;
        }
    }

    private void BreadItemClicked(Microsoft.UI.Xaml.Controls.BreadcrumbBar sender, Microsoft.UI.Xaml.Controls.BreadcrumbBarItemClickedEventArgs args)
    {
        var clickedItem = PageHistory[args.Index];
        var currentActive = PageHistory.FirstOrDefault(a => a.IsActive);

        // Only allow navigating backwards (clicking items before current)
        if (currentActive == null || args.Index >= PageHistory.IndexOf(currentActive))
            return;

        while (PageHistory.FirstOrDefault(a => a.IsActive) != clickedItem && WizardFrame.CanGoBack)
        {
            GoBackFrame();
        }
    }
}
