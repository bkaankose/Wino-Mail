using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core.Preview;
using Wino.Activation;
using Wino.Calendar.Activation;
using Wino.Calendar.Services;
using Wino.Calendar.ViewModels;
using Wino.Core;
using Wino.Core.Domain.Interfaces;
using Wino.Core.UWP;

namespace Wino.Calendar
{
    public sealed partial class App : WinoApplication
    {
        public override string AppCenterKey => "dfdad6ab-95f9-44cc-9112-45ec6730c49e";

        public App()
        {
            InitializeComponent();
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
    }
}
