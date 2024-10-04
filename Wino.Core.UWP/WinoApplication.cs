using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AppCenter;
using Microsoft.AppCenter.Analytics;
using Microsoft.AppCenter.Crashes;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Core;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.UI.Xaml;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;

namespace Wino.Core.UWP
{
    public abstract class WinoApplication : Application
    {
        public new static WinoApplication Current => (WinoApplication)Application.Current;

        public IServiceProvider Services { get; }
        protected ILogInitializer LogInitializer { get; }
        protected IApplicationConfiguration AppConfiguration { get; }
        protected IWinoServerConnectionManager<AppServiceConnection> AppServiceConnectionManager { get; }
        protected IThemeService ThemeService { get; }
        protected IDatabaseService DatabaseService { get; }
        protected ITranslationService TranslationService { get; }
        protected IDialogService DialogService { get; }

        public abstract string AppCenterKey { get; }

        protected WinoApplication()
        {
            ConfigureAppCenter();
            ConfigurePrelaunch();

            Services = ConfigureServices();

            UnhandledException += OnAppUnhandledException;
            Resuming += OnResuming;
            Suspending += OnSuspending;

            LogInitializer = Services.GetService<ILogInitializer>();
            AppConfiguration = Services.GetService<IApplicationConfiguration>();

            AppServiceConnectionManager = Services.GetService<IWinoServerConnectionManager<AppServiceConnection>>();
            ThemeService = Services.GetService<IThemeService>();
            DatabaseService = Services.GetService<IDatabaseService>();
            TranslationService = Services.GetService<ITranslationService>();
            DialogService = Services.GetService<IDialogService>();

            // Make sure the paths are setup on app start.
            AppConfiguration.ApplicationDataFolderPath = ApplicationData.Current.LocalFolder.Path;
            AppConfiguration.PublisherSharedFolderPath = ApplicationData.Current.GetPublisherCacheFolder(ApplicationConfiguration.SharedFolderName).Path;

            ConfigureLogging();
        }

        private void ConfigurePrelaunch()
        {
            if (ApiInformation.IsMethodPresent("Windows.ApplicationModel.Core.CoreApplication", "EnablePrelaunch"))
                CoreApplication.EnablePrelaunch(true);
        }

        private void OnAppUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            var parameters = new Dictionary<string, string>()
            {
                { "BaseMessage", e.Exception.GetBaseException().Message },
                { "BaseStackTrace", e.Exception.GetBaseException().StackTrace },
                { "StackTrace", e.Exception.StackTrace },
                { "Message", e.Exception.Message },
            };

            Log.Error(e.Exception, "[Wino Crash]");

            Crashes.TrackError(e.Exception, parameters);
            Analytics.TrackEvent("Wino Crashed", parameters);
        }

        public virtual void OnResuming(object sender, object e) { }
        public virtual void OnSuspending(object sender, SuspendingEventArgs e) { }

        public abstract IServiceProvider ConfigureServices();
        public void ConfigureAppCenter()
            => AppCenter.Start(AppCenterKey, typeof(Analytics), typeof(Crashes));

        public void ConfigureLogging()
        {
            string logFilePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, Constants.ClientLogFile);
            LogInitializer.SetupLogger(logFilePath);
        }
    }
}
