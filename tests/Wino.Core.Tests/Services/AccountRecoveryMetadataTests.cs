using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class AccountRecoveryMetadataTests
{
    [Fact]
    public async Task MetadataRead_PreservesEndpointsAndPreferencesWithoutReadingOrReturningCredentialReferences()
    {
        await using var database = new InMemoryDatabaseService();
        await database.InitializeAsync();
        var policy = new Mock<IAccountCredentialPersistence>(MockBehavior.Strict);
        var account = new MailAccount { Id = Guid.NewGuid(), Name = "Recovery", Address = "person@example.test", ProviderType = MailProviderType.IMAP4 };
        var server = new CustomServerInformation
        {
            Id = Guid.NewGuid(), AccountId = account.Id, IncomingServer = "imap.example.test", OutgoingServer = "smtp.example.test",
            IncomingServerPassword = "opaque:incoming", OutgoingServerPassword = "opaque:outgoing", CalDavPassword = "opaque:dav"
        };
        var preferences = new MailAccountPreferences { Id = Guid.NewGuid(), AccountId = account.Id, IsSignatureEnabled = true };
        await database.Connection.InsertAsync(account);
        await database.Connection.InsertAsync(server);
        await database.Connection.InsertAsync(preferences);
        var service = CreateService(database, policy.Object);

        var metadata = await service.GetAccountMetadataAsync(account.Id);

        metadata.ServerInformation.IncomingServer.Should().Be(server.IncomingServer);
        metadata.ServerInformation.OutgoingServer.Should().Be(server.OutgoingServer);
        metadata.ServerInformation.IncomingServerPassword.Should().BeEmpty();
        metadata.ServerInformation.OutgoingServerPassword.Should().BeEmpty();
        metadata.ServerInformation.CalDavPassword.Should().BeEmpty();
        metadata.Preferences.IsSignatureEnabled.Should().BeTrue();
        metadata.Preferences.IsSignatureEnabled = false;
        var stored = await database.Connection.Table<CustomServerInformation>().FirstAsync();
        stored.IncomingServerPassword.Should().Be("opaque:incoming");
        stored.OutgoingServerPassword.Should().Be("opaque:outgoing");
        stored.CalDavPassword.Should().Be("opaque:dav");
        (await database.Connection.Table<MailAccountPreferences>().FirstAsync()).IsSignatureEnabled.Should().BeTrue();
        policy.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingCredential_StillFailsExplicitHydrationWhileMetadataRemainsAvailable()
    {
        await using var database = new InMemoryDatabaseService();
        await database.InitializeAsync();
        var account = new MailAccount { Id = Guid.NewGuid(), ProviderType = MailProviderType.IMAP4 };
        await database.Connection.InsertAsync(account);
        await database.Connection.InsertAsync(new CustomServerInformation { Id = Guid.NewGuid(), AccountId = account.Id, IncomingServerPassword = "opaque:missing" });
        var policy = new Mock<IAccountCredentialPersistence>(MockBehavior.Strict);
        policy.Setup(item => item.RestoreServerInformationSecretsAsync(It.IsAny<CustomServerInformation>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AccountCredentialMissingException());
        var service = CreateService(database, policy.Object);

        var hydrate = () => service.GetAccountAsync(account.Id);
        await hydrate.Should().ThrowAsync<AccountCredentialMissingException>();
        var metadata = await service.GetAccountMetadataAsync(account.Id);
        metadata.ServerInformation.IncomingServerPassword.Should().BeEmpty();
        policy.Verify(item => item.RestoreServerInformationSecretsAsync(It.IsAny<CustomServerInformation>(), It.IsAny<CancellationToken>()), Times.Once);
        policy.VerifyNoOtherCalls();
    }

    private static AccountService CreateService(InMemoryDatabaseService database, IAccountCredentialPersistence policy)
        => new(database, Mock.Of<ISignatureService>(), Mock.Of<IAuthenticationProvider>(), Mock.Of<IMimeFileService>(),
            Mock.Of<IPreferencesService>(), Mock.Of<IPictureStorageService>(), policy);
}
