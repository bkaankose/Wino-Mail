using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class IntelligenceMessageContextResolverTests : IAsyncLifetime
{
    private InMemoryDatabaseService _database = null!;
    private MailAccount _account = null!;
    private IntelligenceMessageContextResolver _resolver = null!;

    public async Task InitializeAsync()
    {
        _database = new InMemoryDatabaseService();
        await _database.InitializeAsync();

        _account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Address = "mail@example.test",
            ProviderType = MailProviderType.Outlook,
        };
        var folder = new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = _account.Id,
            FolderName = "test",
            RemoteFolderId = "custom-folder",
            SpecialFolderType = SpecialFolderType.Other,
            IsSynchronizationEnabled = true,
        };
        await _database.Connection.InsertAsync(_account);
        await _database.Connection.InsertAsync(folder);
        await _database.Connection.InsertAsync(new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = "provider-message-id",
            FolderId = folder.Id,
            FileId = Guid.NewGuid(),
            Subject = "Custom-folder message",
            FromAddress = "sender@example.test",
            CreationDate = DateTime.UtcNow,
        });

        var accounts = new Mock<IAccountService>();
        accounts.Setup(service => service.GetAccountAsync(_account.Id)).ReturnsAsync(_account);
        _resolver = new IntelligenceMessageContextResolver(
            _database,
            accounts.Object,
            Mock.Of<IMimeFileService>(),
            Mock.Of<ISynchronizationManager>());
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task GetCandidatesAsync_IncludesMessagesFromAnOrdinaryCustomFolder()
    {
        var candidates = await _resolver.GetCandidatesAsync(_account.Id);

        candidates.Should().ContainSingle(candidate =>
            candidate.ProviderMessageId == "provider-message-id" &&
            candidate.RemoteFolderIds.Contains("custom-folder"));
    }
}
