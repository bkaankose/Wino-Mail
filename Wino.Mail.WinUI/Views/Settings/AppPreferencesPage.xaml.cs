using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class AppPreferencesPage : AppPreferencesPageAbstract
{
    public AppPreferencesPage()
    {
        this.InitializeComponent();
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();

        DispatcherQueue.TryEnqueue(() =>
        {
            WeakReferenceMessenger.Default.Send(new SettingsRootNavigationRequested(WinoPage.SettingOptionsPage));
            WeakReferenceMessenger.Default.Send(new BreadcrumbNavigationRequested(
                Translator.SettingsAppPreferences_Title,
                WinoPage.AppPreferencesPage));
        });
    }
}
