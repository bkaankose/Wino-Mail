﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.WinUI.Notifications;
using Serilog;
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
        private readonly IMailService _mailService;

        public NotificationBuilder(IUnderlyingThemeService underlyingThemeService,
                                   IAccountService accountService,
                                   IFolderService folderService,
                                   IMailService mailService)
        {
            _underlyingThemeService = underlyingThemeService;
            _accountService = accountService;
            _folderService = folderService;
            _mailService = mailService;
        }

        public async Task CreateNotificationsAsync(Guid inboxFolderId, IEnumerable<IMailItem> downloadedMailItems)
        {
            var mailCount = downloadedMailItems.Count();

            try
            {
                // If there are more than 3 mails, just display 1 general toast.
                if (mailCount > 3)
                {
                    var builder = new ToastContentBuilder();
                    builder.SetToastScenario(ToastScenario.Default);

                    builder.AddText(Translator.Notifications_MultipleNotificationsTitle);
                    builder.AddText(string.Format(Translator.Notifications_MultipleNotificationsMessage, mailCount));

                    builder.AddButton(GetDismissButton());
                    builder.AddAudio(new ToastAudio()
                    {
                        Src = new Uri("ms-winsoundevent:Notification.Mail")
                    });

                    builder.Show();
                }
                else
                {
                    var validItems = new List<IMailItem>();

                    // Fetch mails again to fill up assigned folder data and latest statuses.
                    // They've been marked as read by executing synchronizer tasks until inital sync finishes.

                    foreach (var item in downloadedMailItems)
                    {
                        var mailItem = await _mailService.GetSingleMailItemAsync(item.UniqueId);

                        if (mailItem != null && mailItem.AssignedFolder != null)
                        {
                            validItems.Add(mailItem);
                        }
                    }

                    foreach (var mailItem in validItems)
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

                        builder.AddArgument(Constants.ToastMailUniqueIdKey, mailItem.UniqueId.ToString());
                        builder.AddArgument(Constants.ToastActionKey, MailOperation.Navigate);

                        builder.AddButton(GetMarkedAsRead(mailItem.UniqueId));
                        builder.AddButton(GetDeleteButton(mailItem.UniqueId));
                        builder.AddButton(GetDismissButton());
                        builder.AddAudio(new ToastAudio()
                        {
                            Src = new Uri("ms-winsoundevent:Notification.Mail")
                        });

                        builder.Show();
                    }

                    await UpdateTaskbarIconBadgeAsync();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to create notifications.");
            }
        }

        private ToastButton GetDismissButton()
            => new ToastButton()
            .SetDismissActivation()
            .SetImageUri(new Uri("ms-appx:///Assets/NotificationIcons/dismiss.png"));

        private ToastButton GetDeleteButton(Guid mailUniqueId)
            => new ToastButton()
            .SetContent(Translator.MailOperation_Delete)
            .SetImageUri(new Uri("ms-appx:///Assets/NotificationIcons/delete.png"))
            .AddArgument(Constants.ToastMailUniqueIdKey, mailUniqueId.ToString())
            .AddArgument(Constants.ToastActionKey, MailOperation.SoftDelete)
            .SetBackgroundActivation();

        private ToastButton GetMarkedAsRead(Guid mailUniqueId)
            => new ToastButton()
            .SetContent(Translator.MailOperation_MarkAsRead)
            .SetImageUri(new System.Uri("ms-appx:///Assets/NotificationIcons/markread.png"))
            .AddArgument(Constants.ToastMailUniqueIdKey, mailUniqueId.ToString())
            .AddArgument(Constants.ToastActionKey, MailOperation.MarkAsRead)
            .SetBackgroundActivation();

        public async Task UpdateTaskbarIconBadgeAsync()
        {
            int totalUnreadCount = 0;

            try
            {
                var badgeUpdater = BadgeUpdateManager.CreateBadgeUpdaterForApplication();
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
            catch (Exception ex)
            {
                Log.Error(ex, "Error while updating taskbar badge.");
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
