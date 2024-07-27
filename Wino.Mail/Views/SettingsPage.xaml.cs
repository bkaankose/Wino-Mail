using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using MoreLinq;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Views.Abstract;
using Wino.Views.Settings;

namespace Wino.Views
{
    public sealed partial class SettingsPage : SettingsPageAbstract, IRecipient<BreadcrumbNavigationRequested>
    {
        public ObservableCollection<BreadcrumbNavigationItemViewModel> PageHistory { get; set; } = new ObservableCollection<BreadcrumbNavigationItemViewModel>();

        public SettingsPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            SettingsFrame.Navigate(typeof(SettingOptionsPage), null, new SuppressNavigationTransitionInfo());

            var initialRequest = new BreadcrumbNavigationRequested(Translator.MenuSettings, WinoPage.SettingOptionsPage);
            PageHistory.Add(new BreadcrumbNavigationItemViewModel(initialRequest, true));
        }

        public override void OnLanguageChanged()
        {
            base.OnLanguageChanged();

            // Update Settings header in breadcrumb.

            var settingsHeader = PageHistory.FirstOrDefault();

            if (settingsHeader == null) return;

            settingsHeader.Title = Translator.MenuSettings;
        }

        private Type GetNavigationPageType(WinoPage page) => page switch
        {
            WinoPage.AboutPage => typeof(AboutPage),
            WinoPage.PersonalizationPage => typeof(PersonalizationPage),
            WinoPage.MessageListPage => typeof(MessageListPage),
            WinoPage.ReadComposePanePage => typeof(ReadComposePanePage),
            WinoPage.LanguageTimePage => typeof(LanguageTimePage),
            WinoPage.AppPreferencesPage => typeof(AppPreferencesPage),
            _ => null,
        };

        void IRecipient<BreadcrumbNavigationRequested>.Receive(BreadcrumbNavigationRequested message)
        {
            var pageType = GetNavigationPageType(message.PageType);

            if (pageType == null) return;

            SettingsFrame.Navigate(pageType, message.Parameter, new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });

            PageHistory.ForEach(a => a.IsActive = false);

            PageHistory.Add(new BreadcrumbNavigationItemViewModel(message, true));
        }

        private void BreadItemClicked(Microsoft.UI.Xaml.Controls.BreadcrumbBar sender, Microsoft.UI.Xaml.Controls.BreadcrumbBarItemClickedEventArgs args)
        {
            var clickedPageHistory = PageHistory[args.Index];
            var activeIndex = PageHistory.IndexOf(PageHistory.FirstOrDefault(a => a.IsActive));

            while (PageHistory.FirstOrDefault(a => a.IsActive) != clickedPageHistory)
            {
                SettingsFrame.GoBack(new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });
                PageHistory.RemoveAt(PageHistory.Count - 1);
                PageHistory[PageHistory.Count - 1].IsActive = true;
            }
        }
    }
}
