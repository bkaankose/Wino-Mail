using System;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.UWP.Services;
using Wino.Helpers;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Messages;
using Wino.Messaging.Client.Mails;
using Wino.Views;
using Wino.Views.Account;
using Wino.Views.Settings;

namespace Wino.Services
{
    public class NavigationService : NavigationServiceBase, INavigationService
    {
        private readonly IStatePersistanceService _statePersistanceService;

        private WinoPage[] _renderingPageTypes = new WinoPage[]
        {
            WinoPage.MailRenderingPage,
            WinoPage.ComposePage
        };

        public NavigationService(IStatePersistanceService statePersistanceService)
        {
            _statePersistanceService = statePersistanceService;
        }

        public Type GetPageType(WinoPage winoPage)
        {
            return winoPage switch
            {
                WinoPage.None => null,
                WinoPage.IdlePage => typeof(IdlePage),
                WinoPage.AccountDetailsPage => typeof(AccountDetailsPage),
                WinoPage.MergedAccountDetailsPage => typeof(MergedAccountDetailsPage),
                WinoPage.AccountManagementPage => typeof(AccountManagementPage),
                WinoPage.ManageAccountsPage => typeof(ManageAccountsPage),
                WinoPage.SignatureManagementPage => typeof(SignatureManagementPage),
                WinoPage.AboutPage => typeof(AboutPage),
                WinoPage.PersonalizationPage => typeof(PersonalizationPage),
                WinoPage.MessageListPage => typeof(MessageListPage),
                WinoPage.ReadComposePanePage => typeof(ReadComposePanePage),
                WinoPage.MailRenderingPage => typeof(MailRenderingPage),
                WinoPage.ComposePage => typeof(ComposePage),
                WinoPage.MailListPage => typeof(MailListPage),
                WinoPage.SettingsPage => typeof(SettingsPage),
                WinoPage.WelcomePage => typeof(WelcomePage),
                WinoPage.SettingOptionsPage => typeof(SettingOptionsPage),
                WinoPage.AppPreferencesPage => typeof(AppPreferencesPage),
                WinoPage.AliasManagementPage => typeof(AliasManagementPage),
                WinoPage.LanguageTimePage => typeof(LanguageTimePage),
                _ => null,
            };
        }

        public Frame GetCoreFrame(NavigationReferenceFrame frameType)
        {
            if (Window.Current.Content is Frame appFrame)
                return WinoVisualTreeHelper.GetChildObject<Frame>(appFrame.Content as UIElement, frameType.ToString());

            return null;
        }

        public bool Navigate(WinoPage page,
                             object parameter = null,
                             NavigationReferenceFrame frame = NavigationReferenceFrame.ShellFrame,
                             NavigationTransitionType transition = NavigationTransitionType.None)
        {
            var pageType = GetPageType(page);
            Frame shellFrame = GetCoreFrame(NavigationReferenceFrame.ShellFrame);

            _statePersistanceService.IsReadingMail = _renderingPageTypes.Contains(page);

            if (shellFrame != null)
            {
                var currentFrameType = GetCurrentFrameType(ref shellFrame);

                bool isMailListingPageActive = currentFrameType != null && currentFrameType == typeof(MailListPage);

                // Active page is mail list page and we are refreshing the folder.
                if (isMailListingPageActive && currentFrameType == pageType && parameter is NavigateMailFolderEventArgs folderNavigationArgs)
                {
                    // No need for new navigation, just refresh the folder.
                    WeakReferenceMessenger.Default.Send(new ActiveMailFolderChangedEvent(folderNavigationArgs.BaseFolderMenuItem, folderNavigationArgs.FolderInitLoadAwaitTask));
                    WeakReferenceMessenger.Default.Send(new DisposeRenderingFrameRequested());

                    return true;
                }

                var transitionInfo = GetNavigationTransitionInfo(transition);

                // This page must be opened in the Frame placed in MailListingPage.
                if (isMailListingPageActive && frame == NavigationReferenceFrame.RenderingFrame)
                {
                    var listingFrame = GetCoreFrame(NavigationReferenceFrame.RenderingFrame);

                    if (listingFrame == null) return false;

                    // Active page is mail list page and we are opening a mail item.
                    // No navigation needed, just refresh the rendered mail item.
                    if (listingFrame.Content != null
                        && listingFrame.Content.GetType() == GetPageType(WinoPage.MailRenderingPage)
                        && parameter is MailItemViewModel mailItemViewModel
                        && page != WinoPage.ComposePage)
                    {
                        WeakReferenceMessenger.Default.Send(new NewMailItemRenderingRequestedEvent(mailItemViewModel));
                    }
                    else if (listingFrame.Content != null
                        && listingFrame.Content.GetType() == GetPageType(WinoPage.IdlePage)
                        && pageType == typeof(IdlePage))
                    {
                        // Idle -> Idle navigation. Ignore.
                        return true;
                    }
                    else
                    {
                        listingFrame.Navigate(pageType, parameter, transitionInfo);
                    }

                    return true;
                }

                if ((currentFrameType != null && currentFrameType != pageType) || currentFrameType == null)
                {
                    return shellFrame.Navigate(pageType, parameter, transitionInfo);
                }
            }

            return false;
        }

        public void GoBack() => throw new NotImplementedException("GoBack method is not implemented in Wino Mail.");

        // Standalone EML viewer.
        //public void NavigateRendering(MimeMessageInformation mimeMessageInformation, NavigationTransitionType transition = NavigationTransitionType.None)
        //{
        //    if (mimeMessageInformation == null)
        //        throw new ArgumentException("MimeMessage cannot be null.");

        //    Navigate(WinoPage.MailRenderingPage, mimeMessageInformation, NavigationReferenceFrame.RenderingFrame, transition);
        //}

        //// Mail item view model clicked handler.
        //public void NavigateRendering(IMailItem mailItem, NavigationTransitionType transition = NavigationTransitionType.None)
        //{
        //    if (mailItem is MailItemViewModel mailItemViewModel)
        //        Navigate(WinoPage.MailRenderingPage, mailItemViewModel, NavigationReferenceFrame.RenderingFrame, transition);
        //    else
        //        throw new ArgumentException("MailItem must be of type MailItemViewModel.");
        //}

        //public void NavigateFolder(NavigateMailFolderEventArgs args)
        //    => Navigate(WinoPage.MailListPage, args, NavigationReferenceFrame.ShellFrame);
    }
}
