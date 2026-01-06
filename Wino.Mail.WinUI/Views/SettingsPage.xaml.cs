using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using MoreLinq;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Views.Abstract;
using Wino.Views.Settings;

namespace Wino.Views;

public sealed partial class SettingsPage : SettingsPageAbstract, 
    IRecipient<BreadcrumbNavigationRequested>,
    IRecipient<BackBreadcrumNavigationRequested>
{
    public ObservableCollection<BreadcrumbNavigationItemViewModel> PageHistory { get; set; } = [];

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Register for frame navigation events to track back button visibility
        SettingsFrame.Navigated -= SettingsFrameNavigated;
        SettingsFrame.Navigated += SettingsFrameNavigated;

        SettingsFrame.Navigate(typeof(SettingOptionsPage), null, new SuppressNavigationTransitionInfo());

        var initialRequest = new BreadcrumbNavigationRequested(Translator.MenuSettings, WinoPage.SettingOptionsPage);
        PageHistory.Add(new BreadcrumbNavigationItemViewModel(initialRequest, true));

        if (e.Parameter is WinoPage parameterPage)
        {
            switch (parameterPage)
            {
                case WinoPage.AppPreferencesPage:
                    WeakReferenceMessenger.Default.Send(new BreadcrumbNavigationRequested(Translator.SettingsAppPreferences_Title, WinoPage.AppPreferencesPage));
                    break;
                case WinoPage.PersonalizationPage:
                    WeakReferenceMessenger.Default.Send(new BreadcrumbNavigationRequested(Translator.SettingsPersonalization_Title, WinoPage.PersonalizationPage));
                    break;
            }
        }
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();

        // Update Settings header in breadcrumb.

        var settingsHeader = PageHistory.FirstOrDefault();

        if (settingsHeader == null) return;

        settingsHeader.Title = Translator.MenuSettings;
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        // Unregister frame navigation event
        SettingsFrame.Navigated -= SettingsFrameNavigated;

        // Reset navigation state when leaving SettingsPage
        ViewModel.StatePersistenceService.IsSettingsNavigating = false;

        base.OnNavigatingFrom(e);
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

    void IRecipient<BreadcrumbNavigationRequested>.Receive(BreadcrumbNavigationRequested message)
    {
        var pageType = ViewModel.NavigationService.GetPageType(message.PageType);

        if (pageType == null) return;

        SettingsFrame.Navigate(pageType, message.Parameter, new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });

        PageHistory.ForEach(a => a.IsActive = false);

        PageHistory.Add(new BreadcrumbNavigationItemViewModel(message, true));
    }

    private void SettingsFrameNavigated(object sender, NavigationEventArgs e)
    {
        // Update back button visibility based on whether we can go back within the frame
        ViewModel.StatePersistenceService.IsSettingsNavigating = SettingsFrame.CanGoBack;
    }

    private void GoBackFrame(Core.Domain.Enums.NavigationTransitionEffect slideEffect)
    {
        if (SettingsFrame.CanGoBack)
        {
            PageHistory.RemoveAt(PageHistory.Count - 1);

            var winuiEffect = slideEffect switch
            {
                Core.Domain.Enums.NavigationTransitionEffect.FromLeft => Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionEffect.FromLeft,
                _ => Microsoft.UI.Xaml.Media.Animation.SlideNavigationTransitionEffect.FromRight,
            };

            SettingsFrame.GoBack(new SlideNavigationTransitionInfo() { Effect = winuiEffect });

            // Set the new last item as active
            if (PageHistory.Count > 0)
            {
                PageHistory.ForEach(a => a.IsActive = false);
                PageHistory[PageHistory.Count - 1].IsActive = true;
            }

            // Update back button visibility after navigation
            ViewModel.StatePersistenceService.IsSettingsNavigating = SettingsFrame.CanGoBack;
        }
    }

    private void BreadItemClicked(Microsoft.UI.Xaml.Controls.BreadcrumbBar sender, Microsoft.UI.Xaml.Controls.BreadcrumbBarItemClickedEventArgs args)
    {
        var clickedPageHistory = PageHistory[args.Index];

        // Trigger GoBack repeatedly until we reach the clicked breadcrumb item
        while (PageHistory.FirstOrDefault(a => a.IsActive) != clickedPageHistory)
        {
            ViewModel.NavigationService.GoBack();
        }
    }

    public void Receive(BackBreadcrumNavigationRequested message)
    {
        GoBackFrame(message.SlideEffect);
    }
}
