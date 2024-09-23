using System;
using System.Collections.Generic;
using Microsoft.AppCenter.Analytics;
using Microsoft.AppCenter.Crashes;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Foundation.Metadata;
using Windows.UI.Xaml;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.UWP
{
    public abstract class WinoApplication : Application
    {
        public IServiceProvider Services { get; }
        protected ILogInitializer LogInitializer { get; }
        public abstract string AppCenterKey { get; }

        public new static WinoApplication Current => (WinoApplication)Application.Current;

        protected WinoApplication()
        {
            ConfigureAppCenter();
            ConfigurePrelaunch();

            Services = ConfigureServices();

            UnhandledException += OnAppUnhandledException;
            Resuming += OnResuming;
            Suspending += OnSuspending;

            LogInitializer = Services.GetService<ILogInitializer>();

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
        public abstract void ConfigureAppCenter();
        public abstract void ConfigureLogging();
    }
}
