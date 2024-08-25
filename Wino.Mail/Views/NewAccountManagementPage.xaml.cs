using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using MoreLinq;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.UI;
using Wino.Views.Abstract;
using Wino.Views.Account;
using Wino.Views.Settings;

namespace Wino.Views
{
    public sealed partial class NewAccountManagementPage : NewAccountManagementPageAbstract,
        IRecipient<BackBreadcrumNavigationRequested>,
        IRecipient<BreadcrumbNavigationRequested>,
        IRecipient<MergedInboxRenamed>
    {
        public ObservableCollection<BreadcrumbNavigationItemViewModel> PageHistory { get; set; } = new ObservableCollection<BreadcrumbNavigationItemViewModel>();

        public NewAccountManagementPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var initialRequest = new BreadcrumbNavigationRequested("Manage Accounts", Core.Domain.Enums.WinoPage.AccountManagementPage);
            PageHistory.Add(new BreadcrumbNavigationItemViewModel(initialRequest, true));

            AccountPagesFrame.Navigate(typeof(AccountManagementPage), null, new SuppressNavigationTransitionInfo());
        }

        private Type GetPageNavigationType(WinoPage page)
        {
            return page switch
            {
                WinoPage.SignatureManagementPage => typeof(SignatureManagementPage),
                WinoPage.AccountDetailsPage => typeof(AccountDetailsPage),
                WinoPage.MergedAccountDetailsPage => typeof(MergedAccountDetailsPage),
                WinoPage.AliasManagementPage => typeof(AliasManagementPage),
                _ => null,
            };
        }

        void IRecipient<BreadcrumbNavigationRequested>.Receive(BreadcrumbNavigationRequested message)
        {
            var pageType = GetPageNavigationType(message.PageType);

            if (pageType == null) return;

            AccountPagesFrame.Navigate(pageType, message.Parameter, new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });

            PageHistory.ForEach(a => a.IsActive = false);

            PageHistory.Add(new BreadcrumbNavigationItemViewModel(message, true));
        }

        private void GoBackFrame()
        {
            if (AccountPagesFrame.CanGoBack)
            {
                PageHistory.RemoveAt(PageHistory.Count - 1);

                AccountPagesFrame.GoBack(new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });
            }
        }

        private void BreadItemClicked(Microsoft.UI.Xaml.Controls.BreadcrumbBar sender, Microsoft.UI.Xaml.Controls.BreadcrumbBarItemClickedEventArgs args)
        {
            var clickedPageHistory = PageHistory[args.Index];

            while (PageHistory.FirstOrDefault(a => a.IsActive) != clickedPageHistory)
            {
                AccountPagesFrame.GoBack(new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });
                PageHistory.RemoveAt(PageHistory.Count - 1);
                PageHistory[PageHistory.Count - 1].IsActive = true;
            }
        }

        public void Receive(BackBreadcrumNavigationRequested message)
        {
            GoBackFrame();
        }

        public void Receive(AccountUpdatedMessage message)
        {
            // TODO: Find better way to retrieve page history from the stack for the account.
            var activePage = PageHistory.LastOrDefault();

            if (activePage == null) return;

            activePage.Title = message.Account.Name;
        }

        public void Receive(MergedInboxRenamed message)
        {
            // TODO: Find better way to retrieve page history from the stack for the merged account.
            var activePage = PageHistory.LastOrDefault();

            if (activePage == null) return;

            activePage.Title = message.NewName;
        }
    }
}
