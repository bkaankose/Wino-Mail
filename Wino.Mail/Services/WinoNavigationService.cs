using System;
using CommunityToolkit.Mvvm.Messaging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Navigation;
using Wino.Helpers;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Messages;
using Wino.Views;
using Wino.Views.Account;
using Wino.Views.Settings;

namespace Wino.Services
{
    public class WinoNavigationService : IWinoNavigationService
    {
        private Frame GetCoreFrame(NavigationReferenceFrame frameType)
        {
            if (Window.Current.Content is Frame appFrame && appFrame.Content is AppShell shellPage)
                return WinoVisualTreeHelper.GetChildObject<Frame>(shellPage, frameType.ToString());

            return null;
        }

        private Type GetCurrentFrameType(ref Frame _frame)
        {
            if (_frame != null && _frame.Content != null)
                return _frame.Content.GetType();
            else
            {
                return null;
            }
        }

        private Type GetPageType(WinoPage winoPage)
        {
            switch (winoPage)
            {
                case WinoPage.None:
                    return null;
                case WinoPage.IdlePage:
                    return typeof(IdlePage);
                case WinoPage.AccountDetailsPage:
                    return typeof(AccountDetailsPage);
                case WinoPage.MergedAccountDetailsPage:
                    return typeof(MergedAccountDetailsPage);
                case WinoPage.AccountManagementPage:
                    return typeof(NewAccountManagementPage);
                case WinoPage.SignatureManagementPage:
                    return typeof(SignatureManagementPage);
                case WinoPage.AboutPage:
                    return typeof(AboutPage);
                case WinoPage.PersonalizationPage:
                    return typeof(PersonalizationPage);
                case WinoPage.MessageListPage:
                    return typeof(MessageListPage);
                case WinoPage.ReadingPanePage:
                    return typeof(ReadingPanePage);
                case WinoPage.MailRenderingPage:
                    return typeof(MailRenderingPage);
                case WinoPage.ComposePage:
                    return typeof(ComposePage);
                case WinoPage.MailListPage:
                    return typeof(MailListPage);
                case WinoPage.SettingsPage:
                    return typeof(SettingsPage);
                case WinoPage.WelcomePage:
                    return typeof(WelcomePage);
                case WinoPage.SettingOptionsPage:
                    return typeof(SettingOptionsPage);
                default:
                    return null;
            }
        }

        public bool Navigate(WinoPage page,
                             object parameter = null,
                             NavigationReferenceFrame frame = NavigationReferenceFrame.ShellFrame,
                             NavigationTransitionType transition = NavigationTransitionType.None)
        {
            var pageType = GetPageType(page);
            Frame shellFrame = GetCoreFrame(NavigationReferenceFrame.ShellFrame);

            if (shellFrame != null)
            {
                var currentFrameType = GetCurrentFrameType(ref shellFrame);

                bool isMailListingPageActive = currentFrameType != null && currentFrameType == typeof(MailListPage);

                // Active page is mail list page and we are refreshing the folder.
                if (isMailListingPageActive && currentFrameType == pageType && parameter is NavigateMailFolderEventArgs folderNavigationArgs)
                {
                    // No need for new navigation, just refresh the folder.
                    WeakReferenceMessenger.Default.Send(new ActiveMailFolderChangedEvent(folderNavigationArgs.BaseFolderMenuItem, folderNavigationArgs.FolderInitLoadAwaitTask));

                    return true;
                }

                var transitionInfo = GetNavigationTransitionInfo(transition);

                // This page must be opened in the Frame placed in MailListingPage.
                if (isMailListingPageActive && frame == NavigationReferenceFrame.RenderingFrame)
                {
                    var listingFrame = GetCoreFrame(NavigationReferenceFrame.RenderingFrame);

                    if (listingFrame == null) return false;

                    listingFrame.Navigate(pageType, parameter, transitionInfo);

                    return true;
                }

                if ((currentFrameType != null && currentFrameType != pageType) || currentFrameType == null)
                {
                    return shellFrame.Navigate(pageType, parameter, transitionInfo);
                }
            }

            return false;
        }

        private NavigationTransitionInfo GetNavigationTransitionInfo(NavigationTransitionType transition)
        {
            return transition switch
            {
                NavigationTransitionType.DrillIn => new DrillInNavigationTransitionInfo(),
                _ => new SuppressNavigationTransitionInfo(),
            };
        }

        public void NavigateCompose(IMailItem mailItem, NavigationTransitionType transition = NavigationTransitionType.None)
            => Navigate(WinoPage.ComposePage, mailItem, NavigationReferenceFrame.RenderingFrame, transition);

        // Standalone EML viewer.
        public void NavigateRendering(MimeMessageInformation mimeMessageInformation, NavigationTransitionType transition = NavigationTransitionType.None)
        {
            if (mimeMessageInformation == null)
                throw new ArgumentException("MimeMessage cannot be null.");

            Navigate(WinoPage.MailRenderingPage, mimeMessageInformation, NavigationReferenceFrame.RenderingFrame, transition);
        }

        // Mail item view model clicked handler.
        public void NavigateRendering(IMailItem mailItem, NavigationTransitionType transition = NavigationTransitionType.None)
        {
            if (mailItem is MailItemViewModel mailItemViewModel)
                Navigate(WinoPage.MailRenderingPage, mailItemViewModel, NavigationReferenceFrame.RenderingFrame, transition);
            else
                throw new ArgumentException("MailItem must be of type MailItemViewModel.");
        }

        public void NavigateWelcomePage() => Navigate(WinoPage.WelcomePage);

        public void NavigateManageAccounts() => Navigate(WinoPage.AccountManagementPage);

        public void NavigateFolder(NavigateMailFolderEventArgs args)
            => Navigate(WinoPage.MailListPage, args, NavigationReferenceFrame.ShellFrame);
    }
}
