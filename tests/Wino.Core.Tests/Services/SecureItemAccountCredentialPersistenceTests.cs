#nullable enable
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Tests.Helpers;
using Wino.Services.Security;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class SecureItemAccountCredentialPersistenceTests
{
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly InMemorySecureItemStore _store = new();

    private string Scope => $"mail-passwords.{_accountId:N}";

    [Fact]
    public async Task Prepare_WritesImmutableRevisionAndRestoreHydratesIt()
    {
        var persistence = new SecureItemAccountCredentialPersistence(_store);
        var information = Server("incoming", "outgoing", null);

        var stored = await persistence.PrepareServerInformationForStorageAsync(information);
        var second = await persistence.PrepareServerInformationForStorageAsync(Server("new", "outgoing", "dav"));

        stored.IncomingServerPassword.Should().StartWith($"wino-keychain:v1:mail-passwords:{_accountId:N}:");
        stored.OutgoingServerPassword.Should().Be(stored.IncomingServerPassword);
        stored.CalDavPassword.Should().Be(stored.IncomingServerPassword);
        information.IncomingServerPassword.Should().Be("incoming");
        second.IncomingServerPassword.Should().NotBe(stored.IncomingServerPassword);
        _store.Keys.Should().HaveCount(2).And.OnlyContain(key => key.Scope == Scope);

        await persistence.RestoreServerInformationSecretsAsync(stored);
        stored.IncomingServerPassword.Should().Be("incoming");
        stored.OutgoingServerPassword.Should().Be("outgoing");
        stored.CalDavPassword.Should().BeNull();
    }

    [Fact]
    public async Task NoSecrets_WritesNothingAndRestoreLeavesValues()
    {
        var persistence = new SecureItemAccountCredentialPersistence(_store);

        var stored = await persistence.PrepareServerInformationForStorageAsync(Server(null, "", null));
        await persistence.RestoreServerInformationSecretsAsync(stored);

        _store.Keys.Should().BeEmpty();
        stored.IncomingServerPassword.Should().BeNull();
        stored.OutgoingServerPassword.Should().BeEmpty();
    }

    [Fact]
    public async Task MissingRevision_ThrowsCredentialMissing()
    {
        var persistence = new SecureItemAccountCredentialPersistence(_store);
        var stored = await persistence.PrepareServerInformationForStorageAsync(Server("incoming", "outgoing", null));
        _store.DeleteScope(Scope);

        var act = () => persistence.RestoreServerInformationSecretsAsync(stored);

        await act.Should().ThrowAsync<AccountCredentialMissingException>();
    }

    [Fact]
    public async Task CorruptOrForeignRevision_ThrowsCryptographicException()
    {
        var persistence = new SecureItemAccountCredentialPersistence(_store);
        var stored = await persistence.PrepareServerInformationForStorageAsync(Server("incoming", "outgoing", null));
        var key = _store.Keys.Single();
        var original = _store.Read(key.Scope, key.Account);

        _store.Write(key.Scope, key.Account, Encoding.UTF8.GetBytes("{not json"));
        await RestoreCopy(persistence, stored).Should().ThrowAsync<CryptographicException>();

        var foreign = await new SecureItemAccountCredentialPersistence(_store).PrepareServerInformationForStorageAsync(
            new CustomServerInformation { AccountId = Guid.NewGuid(), IncomingServerPassword = "other" });
        var foreignKey = _store.Keys.Single(item => item.Scope != Scope);
        _store.Write(key.Scope, key.Account, _store.Read(foreignKey.Scope, foreignKey.Account) ?? []);
        await RestoreCopy(persistence, stored).Should().ThrowAsync<CryptographicException>();

        _store.Write(key.Scope, key.Account, original ?? []);
        var inconsistent = stored.CloneForCredentialStorage();
        inconsistent.OutgoingServerPassword = foreign.IncomingServerPassword;
        await persistence.Invoking(value => value.RestoreServerInformationSecretsAsync(inconsistent))
            .Should().ThrowAsync<CryptographicException>();
        await RestoreCopy(persistence, stored).Should().NotThrowAsync();
    }

    [Fact]
    public async Task UnhydratedReference_IsRejectedOnPrepare_AndDeleteRemovesTheScope()
    {
        var persistence = new SecureItemAccountCredentialPersistence(_store);
        var stored = await persistence.PrepareServerInformationForStorageAsync(Server("incoming", null, null));

        await persistence.Invoking(value => value.PrepareServerInformationForStorageAsync(stored))
            .Should().ThrowAsync<CryptographicException>();

        await persistence.DeleteMailAccountSecretsAsync(_accountId);
        _store.Keys.Should().BeEmpty();
    }

    private static Func<Task> RestoreCopy(SecureItemAccountCredentialPersistence persistence, CustomServerInformation stored)
        => () => persistence.RestoreServerInformationSecretsAsync(stored.CloneForCredentialStorage());

    private CustomServerInformation Server(string? incoming, string? outgoing, string? calDav) => new()
    {
        AccountId = _accountId,
        IncomingServerPassword = incoming,
        OutgoingServerPassword = outgoing,
        CalDavPassword = calDav,
    };
}
