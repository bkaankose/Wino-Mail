using FluentAssertions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.ViewModels.Search;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class MailSearchFolderResolverTests
{
    private static readonly Guid AccountA = Guid.NewGuid();
    private static readonly Guid AccountB = Guid.NewGuid();

    //  Account A: Inbox > Projects > Archive2024, Sent, and the sticky More placeholder.
    private static readonly MailItemFolder InboxA = Folder(AccountA, "inbox-a");
    private static readonly MailItemFolder ProjectsA = Folder(AccountA, "projects-a", "inbox-a");
    private static readonly MailItemFolder Archive2024A = Folder(AccountA, "archive-2024-a", "projects-a");
    private static readonly MailItemFolder SentA = Folder(AccountA, "sent-a");
    private static readonly MailItemFolder MoreA = new() { Id = Guid.NewGuid(), MailAccountId = AccountA, IsSticky = true };

    //  Account B: Inbox > Receipts, Sent.
    private static readonly MailItemFolder InboxB = Folder(AccountB, "inbox-b");
    private static readonly MailItemFolder ReceiptsB = Folder(AccountB, "receipts-b", "inbox-b");
    private static readonly MailItemFolder SentB = Folder(AccountB, "sent-b");

    private static readonly Dictionary<Guid, IReadOnlyList<IMailItemFolder>> FoldersByAccount = new()
    {
        [AccountA] = [InboxA, ProjectsA, Archive2024A, SentA, MoreA],
        [AccountB] = [InboxB, ReceiptsB, SentB],
    };

    [Fact]
    public async Task CurrentFolder_ReturnsOnlyTheActiveFoldersWithoutLoadingAccounts()
    {
        var loads = 0;

        var folders = await MailSearchFolderResolver.ResolveAsync(
            MailSearchScope.CurrentFolder,
            [ProjectsA],
            accountId => { loads++; return Task.FromResult(FoldersByAccount[accountId]); });

        folders.Should().Equal(ProjectsA);
        loads.Should().Be(0);
    }

    [Fact]
    public async Task Subfolders_AddsEveryNestedLevelOfTheActiveFolder()
    {
        var folders = await Resolve(MailSearchScope.Subfolders, InboxA);

        folders.Should().BeEquivalentTo(new[] { InboxA, ProjectsA, Archive2024A });
    }

    [Fact]
    public async Task AllFolders_StaysInsideTheActiveAccountAndSkipsPlaceholders()
    {
        var folders = await Resolve(MailSearchScope.AllFolders, SentA);

        folders.Should().BeEquivalentTo(new[] { InboxA, ProjectsA, Archive2024A, SentA });
    }

    [Fact]
    public async Task MergedFolder_ResolvesEachAccountSeparately()
    {
        var subfolders = await Resolve(MailSearchScope.Subfolders, InboxA, InboxB);
        var allFolders = await Resolve(MailSearchScope.AllFolders, InboxA, InboxB);

        subfolders.Should().BeEquivalentTo(new[] { InboxA, ProjectsA, Archive2024A, InboxB, ReceiptsB });
        allFolders.Should().BeEquivalentTo(new[] { InboxA, ProjectsA, Archive2024A, SentA, InboxB, ReceiptsB, SentB });
    }

    [Fact]
    public async Task Subfolders_ToleratesAParentCycle()
    {
        var first = Folder(AccountA, "cycle-1", "cycle-2");
        var second = Folder(AccountA, "cycle-2", "cycle-1");

        var folders = await MailSearchFolderResolver.ResolveAsync(
            MailSearchScope.Subfolders,
            [first],
            _ => Task.FromResult<IReadOnlyList<IMailItemFolder>>([first, second]));

        folders.Should().BeEquivalentTo(new[] { first, second });
    }

    private static Task<IReadOnlyList<IMailItemFolder>> Resolve(MailSearchScope scope, params IMailItemFolder[] active)
        => MailSearchFolderResolver.ResolveAsync(scope, active, accountId => Task.FromResult(FoldersByAccount[accountId]));

    private static MailItemFolder Folder(Guid accountId, string remoteId, string? parentRemoteId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            RemoteFolderId = remoteId,
            ParentRemoteFolderId = parentRemoteId,
            FolderName = remoteId,
        };
}
