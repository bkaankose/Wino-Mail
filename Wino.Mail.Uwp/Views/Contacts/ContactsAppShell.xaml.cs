using Microsoft.Extensions.DependencyInjection;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using NavigationView = Microsoft.UI.Xaml.Controls.NavigationView;
using NavigationViewDisplayMode = Microsoft.UI.Xaml.Controls.NavigationViewDisplayMode;
using NavigationViewDisplayModeChangedEventArgs = Microsoft.UI.Xaml.Controls.NavigationViewDisplayModeChangedEventArgs;

namespace Wino.Mail.Uwp.Views.Contacts;

public sealed partial class ContactsAppShell : Views.Abstract.ContactsAppShellAbstract
{
    public IPreferencesService PreferencesService { get; } = WinoApplication.Current.Services.GetRequiredService<IPreferencesService>();
    public IStatePersistanceService StatePersistenceService { get; } = WinoApplication.Current.Services.GetRequiredService<IStatePersistanceService>();
    public INavigationService NavigationService { get; } = WinoApplication.Current.Services.GetRequiredService<INavigationService>();

    public ContactsAppShell()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.NavigationMode == Windows.UI.Xaml.Navigation.NavigationMode.New && InnerShellFrame.Content == null)
        {
            NavigationService.Navigate(WinoPage.ContactsPage, null, NavigationReferenceFrame.InnerShellFrame, NavigationTransitionType.None);
        }
    }

    private void NavigationViewDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        InnerShellFrame.Margin = args.DisplayMode == NavigationViewDisplayMode.Minimal
            ? new Thickness(7, 0, 0, 0)
            : new Thickness(0);
    }
}
