using FluentAssertions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Synchronizers.Mail;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public class ImapKnownFolderBootstrapLifecycleTests
{
    [Theory]
    [InlineData(MailProviderType.IMAP4, true, ImapKnownFolderBootstrapState.Pending)]
    [InlineData(MailProviderType.IMAP4, false, ImapKnownFolderBootstrapState.NotRequired)]
    [InlineData(MailProviderType.POP3, true, ImapKnownFolderBootstrapState.NotRequired)]
    [InlineData(MailProviderType.Gmail, true, ImapKnownFolderBootstrapState.NotRequired)]
    public void GetInitialState_OnlyNewMailEnabledImapAccountsArePending(
        MailProviderType providerType,
        bool isMailAccessGranted,
        ImapKnownFolderBootstrapState expected)
        => ImapKnownFolderBootstrap.GetInitialState(providerType, isMailAccessGranted).Should().Be(expected);

    [Fact]
    public void ExistingAccount_DefaultsToNotRequired()
        => new MailAccount().ImapKnownFolderBootstrapState.Should().Be(ImapKnownFolderBootstrapState.NotRequired);

    [Fact]
    public async Task CompleteAsync_PersistsCompletedState()
    {
        var persistedState = ImapKnownFolderBootstrapState.NotRequired;
        var account = new MailAccount { ImapKnownFolderBootstrapState = ImapKnownFolderBootstrapState.Pending };

        await ImapKnownFolderBootstrap.CompleteAsync(account, value =>
        {
            persistedState = value.ImapKnownFolderBootstrapState;
            return Task.CompletedTask;
        });

        account.ImapKnownFolderBootstrapState.Should().Be(ImapKnownFolderBootstrapState.Completed);
        persistedState.Should().Be(ImapKnownFolderBootstrapState.Completed);
    }

    [Fact]
    public async Task CompleteAsync_WhenPersistenceFails_RemainsPending()
    {
        var account = new MailAccount { ImapKnownFolderBootstrapState = ImapKnownFolderBootstrapState.Pending };

        var action = () => ImapKnownFolderBootstrap.CompleteAsync(
            account,
            _ => Task.FromException(new InvalidOperationException("database unavailable")));

        await action.Should().ThrowAsync<InvalidOperationException>();
        account.ImapKnownFolderBootstrapState.Should().Be(ImapKnownFolderBootstrapState.Pending);
    }

    [Theory]
    [InlineData(SpecialFolderType.Inbox, true, true)]
    [InlineData(SpecialFolderType.Deleted, true, false)]
    [InlineData(SpecialFolderType.Other, false, false)]
    public void ApplyFolderRole_SetsConsistentSystemAndUnreadDefaults(
        SpecialFolderType role,
        bool isSystem,
        bool showUnread)
    {
        var folder = new MailItemFolder();

        ImapSynchronizer.ApplyFolderRole(folder, role);

        folder.SpecialFolderType.Should().Be(role);
        folder.IsSystemFolder.Should().Be(isSystem);
        folder.IsSynchronizationEnabled.Should().Be(isSystem);
        folder.IsSticky.Should().Be(isSystem);
        folder.ShowUnreadCount.Should().Be(showUnread);
    }
}
