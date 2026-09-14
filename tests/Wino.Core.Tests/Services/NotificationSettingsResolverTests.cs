using System;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Notifications;
using Xunit;

namespace Wino.Core.Tests.Services;

public class NotificationSettingsResolverTests
{
    [Fact]
    public void ResolveMail_UsesTheDefaultsWhileTheAccountFollowsThem()
    {
        var defaults = CreateDefaults();
        var account = CreateAccount();

        account.HasCustomNotificationSettings = false;
        account.NotificationScope = MailNotificationScope.AllFolders;
        account.AccountNotificationSoundEvent = NotificationSoundEvent.IM;

        var resolved = NotificationSettingsResolver.ResolveMail(defaults.Object, account);

        resolved.IsEnabled.Should().BeTrue();
        resolved.Scope.Should().Be(MailNotificationScope.InboxOnly);
        resolved.Sound.Should().Be(NotificationSoundEvent.Mail);
    }

    [Fact]
    public void ResolveMail_UsesTheAccountValuesOnceItCustomises()
    {
        var defaults = CreateDefaults();
        var account = CreateAccount();

        account.HasCustomNotificationSettings = true;
        account.NotificationScope = MailNotificationScope.FocusedInboxOnly;
        account.NotificationContent = MailNotificationContent.SenderOnly;
        account.AccountNotificationSoundEvent = NotificationSoundEvent.IM;

        var resolved = NotificationSettingsResolver.ResolveMail(defaults.Object, account);

        resolved.Scope.Should().Be(MailNotificationScope.FocusedInboxOnly);
        resolved.Content.Should().Be(MailNotificationContent.SenderOnly);
        resolved.Sound.Should().Be(NotificationSoundEvent.IM);
    }

    [Fact]
    public void ResolveMail_KeepsTheStoredOverridesWhenCustomisingIsTurnedOffAndOnAgain()
    {
        var defaults = CreateDefaults();
        var account = CreateAccount();

        account.HasCustomNotificationSettings = true;
        account.NotificationScope = MailNotificationScope.AllFolders;

        // Going back to the defaults must not erase the account's own choice, which is the whole
        // reason this is an explicit flag rather than nullable per-field overrides.
        account.HasCustomNotificationSettings = false;
        NotificationSettingsResolver.ResolveMail(defaults.Object, account).Scope.Should().Be(MailNotificationScope.InboxOnly);

        account.HasCustomNotificationSettings = true;
        NotificationSettingsResolver.ResolveMail(defaults.Object, account).Scope.Should().Be(MailNotificationScope.AllFolders);
    }

    [Fact]
    public void ResolveMail_AccountLevelOffWinsOverEverythingElse()
    {
        var defaults = CreateDefaults();
        var account = CreateAccount();

        account.IsNotificationsEnabled = false;
        account.HasCustomNotificationSettings = true;
        account.IsNewMailNotificationEnabled = true;

        NotificationSettingsResolver.ResolveMail(defaults.Object, account).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void ResolveMail_TypeLevelOffWinsOverAnAccountOptIn()
    {
        var defaults = CreateDefaults();
        var account = CreateAccount();

        defaults.Object.AreNewMailNotificationsEnabled = false;
        account.HasCustomNotificationSettings = true;
        account.IsNewMailNotificationEnabled = true;

        NotificationSettingsResolver.ResolveMail(defaults.Object, account).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void ResolveMail_FallsBackToTheDefaultsWhenThereIsNoAccount()
    {
        var defaults = CreateDefaults();

        // The aggregate "N new messages" toast is not about one mailbox, so it must not be treated
        // as a disabled account and silently dropped.
        var resolved = NotificationSettingsResolver.ResolveMail(defaults.Object, account: null);

        resolved.IsEnabled.Should().BeTrue();
        resolved.Sound.Should().Be(NotificationSoundEvent.Mail);
    }

    [Fact]
    public void ResolveCalendarRemindersEnabled_HonoursBothLevels()
    {
        var defaults = CreateDefaults();
        var account = CreateAccount();

        account.HasCustomNotificationSettings = true;
        account.AreCalendarRemindersEnabled = false;
        NotificationSettingsResolver.ResolveCalendarRemindersEnabled(defaults.Object, account).Should().BeFalse();

        account.AreCalendarRemindersEnabled = true;
        NotificationSettingsResolver.ResolveCalendarRemindersEnabled(defaults.Object, account).Should().BeTrue();

        defaults.Object.AreCalendarRemindersEnabled = false;
        NotificationSettingsResolver.ResolveCalendarRemindersEnabled(defaults.Object, account).Should().BeFalse();
    }

    private static Mock<IPreferencesService> CreateDefaults()
    {
        var defaults = new Mock<IPreferencesService>();

        defaults.SetupProperty(service => service.AreNewMailNotificationsEnabled, true);
        defaults.SetupProperty(service => service.AreCalendarRemindersEnabled, true);
        defaults.SetupProperty(service => service.MailNotificationScope, MailNotificationScope.InboxOnly);
        defaults.SetupProperty(service => service.MailNotificationContent, MailNotificationContent.SenderSubjectPreview);
        defaults.SetupProperty(service => service.MailNotificationSoundEvent, NotificationSoundEvent.Mail);

        return defaults;
    }

    private static MailAccountPreferences CreateAccount() => new()
    {
        Id = Guid.NewGuid(),
        AccountId = Guid.NewGuid(),
        IsNotificationsEnabled = true
    };
}
