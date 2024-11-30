using System;
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
using Wino.Core;
using Wino.Core.Domain.Interfaces;
using Wino.Core.UWP;
using Wino.Messaging.Client.Connection;
using Wino.Messaging.Server;

namespace Wino.Calendar
{
    public sealed partial class App : WinoApplication, IRecipient<NewSynchronizationRequested>
    {
        public override string AppCenterKey => "dfdad6ab-95f9-44cc-9112-45ec6730c49e";

        private BackgroundTaskDeferral connectionBackgroundTaskDeferral;
        private BackgroundTaskDeferral toastActionBackgroundTaskDeferral;

        public App()
        {
            InitializeComponent();

            WeakReferenceMessenger.Default.Register(this);
        }

        public override IServiceProvider ConfigureServices()
        {
            var services = new ServiceCollection();

            services.RegisterCoreServices();
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
        }

        private void RegisterViewModels(IServiceCollection services)
        {
            services.AddSingleton(typeof(AppShellViewModel));
            services.AddTransient(typeof(CalendarPageViewModel));
            services.AddTransient(typeof(CalendarSettingsPageViewModel));
            services.AddTransient(typeof(AccountManagementViewModel));
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

        public void Receive(NewSynchronizationRequested message)
        {

        }



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

                    connectionBackgroundTaskDeferral = args.TaskInstance.GetDeferral();
                    args.TaskInstance.Canceled += OnConnectionBackgroundTaskCanceled;

                    AppServiceConnectionManager.Connection = appServiceTriggerDetails.AppServiceConnection;

                    WeakReferenceMessenger.Default.Send(new WinoServerConnectionEstablished());
                }
            }
        }

        public void OnConnectionBackgroundTaskCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
        {
            sender.Canceled -= OnConnectionBackgroundTaskCanceled;

            Log.Information($"Server connection background task was canceled. Reason: {reason}");

            connectionBackgroundTaskDeferral?.Complete();
            connectionBackgroundTaskDeferral = null;

            AppServiceConnectionManager.Connection = null;
        }
    }
}
