﻿using System;
using System.Collections.Generic;
using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Nito.AsyncEx;
using Serilog;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
using Windows.UI.Core.Preview;
using Windows.UI.Notifications;
using Wino.Activation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.UWP;
using Wino.Mail.Services;
using Wino.Mail.ViewModels;
using Wino.Messaging.Client.Connection;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.Server;
using Wino.Services;

namespace Wino;

public sealed partial class App : WinoApplication,
    IRecipient<NewMailSynchronizationRequested>
{
    private BackgroundTaskDeferral connectionBackgroundTaskDeferral;
    private BackgroundTaskDeferral toastActionBackgroundTaskDeferral;

    public App()
    {
        InitializeComponent();

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        WeakReferenceMessenger.Default.Register<LanguageChanged>(this);
        WeakReferenceMessenger.Default.Register<NewMailSynchronizationRequested>(this);
    }

    public override async void OnResuming(object sender, object e)
    {
        // App Service connection was lost on suspension.
        // We must restore it.
        // Server might be running already, but re-launching it will trigger a new connection attempt.

        // Server connection is now handled by the empty implementation
        // No need to reconnect after resuming
    }

    public override IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.RegisterViewModelService();
        services.RegisterSharedServices();
        services.RegisterCoreUWPServices();
        services.RegisterCoreViewModels();

        RegisterUWPServices(services);
        RegisterViewModels(services);
        RegisterActivationHandlers(services);

        return services.BuildServiceProvider();
    }

    #region Dependency Injection

    private void RegisterActivationHandlers(IServiceCollection services)
    {
        services.AddTransient<ProtocolActivationHandler>();
        services.AddTransient<ToastNotificationActivationHandler>();
        services.AddTransient<FileActivationHandler>();
    }

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
    }

    #endregion

    protected override async void OnBackgroundActivated(BackgroundActivatedEventArgs args)
    {
        base.OnBackgroundActivated(args);

        if (args.TaskInstance.TriggerDetails is AppServiceTriggerDetails appServiceTriggerDetails)
        {
            LogActivation("OnBackgroundActivated -> AppServiceTriggerDetails received.");

            // Only accept connections from callers in the same package
            if (appServiceTriggerDetails.CallerPackageFamilyName == Package.Current.Id.FamilyName)
            {
                // Connection established from the fulltrust process
                // This is no longer needed with the empty connection manager implementation

                connectionBackgroundTaskDeferral = args.TaskInstance.GetDeferral();
                args.TaskInstance.Canceled += OnConnectionBackgroundTaskCanceled;
            }
        }
        else if (args.TaskInstance.TriggerDetails is ToastNotificationActionTriggerDetail toastNotificationActionTriggerDetail)
        {
            // Notification action is triggered and the app is not running.

            LogActivation("OnBackgroundActivated -> ToastNotificationActionTriggerDetail received.");

            toastActionBackgroundTaskDeferral = args.TaskInstance.GetDeferral();
            args.TaskInstance.Canceled += OnToastActionClickedBackgroundTaskCanceled;

            await InitializeServicesAsync();

            var toastArguments = ToastArguments.Parse(toastNotificationActionTriggerDetail.Argument);

            // All toast activation mail actions are handled here like mark as read or delete.
            // This should not launch the application on the foreground.

            // Get the action and mail item id.
            // Prepare package and send to delegator.

            if (toastArguments.TryGetValue(Constants.ToastActionKey, out MailOperation action) &&
                                toastArguments.TryGetValue(Constants.ToastMailUniqueIdKey, out string mailUniqueIdString) &&
                                Guid.TryParse(mailUniqueIdString, out Guid mailUniqueId))
            {

                // At this point server should've already been connected.

                var processor = base.Services.GetService<IWinoRequestProcessor>();
                var delegator = base.Services.GetService<IWinoRequestDelegator>();
                var mailService = base.Services.GetService<IMailService>();

                var mailItem = await mailService.GetSingleMailItemAsync(mailUniqueId);

                if (mailItem != null)
                {
                    var package = new MailOperationPreperationRequest(action, mailItem);

                    await delegator.ExecuteAsync(package);
                }
            }

            toastActionBackgroundTaskDeferral.Complete();
        }
        else
        {
            // Other background activations might have handlers.
            // AppServiceTrigger is handled here because delegating it to handlers somehow make it not work...

            await ActivateWinoAsync(args);
        }
    }

    private void OnToastActionClickedBackgroundTaskCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
    {
        sender.Canceled -= OnToastActionClickedBackgroundTaskCanceled;

        Log.Information($"Toast action background task was canceled. Reason: {reason}");

        toastActionBackgroundTaskDeferral?.Complete();
        toastActionBackgroundTaskDeferral = null;
    }

    protected override IEnumerable<ActivationHandler> GetActivationHandlers()
    {
        yield return Services.GetService<ProtocolActivationHandler>();
        yield return Services.GetService<ToastNotificationActivationHandler>();
        yield return Services.GetService<FileActivationHandler>();
    }

    public void OnConnectionBackgroundTaskCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
    {
        sender.Canceled -= OnConnectionBackgroundTaskCanceled;

        Log.Information($"Server connection background task was canceled. Reason: {reason}");

        connectionBackgroundTaskDeferral?.Complete();
        connectionBackgroundTaskDeferral = null;
    }

    public async void Receive(NewMailSynchronizationRequested message)
    {
        // Synchronization is now handled elsewhere
        // The empty connection manager doesn't perform actual sync operations
        await Task.CompletedTask;
    }

    protected override async void OnApplicationCloseRequested(object sender, SystemNavigationCloseRequestedPreviewEventArgs e)
    {
        Log.Information("App close requested.");

        var deferral = e.GetDeferral();

        // Wino should notify user on app close if:
        // 1. Startup behavior is not Enabled.
        // 2. Server terminate behavior is set to Terminate.

        // User has some accounts. Check if Wino Server runs on system startup.

        var dialogService = base.Services.GetService<IMailDialogService>();
        var startupBehaviorService = base.Services.GetService<IStartupBehaviorService>();
        var preferencesService = base.Services.GetService<IPreferencesService>();

        var currentStartupBehavior = await startupBehaviorService.GetCurrentStartupBehaviorAsync();

        bool? isGoToAppPreferencesRequested = null;

        if (currentStartupBehavior != StartupBehaviorResult.Enabled)
        {
            // Startup behavior is not enabled.

            isGoToAppPreferencesRequested = await dialogService.ShowWinoCustomMessageDialogAsync(Translator.AppCloseBackgroundSynchronizationWarningTitle,
                                                             $"{Translator.AppCloseStartupLaunchDisabledWarningMessageFirstLine}\n{Translator.AppCloseStartupLaunchDisabledWarningMessageSecondLine}\n\n{Translator.AppCloseStartupLaunchDisabledWarningMessageThirdLine}",
                                                             Translator.Buttons_Yes,
                                                             WinoCustomMessageDialogIcon.Warning,
                                                             Translator.Buttons_No,
                                                             "DontAskDisabledStartup");
        }

        if (isGoToAppPreferencesRequested == true)
        {
            WeakReferenceMessenger.Default.Send(new NavigateAppPreferencesRequested());
            e.Handled = true;
        }

        deferral.Complete();
    }

    protected override ActivationHandler<IActivatedEventArgs> GetDefaultActivationHandler()
        => new DefaultActivationHandler();
}
