using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class AccountCredentialPersistenceTests
{
    [Fact]
    public async Task SessionReplacement_WritesPreparedCloneWithoutMutatingAuthenticatedAccount()
    {
        await using var database = new InMemoryDatabaseService();
        await database.InitializeAsync();
        var policy = new RevisionPolicy();
        var sessions = new WinoAccountSessionService(database, policy);
        var account = new WinoAccount { Id = Guid.NewGuid(), AccessToken = "access", RefreshToken = "refresh" };

        await sessions.ReplaceAsync(account, () => Task.CompletedTask);

        var stored = await database.Connection.Table<WinoAccount>().FirstAsync();
        stored.AccessToken.Should().StartWith("reference:");
        stored.RefreshToken.Should().Be(stored.AccessToken);
        account.AccessToken.Should().Be("access");
        account.RefreshToken.Should().Be("refresh");
        var api = new Moq.Mock<IWinoAccountApiClient>();
        var profile = new WinoAccountProfileService(database, api.Object, policy, sessionService: sessions);
        var hydrated = await profile.GetActiveAccountAsync();
        hydrated!.AccessToken.Should().Be("access");
        hydrated.RefreshToken.Should().Be("refresh");
        (await database.Connection.Table<WinoAccount>().FirstAsync()).AccessToken.Should().Be(stored.AccessToken);
    }

    [Fact]
    public async Task FailedCredentialPreparation_PreservesCurrentDatabaseAndSession()
    {
        await using var database = new InMemoryDatabaseService();
        await database.InitializeAsync();
        var policy = new RevisionPolicy();
        var sessions = new WinoAccountSessionService(database, policy);
        var original = new WinoAccount { Id = Guid.NewGuid(), AccessToken = "original", RefreshToken = "refresh" };
        await sessions.ReplaceAsync(original, () => Task.CompletedTask);
        var captured = (await sessions.CaptureAsync())!;
        var stored = (await database.Connection.Table<WinoAccount>().FirstAsync()).AccessToken;
        policy.FailPreparation = true;

        var replace = () => sessions.ReplaceAsync(new WinoAccount { Id = Guid.NewGuid(), AccessToken = "replacement" }, () => Task.CompletedTask);
        await replace.Should().ThrowAsync<InvalidOperationException>();

        (await database.Connection.Table<WinoAccount>().FirstAsync()).AccessToken.Should().Be(stored);
        (await sessions.IsCurrentAsync(captured)).Should().BeTrue();
        policy.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task SignOut_InvalidatesDatabaseReferencesBeforeDeletingAccountSecrets()
    {
        await using var database = new InMemoryDatabaseService();
        await database.InitializeAsync();
        var policy = new RevisionPolicy();
        var sessions = new WinoAccountSessionService(database, policy);
        var account = new WinoAccount { Id = Guid.NewGuid(), AccessToken = "access", RefreshToken = "refresh" };
        await sessions.ReplaceAsync(account, () => Task.CompletedTask);
        policy.BeforeDelete = async () => (await database.Connection.Table<WinoAccount>().CountAsync()).Should().Be(0);

        await sessions.ReplaceAsync(null, () => Task.CompletedTask);

        policy.Deleted.Should().Equal(account.Id);
    }

    private sealed class RevisionPolicy : IAccountCredentialPersistence
    {
        private readonly Dictionary<string, (string Access, string Refresh)> _revisions = [];
        public List<Guid> Deleted { get; } = [];
        public bool FailPreparation { get; set; }
        public Func<Task>? BeforeDelete { get; set; }
        public Task<WinoAccount> PrepareWinoAccountForStorageAsync(WinoAccount account, CancellationToken cancellationToken = default)
        {
            if (FailPreparation) throw new InvalidOperationException("Credential persistence unavailable.");
            var clone = account.CloneForCredentialStorage();
            var reference = "reference:" + Guid.NewGuid().ToString("N");
            _revisions.Add(reference, (account.AccessToken, account.RefreshToken));
            clone.AccessToken = clone.RefreshToken = reference;
            return Task.FromResult(clone);
        }
        public Task RestoreWinoAccountSecretsAsync(WinoAccount account, CancellationToken cancellationToken = default)
        {
            var secrets = _revisions[account.AccessToken];
            account.AccessToken = secrets.Access;
            account.RefreshToken = secrets.Refresh;
            return Task.CompletedTask;
        }
        public async Task DeleteWinoAccountSecretsAsync(Guid accountId, CancellationToken cancellationToken = default)
        {
            if (BeforeDelete is not null) await BeforeDelete();
            Deleted.Add(accountId);
        }
        public Task<CustomServerInformation> PrepareServerInformationForStorageAsync(CustomServerInformation information, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RestoreServerInformationSecretsAsync(CustomServerInformation information, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteMailAccountSecretsAsync(Guid accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
