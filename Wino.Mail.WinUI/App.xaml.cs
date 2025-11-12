using System;
using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Windows.AppLifecycle;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.WinUI;
using Wino.Core.WinUI.Interfaces;
using Wino.Mail.Services;
using Wino.Mail.ViewModels;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.Server;
using Wino.Services;
namespace Wino.Mail.WinUI;

public partial class App : WinoApplication, IRecipient<NewMailSynchronizationRequested>
{
    private ISynchronizationManager? _synchronizationManager;

    public App()
    {
        InitializeComponent();

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ToastNotificationManagerCompat.OnActivated += ToastActivationHandler;

        RegisterRecipients();
    }

    private async void ToastActivationHandler(ToastNotificationActivatedEventArgsCompat e)
    {
        // If we weren't launched by an app, launch our window like normal.
        // Otherwise if launched by a toast, our OnActivated callback will be triggered.

        var toastArgs = ToastArguments.Parse(e.Argument);

        var mailService = Services.GetRequiredService<IMailService>();
        var accountService = Services.GetRequiredService<IAccountService>();

        if (Guid.TryParse(toastArgs[Constants.ToastMailUniqueIdKey], out Guid mailItemUniqueId))
        {
            var account = await mailService.GetMailAccountByUniqueIdAsync(mailItemUniqueId).ConfigureAwait(false);
            if (account == null) return;

            var mailItem = await mailService.GetSingleMailItemAsync(mailItemUniqueId).ConfigureAwait(false);
            if (mailItem == null) return;

            var message = new AccountMenuItemExtended(mailItem.AssignedFolder.Id, mailItem);

            // Delegate this event to LaunchProtocolService so app shell can pick it up on launch if app doesn't work.
            var launchProtocolService = Services.GetRequiredService<ILaunchProtocolService>();
            launchProtocolService.LaunchParameter = message;

            // Send the messsage anyways. Launch protocol service will be ignored if the message is picked up by subscriber shell.
            WeakReferenceMessenger.Default.Send(message);
        }

        if (ToastNotificationManagerCompat.WasCurrentProcessToastActivated())
        {
            MainWindow.BringToFront();
        }
    }

    #region Dependency Injection


    private void RegisterUWPServices(IServiceCollection services)
    {
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IMailDialogService, DialogService>();
        services.AddTransient<ISettingsBuilderService, SettingsBuilderService>();
        services.AddTransient<IProviderService, ProviderService>();
        services.AddSingleton<IAuthenticatorConfig, MailAuthenticatorConfiguration>();
    }

    private void RegisterViewModels(IServiceCollection services)
    {
        services.AddSingleton(typeof(AppShellViewModel));

        services.AddTransient(typeof(MailListPageViewModel));
        services.AddTransient(typeof(MailRenderingPageViewModel));
        services.AddTransient(typeof(AccountManagementViewModel));
        services.AddTransient(typeof(WelcomePageViewModel));

        services.AddTransient(typeof(ComposePageViewModel));
        services.AddTransient(typeof(IdlePageViewModel));

        services.AddTransient(typeof(EditAccountDetailsPageViewModel));
        services.AddTransient(typeof(AccountDetailsPageViewModel));
        services.AddTransient(typeof(SignatureManagementPageViewModel));
        services.AddTransient(typeof(MessageListPageViewModel));
        services.AddTransient(typeof(ReadComposePanePageViewModel));
        services.AddTransient(typeof(MergedAccountDetailsPageViewModel));
        services.AddTransient(typeof(LanguageTimePageViewModel));
        services.AddTransient(typeof(AppPreferencesPageViewModel));
        services.AddTransient(typeof(AliasManagementPageViewModel));
        services.AddTransient(typeof(ContactsPageViewModel));
    }

    #endregion

    public override IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.RegisterViewModelService();
        services.RegisterSharedServices();
        services.RegisterCoreUWPServices();
        services.RegisterCoreViewModels();

        RegisterUWPServices(services);
        RegisterViewModels(services);

        return services.BuildServiceProvider();
    }

    private bool IsStartupTaskLaunch() => AppInstance.GetCurrent().GetActivatedEventArgs()?.Kind == ExtendedActivationKind.StartupTask;
    public bool IsAppRunning() => MainWindow != null;

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // If it's toast activation, compat will handle it.
        if (IsAppRunning()) return;

        // TODO: Check app relaunch mutex before loading anything.

        // Initialize NewThemeService first to get backdrop settings before creating window
        var newThemeService = Services.GetRequiredService<INewThemeService>();
        var configService = Services.GetRequiredService<IConfigurationService>();
        var nativeAppService = Services.GetRequiredService<INativeAppService>();

        _synchronizationManager = Services.GetRequiredService<ISynchronizationManager>();

        // Load saved backdrop type before creating window
        var savedBackdropType = (WindowBackdropType)configService.Get("WindowBackdropTypeKey", (int)WindowBackdropType.Mica);

        MainWindow = new ShellWindow();

        nativeAppService.GetCoreWindowHwnd = () => WinRT.Interop.WindowNative.GetWindowHandle(MainWindow);

        await InitializeServicesAsync();

        if (MainWindow is not IWinoShellWindow shellWindow) throw new ArgumentException("MainWindow must implement IWinoShellWindow");

        bool isStartupTaskLaunch = IsStartupTaskLaunch();

        shellWindow.HandleAppActivation(args);

        // Do not actiavate window if launched from startup task. Keep running in the system tray.
        if (!isStartupTaskLaunch)
        {
            MainWindow.Activate();
        }
    }

    private void RegisterRecipients()
    {
        WeakReferenceMessenger.Default.Register<NewMailSynchronizationRequested>(this);
    }

    public void Receive(NewMailSynchronizationRequested message)
    {
        _synchronizationManager?.SynchronizeMailAsync(message.Options);
    }
}
