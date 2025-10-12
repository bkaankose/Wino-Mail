using System;
using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.WinUI;
using Wino.Core.WinUI.Interfaces;
using Wino.Mail.Services;
using Wino.Mail.ViewModels;
using Wino.Mail.WinUI.Services;
using Wino.Messaging.Server;
using Wino.Services;
namespace Wino.Mail.WinUI;

public partial class App : WinoApplication, IRecipient<NewMailSynchronizationRequested>
{
    private ISynchronizationManager _synchronizationManager;

    public App()
    {
        InitializeComponent();

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        WeakReferenceMessenger.Default.Register<NewMailSynchronizationRequested>(this);
    }

    #region Dependency Injection


    private void RegisterUWPServices(IServiceCollection services)
    {
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IMailDialogService, DialogService>();
        services.AddTransient<ISettingsBuilderService, SettingsBuilderService>();
        services.AddTransient<IProviderService, ProviderService>();
        services.AddSingleton<IAuthenticatorConfig, MailAuthenticatorConfiguration>();
        services.AddSingleton<ISystemTrayService, SystemTrayService>();
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

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
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

        // Initialize system tray
        var systemTrayService = Services.GetRequiredService<ISystemTrayService>();
        if (systemTrayService != null)
        {
            systemTrayService.Initialize();
            systemTrayService.Show(); // Explicitly show the tray icon
        }

        if (MainWindow is not IWinoShellWindow shellWindow) throw new ArgumentException("MainWindow must implement IWinoShellWindow");

        shellWindow.HandleAppActivation(args);

        MainWindow.Activate();
    }

    public void Receive(NewMailSynchronizationRequested message)
    {
        _synchronizationManager.SynchronizeMailAsync(message.Options);
    }
}
