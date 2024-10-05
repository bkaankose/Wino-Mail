using System;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core;
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
            //services.AddSingleton<IApplicationResourceManager<ResourceDictionary>, ApplicationResourceManager>();
            //services.AddSingleton<INavigationService, NavigationService>();
            //services.AddSingleton<IDialogService, DialogService>();
        }

        private void RegisterViewModels(IServiceCollection services)
        {
            //services.AddSingleton(typeof(AppShellViewModel));

            //services.AddTransient(typeof(MailListPageViewModel));
            //services.AddTransient(typeof(MailRenderingPageViewModel));
            //services.AddTransient(typeof(AccountManagementViewModel));
            //services.AddTransient(typeof(WelcomePageViewModel));

            //services.AddTransient(typeof(ComposePageViewModel));
            //services.AddTransient(typeof(IdlePageViewModel));

            //services.AddTransient(typeof(AccountDetailsPageViewModel));
            //services.AddTransient(typeof(SignatureManagementPageViewModel));
            //services.AddTransient(typeof(MessageListPageViewModel));
            //services.AddTransient(typeof(ReadComposePanePageViewModel));
            //services.AddTransient(typeof(MergedAccountDetailsPageViewModel));
            //services.AddTransient(typeof(LanguageTimePageViewModel));
            //services.AddTransient(typeof(AppPreferencesPageViewModel));
            //services.AddTransient(typeof(AliasManagementPageViewModel));
        }

        #endregion
    }
}
