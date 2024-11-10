using System;
using System.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Wino.Core.ViewModels;
using Wino.Messaging.Client.Shell;

namespace Wino.Core.UWP
{
    public class BasePage : Page, IRecipient<LanguageChanged>
    {
        public UIElement ShellContent
        {
            get { return (UIElement)GetValue(ShellContentProperty); }
            set { SetValue(ShellContentProperty, value); }
        }

        public static readonly DependencyProperty ShellContentProperty = DependencyProperty.Register(nameof(ShellContent), typeof(UIElement), typeof(BasePage), new PropertyMetadata(null));

        public void Receive(LanguageChanged message)
        {
            OnLanguageChanged();
        }

        public virtual void OnLanguageChanged() { }
    }

    public abstract class BasePage<T> : BasePage where T : CoreBaseViewModel
    {
        public T ViewModel { get; } = WinoApplication.Current.Services.GetService<T>();

        protected BasePage()
        {
            ViewModel.Dispatcher = new UWPDispatcher(Dispatcher);

            Loaded += PageLoaded;
            Unloaded += PageUnloaded;
        }

        private void PageUnloaded(object sender, RoutedEventArgs e)
        {
            Loaded -= PageLoaded;
            Unloaded -= PageUnloaded;
        }

        private void PageLoaded(object sender, RoutedEventArgs e) => ViewModel.OnPageLoaded();

        ~BasePage()
        {
            Debug.WriteLine($"Disposed {GetType().Name}");
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var mode = GetNavigationMode(e.NavigationMode);
            var parameter = e.Parameter;

            WeakReferenceMessenger.Default.UnregisterAll(this);
            WeakReferenceMessenger.Default.RegisterAll(this);

            ViewModel.OnNavigatedTo(mode, parameter);
        }

        protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            base.OnNavigatingFrom(e);

            var mode = GetNavigationMode(e.NavigationMode);
            var parameter = e.Parameter;

            WeakReferenceMessenger.Default.UnregisterAll(this);

            ViewModel.OnNavigatedFrom(mode, parameter);

            GC.Collect();
        }

        private Domain.Models.Navigation.NavigationMode GetNavigationMode(NavigationMode mode)
        {
            return (Domain.Models.Navigation.NavigationMode)mode;
        }
    }
}
