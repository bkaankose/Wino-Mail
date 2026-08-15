using FluentAssertions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Extensions;
using Xunit;

namespace Wino.Core.Tests.Services;

public class MailCopyIdentityExtensionsTests
{
    private static MailCopy BuildCopy(
        string serverId,
        Guid folderId,
        MailAccount? account = null,
        DateTime? creationDate = null)
        => new()
        {
            UniqueId = Guid.NewGuid(),
            Id = serverId,
            FolderId = folderId,
            ThreadId = "thread-1",
            CreationDate = creationDate ?? new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc),
            AssignedAccount = account
        };

    [Fact]
    public void CollapseServerMessageDuplicates_PrefersPreferredCopyRegardlessOfInputOrder()
    {
        var account = new MailAccount { Id = Guid.NewGuid() };
        var inboxFolderId = Guid.NewGuid();
        var categoryFolderId = Guid.NewGuid();

        // The category copy is listed first and is newer, but the inbox copy is the preferred one.
        var categoryCopy = BuildCopy("server-1", categoryFolderId, account, new DateTime(2026, 8, 15, 18, 0, 0, DateTimeKind.Utc));
        var inboxCopy = BuildCopy("server-1", inboxFolderId, account, new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc));

        var collapsed = new[] { categoryCopy, inboxCopy }
            .CollapseServerMessageDuplicates(null, mail => mail.FolderId == inboxFolderId)
            .ToList();

        collapsed.Should().ContainSingle();
        collapsed[0].UniqueId.Should().Be(inboxCopy.UniqueId);
    }

    [Fact]
    public void CollapseServerMessageDuplicates_KeepsRowsFromDifferentAccounts()
    {
        var firstAccount = new MailAccount { Id = Guid.NewGuid() };
        var secondAccount = new MailAccount { Id = Guid.NewGuid() };

        var collapsed = new[]
        {
            BuildCopy("shared-server-id", Guid.NewGuid(), firstAccount),
            BuildCopy("shared-server-id", Guid.NewGuid(), secondAccount)
        }
        .CollapseServerMessageDuplicates(null, _ => false)
        .ToList();

        collapsed.Should().HaveCount(2);
    }

    [Fact]
    public void CollapseServerMessageDuplicates_TreatsRowsWithoutServerIdAsDistinct()
    {
        var account = new MailAccount { Id = Guid.NewGuid() };

        var collapsed = new[]
        {
            BuildCopy(string.Empty, Guid.NewGuid(), account),
            BuildCopy(null!, Guid.NewGuid(), account)
        }
        .CollapseServerMessageDuplicates(null, _ => false)
        .ToList();

        collapsed.Should().HaveCount(2);
    }

    [Fact]
    public void CollapseServerMessageDuplicates_FallsBackToNewestWhenNoCopyIsPreferred()
    {
        var account = new MailAccount { Id = Guid.NewGuid() };

        var older = BuildCopy("server-1", Guid.NewGuid(), account, new DateTime(2026, 8, 15, 8, 0, 0, DateTimeKind.Utc));
        var newer = BuildCopy("server-1", Guid.NewGuid(), account, new DateTime(2026, 8, 15, 20, 0, 0, DateTimeKind.Utc));

        var collapsed = new[] { older, newer }
            .CollapseServerMessageDuplicates(null, _ => false)
            .ToList();

        collapsed.Should().ContainSingle();
        collapsed[0].UniqueId.Should().Be(newer.UniqueId);
    }

    [Fact]
    public void CollapseServerMessageDuplicates_GroupsUnhydratedCopiesThroughTheFolderMap()
    {
        var accountId = Guid.NewGuid();
        var inboxFolderId = Guid.NewGuid();
        var unreadFolderId = Guid.NewGuid();

        var accountIdsByFolderId = new Dictionary<Guid, Guid>
        {
            [inboxFolderId] = accountId,
            [unreadFolderId] = accountId
        };

        var collapsed = new[]
        {
            BuildCopy("server-1", unreadFolderId),
            BuildCopy("server-1", inboxFolderId)
        }
        .CollapseServerMessageDuplicates(accountIdsByFolderId, mail => mail.FolderId == inboxFolderId)
        .ToList();

        collapsed.Should().ContainSingle();
        collapsed[0].FolderId.Should().Be(inboxFolderId);
    }

    [Fact]
    public void ResolveAccountId_PrefersAssignedAccountOverFolderMap()
    {
        var assignedAccountId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var copy = BuildCopy("server-1", folderId, new MailAccount { Id = assignedAccountId });

        var resolved = copy.ResolveAccountId(new Dictionary<Guid, Guid> { [folderId] = Guid.NewGuid() });

        resolved.Should().Be(assignedAccountId);
    }

    [Fact]
    public void ResolveServerMailId_WithoutServerId_FallsBackToUniqueId()
    {
        var copy = BuildCopy(string.Empty, Guid.NewGuid());

        copy.ResolveServerMailId().Should().Be(copy.UniqueId.ToString("N"));
    }
}
