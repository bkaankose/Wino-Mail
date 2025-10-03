﻿using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
using Windows.UI.Core.Preview;
using Wino.Activation;
using Wino.Calendar.Activation;
using Wino.Calendar.Services;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.UWP;
using Wino.Core.ViewModels;
using Wino.Messaging.Client.Connection;
using Wino.Messaging.Server;
using Wino.Services;

namespace Wino.Calendar;

public sealed partial class App : WinoApplication, IRecipient<NewCalendarSynchronizationRequested>
{
    private BackgroundTaskDeferral connectionBackgroundTaskDeferral;

    public App()
    {
        InitializeComponent();
        WeakReferenceMessenger.Default.Register<NewCalendarSynchronizationRequested>(this);
    }

    public override IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.RegisterSharedServices();
        services.RegisterCalendarViewModelServices();
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
        //services.AddTransient<ProtocolActivationHandler>();
        //services.AddTransient<ToastNotificationActivationHandler>();
        //services.AddTransient<FileActivationHandler>();
    }

    private void RegisterUWPServices(IServiceCollection services)
    {
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ICalendarDialogService, DialogService>();
        services.AddTransient<ISettingsBuilderService, SettingsBuilderService>();
        services.AddTransient<IProviderService, ProviderService>();
        services.AddSingleton<IAuthenticatorConfig, CalendarAuthenticatorConfig>();
        services.AddSingleton<IAccountCalendarStateService, AccountCalendarStateService>();
    }

    private void RegisterViewModels(IServiceCollection services)
    {
        services.AddSingleton(typeof(AppShellViewModel));
        services.AddSingleton(typeof(CalendarPageViewModel));
        services.AddTransient(typeof(CalendarSettingsPageViewModel));
        services.AddTransient(typeof(AccountManagementViewModel));
        services.AddTransient(typeof(PersonalizationPageViewModel));
        services.AddTransient(typeof(AccountDetailsPageViewModel));
        services.AddTransient(typeof(EventDetailsPageViewModel));
    }

    #endregion

    protected override void OnApplicationCloseRequested(object sender, SystemNavigationCloseRequestedPreviewEventArgs e)
    {
        // TODO: Check server running.
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        LogActivation($"OnLaunched -> {args.GetType().Name}, Kind -> {args.Kind}, PreviousExecutionState -> {args.PreviousExecutionState}, IsPrelaunch -> {args.PrelaunchActivated}");

        if (!args.PrelaunchActivated)
        {
            await ActivateWinoAsync(args);
        }
    }

    protected override IEnumerable<ActivationHandler> GetActivationHandlers()
    {
        return null;
    }

    protected override ActivationHandler<IActivatedEventArgs> GetDefaultActivationHandler()
        => new DefaultActivationHandler();

    protected override void OnBackgroundActivated(BackgroundActivatedEventArgs args)
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
    }

    public void OnConnectionBackgroundTaskCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
    {
        sender.Canceled -= OnConnectionBackgroundTaskCanceled;

        Log.Information($"Server connection background task was canceled. Reason: {reason}");

        connectionBackgroundTaskDeferral?.Complete();
        connectionBackgroundTaskDeferral = null;
    }

    public async void Receive(NewCalendarSynchronizationRequested message)
    {
        // Synchronization is no longer performed through the server connection manager
        // This method is kept for compatibility but doesn't perform any actual work
        await Task.CompletedTask;
    }
}
