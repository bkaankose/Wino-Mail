using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Services;

namespace Wino.Core.UWP.Services
{
    // TODO: Refactor this thing. It's garbage.

    public class NotificationBuilder : INotificationBuilder
    {
        private readonly IUnderlyingThemeService _underlyingThemeService;
        private readonly IAccountService _accountService;
        private readonly IFolderService _folderService;

        public NotificationBuilder(IUnderlyingThemeService underlyingThemeService, IAccountService accountService, IFolderService folderService)
        {
            _underlyingThemeService = underlyingThemeService;
            _accountService = accountService;
            _folderService = folderService;
        }

        public async Task CreateNotificationsAsync(Guid inboxFolderId, IEnumerable<IMailItem> newMailItems)
        {
            var mailCount = newMailItems.Count();

            // If there are more than 3 mails, just display 1 general toast.
            if (mailCount > 3)
            {
                var builder = new ToastContentBuilder();
                builder.SetToastScenario(ToastScenario.Default);

                builder.AddText(Translator.Notifications_MultipleNotificationsTitle);
                builder.AddText(string.Format(Translator.Notifications_MultipleNotificationsTitle, mailCount));

                builder.AddButton(GetDismissButton());

                builder.Show();
            }
            else
            {
                foreach (var mailItem in newMailItems)
                {
                    if (mailItem.IsRead)
                        continue;

                    var builder = new ToastContentBuilder();
                    builder.SetToastScenario(ToastScenario.Default);

                    var host = ThumbnailService.GetHost(mailItem.FromAddress);

                    var knownTuple = ThumbnailService.CheckIsKnown(host);

                    bool isKnown = knownTuple.Item1;
                    host = knownTuple.Item2;

                    if (isKnown)
                        builder.AddAppLogoOverride(new System.Uri(ThumbnailService.GetKnownHostImage(host)), hintCrop: ToastGenericAppLogoCrop.Default);
                    else
                    {
                        // TODO: https://learn.microsoft.com/en-us/windows/apps/design/shell/tiles-and-notifications/adaptive-interactive-toasts?tabs=toolkit
                        // Follow official guides for icons/theme.

                        bool isOSDarkTheme = _underlyingThemeService.IsUnderlyingThemeDark();
                        string profileLogoName = isOSDarkTheme ? "profile-dark.png" : "profile-light.png";

                        builder.AddAppLogoOverride(new System.Uri($"ms-appx:///Assets/NotificationIcons/{profileLogoName}"), hintCrop: ToastGenericAppLogoCrop.Circle);
                    }

                    // Override system notification timetamp with received date of the mail.
                    // It may create confusion for some users, but still it's the truth...
                    builder.AddCustomTimeStamp(mailItem.CreationDate.ToLocalTime());

                    builder.AddText(mailItem.FromName);
                    builder.AddText(mailItem.Subject);
                    builder.AddText(mailItem.PreviewText);

                    builder.AddArgument(Constants.ToastMailItemIdKey, mailItem.UniqueId.ToString());
                    builder.AddArgument(Constants.ToastActionKey, MailOperation.Navigate);

                    builder.AddButton(GetMarkedAsRead(mailItem.Id));
                    builder.AddButton(GetDeleteButton(mailItem.Id));
                    builder.AddButton(GetDismissButton());

                    builder.Show();
                }

                await UpdateTaskbarIconBadgeAsync();
            }
        }

        private ToastButton GetDismissButton()
            => new ToastButton()
            .SetDismissActivation()
            .SetImageUri(new Uri("ms-appx:///Assets/NotificationIcons/dismiss.png"));

        private ToastButton GetDeleteButton(string mailCopyId)
            => new ToastButton()
            .SetContent(Translator.MailOperation_Delete)
            .SetImageUri(new Uri("ms-appx:///Assets/NotificationIcons/delete.png"))
            .AddArgument(Constants.ToastMailItemIdKey, mailCopyId)
            .AddArgument(Constants.ToastActionKey, MailOperation.SoftDelete)
            .SetBackgroundActivation();

        private ToastButton GetMarkedAsRead(string mailCopyId)
            => new ToastButton()
            .SetContent(Translator.MailOperation_MarkAsRead)
            .SetImageUri(new System.Uri("ms-appx:///Assets/NotificationIcons/markread.png"))
            .AddArgument(Constants.ToastMailItemIdKey, mailCopyId)
            .AddArgument(Constants.ToastActionKey, MailOperation.MarkAsRead)
            .SetBackgroundActivation();

        public async Task UpdateTaskbarIconBadgeAsync()
        {
            int totalUnreadCount = 0;
            var badgeUpdater = BadgeUpdateManager.CreateBadgeUpdaterForApplication();

            try
            {
                var accounts = await _accountService.GetAccountsAsync();

                foreach (var account in accounts)
                {
                    var accountInbox = await _folderService.GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Inbox);

                    if (accountInbox == null)
                        continue;

                    var inboxUnreadCount = await _folderService.GetFolderNotificationBadgeAsync(accountInbox.Id);

                    totalUnreadCount += inboxUnreadCount;
                }

                if (totalUnreadCount > 0)
                {
                    // Get the blank badge XML payload for a badge number
                    XmlDocument badgeXml = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);

                    // Set the value of the badge in the XML to our number
                    XmlElement badgeElement = badgeXml.SelectSingleNode("/badge") as XmlElement;
                    badgeElement.SetAttribute("value", totalUnreadCount.ToString());

                    // Create the badge notification
                    BadgeNotification badge = new BadgeNotification(badgeXml);

                    // And update the badge
                    badgeUpdater.Update(badge);
                }
                else
                    badgeUpdater.Clear();
            }
            catch (System.Exception ex)
            {
                // TODO: Log exceptions.

                badgeUpdater.Clear();
            }
        }

        public async Task CreateTestNotificationAsync(string title, string message)
        {
            // with args test.
            await CreateNotificationsAsync(Guid.Parse("28c3c39b-7147-4de3-b209-949bd19eede6"), new List<IMailItem>()
            {
                new MailCopy()
                {
                    Subject = "test subject",
                    PreviewText = "preview html",
                    CreationDate = DateTime.UtcNow,
                    FromAddress = "bkaankose@outlook.com",
                    Id = "AAkALgAAAAAAHYQDEapmEc2byACqAC-EWg0AnMdP0zg8wkS_Ib2Eeh80LAAGq91I3QAA",
                }
            });

            //var builder = new ToastContentBuilder();
            //builder.SetToastScenario(ToastScenario.Default);

            //builder.AddText(title);
            //builder.AddText(message);

            //builder.Show();

            //await Task.CompletedTask;
        }
    }
}
