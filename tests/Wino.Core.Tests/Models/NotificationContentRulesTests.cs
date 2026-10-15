using FluentAssertions;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Notifications;
using Xunit;

namespace Wino.Core.Tests.Models;

public sealed class NotificationContentRulesTests
{
    private static MailCopy Mail(SpecialFolderType folder = SpecialFolderType.Inbox, bool focused = true) => new()
    {
        FromName = "Ada Lovelace",
        FromAddress = "ada@example.com",
        Subject = "Release checklist",
        PreviewText = "The build is green.",
        IsFocused = focused,
        AssignedFolder = new MailItemFolder { SpecialFolderType = folder }
    };

    [Fact]
    public void PrivacyLevelDecidesTheText()
    {
        NotificationContentRules.ComposeMailText(Mail(), MailNotificationContent.SenderSubjectPreview)
            .Should().Be(new MailNotificationText("Ada Lovelace", "Release checklist", "The build is green."));
        NotificationContentRules.ComposeMailText(Mail(), MailNotificationContent.SenderOnly)
            .Should().Be(new MailNotificationText("Ada Lovelace", null, null));
        NotificationContentRules.ComposeMailText(Mail(), MailNotificationContent.Nothing).Title
            .Should().Be(Translator.Notifications_MultipleNotificationsTitle);
        NotificationContentRules.ShowsSenderPicture(MailNotificationContent.Nothing).Should().BeFalse();
    }

    [Fact]
    public void SenderAddressStandsInForAMissingName()
    {
        var mail = Mail();
        mail.FromName = " ";
        NotificationContentRules.ComposeMailText(mail, MailNotificationContent.SenderOnly).Title.Should().Be("ada@example.com");
    }

    [Theory]
    [InlineData(MailNotificationScope.AllFolders, SpecialFolderType.Junk, false, true)]
    [InlineData(MailNotificationScope.InboxOnly, SpecialFolderType.Other, true, false)]
    [InlineData(MailNotificationScope.FocusedInboxOnly, SpecialFolderType.Inbox, false, false)]
    [InlineData(MailNotificationScope.FocusedInboxOnly, SpecialFolderType.Inbox, true, true)]
    [InlineData(MailNotificationScope.InboxAndCustomFolders, SpecialFolderType.Other, false, true)]
    public void ScopeFollowsTheWindowsRule(MailNotificationScope scope, SpecialFolderType folder, bool focused, bool expected)
        => NotificationContentRules.IsWithinNotificationScope(Mail(folder, focused), scope).Should().Be(expected);

    [Fact]
    public void ConfiguredActionsFallBackAndNeverRepeat()
    {
        NotificationContentRules.ResolveMailActions(MailOperation.Archive, MailOperation.SoftDelete)
            .Should().Be((MailOperation.Archive, MailOperation.SoftDelete));
        NotificationContentRules.ResolveMailActions(MailOperation.None, MailOperation.None)
            .Should().Be((MailOperation.MarkAsRead, MailOperation.SoftDelete));
        var (first, second) = NotificationContentRules.ResolveMailActions(MailOperation.Archive, MailOperation.Archive);
        first.Should().Be(MailOperation.Archive);
        second.Should().NotBe(MailOperation.Archive);
    }

    [Fact]
    public void ReminderContextDescribesTheStart()
    {
        var now = new DateTime(2026, 10, 14, 10, 0, 0);
        NotificationContentRules.GetCalendarReminderContext(now.AddMinutes(15), now)
            .Should().Be(string.Format(Translator.CalendarReminder_StartsInMinutes, 15));
        NotificationContentRules.GetCalendarReminderContext(now.AddSeconds(20), now).Should().Be(Translator.CalendarReminder_StartingNow);
        NotificationContentRules.GetCalendarReminderContext(now.AddHours(-2), now)
            .Should().Be(string.Format(Translator.CalendarReminder_StartedHoursAgo, 2));
    }

    [Fact]
    public void ReminderSnoozeIsLimitedToSupportedLengths()
    {
        NotificationContentRules.ResolveReminderSnoozeMinutes(30 * 60, 0, 15).Should().Be(15);
        NotificationContentRules.ResolveReminderSnoozeMinutes(0, 0, 15).Should().BeNull();
    }
}
