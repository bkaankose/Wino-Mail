using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Mail.WinUI.Views.Contacts;

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

        if (e.NavigationMode == Microsoft.UI.Xaml.Navigation.NavigationMode.New && InnerShellFrame.Content == null)
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
