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

public partial class AccountAliasCapabilityTests : IAsyncLifetime
{
    private InMemoryDatabaseService _databaseService = null!;
    private AccountService _accountService = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();
        _accountService = CreateAccountService(_databaseService);
    }

    public async Task DisposeAsync() => await _databaseService.DisposeAsync();

    [Fact]
    public async Task CreateRootAliasAsync_SetsManualConfirmedDefaults()
    {
        var accountId = Guid.NewGuid();

        await _accountService.CreateRootAliasAsync(accountId, "root@example.com");

        var aliases = await _accountService.GetAccountAliasesAsync(accountId);
        var alias = aliases.Should().ContainSingle().Subject;

        alias.Source.Should().Be(AliasSource.Manual);
        alias.SendCapability.Should().Be(AliasSendCapability.Confirmed);
        alias.IsPrimary.Should().BeTrue();
        alias.IsRootAlias.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateRemoteAliasInformationAsync_PreservesManualAliasesWhileAddingProviderAliases()
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Alias Test",
            Address = "primary@example.com",
            ProviderType = MailProviderType.Outlook
        };

        await _databaseService.Connection.InsertAsync(account, typeof(MailAccount));

        await _accountService.UpdateAccountAliasesAsync(account.Id,
        [
            new MailAccountAlias
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                AliasAddress = "primary@example.com",
                ReplyToAddress = "primary@example.com",
                IsPrimary = true,
                IsRootAlias = true,
                Source = AliasSource.Manual,
                SendCapability = AliasSendCapability.Confirmed
            },
            new MailAccountAlias
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                AliasAddress = "custom@example.com",
                ReplyToAddress = "replies@example.com",
                Source = AliasSource.Manual,
                SendCapability = AliasSendCapability.Unknown
            }
        ]);

        await _accountService.UpdateRemoteAliasInformationAsync(account,
        [
            new RemoteAccountAlias
            {
                AliasAddress = "primary@example.com",
                ReplyToAddress = "primary@example.com",
                IsPrimary = true,
                IsRootAlias = true,
                Source = AliasSource.ProviderDiscovered,
                SendCapability = AliasSendCapability.Confirmed
            },
            new RemoteAccountAlias
            {
                AliasAddress = "sales@example.com",
                ReplyToAddress = "sales@example.com",
                Source = AliasSource.ProviderDiscovered,
                SendCapability = AliasSendCapability.Unknown
            }
        ]);

        var aliases = await _accountService.GetAccountAliasesAsync(account.Id);

        aliases.Should().Contain(a => a.AliasAddress == "custom@example.com" && a.Source == AliasSource.Manual);
        aliases.Should().Contain(a => a.AliasAddress == "sales@example.com" && a.Source == AliasSource.ProviderDiscovered);
    }

    [Fact]
    public async Task EnsurePrimaryAccountAliasAsync_RepairsAccountWithoutAliases()
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "No Alias",
            Address = "repair@example.com",
            ProviderType = MailProviderType.Outlook
        };
        await _databaseService.Connection.InsertAsync(account, typeof(MailAccount));

        var repaired = await _accountService.EnsurePrimaryAccountAliasAsync(account.Id);
        var again = await _accountService.EnsurePrimaryAccountAliasAsync(account.Id);

        repaired.Should().NotBeNull();
        repaired.AliasAddress.Should().Be("repair@example.com");
        repaired.IsPrimary.Should().BeTrue();
        repaired.IsRootAlias.Should().BeTrue();
        again.Id.Should().Be(repaired.Id);
        (await _accountService.GetAccountAliasesAsync(account.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task EnsurePrimaryAccountAliasAsync_ReturnsNullForUnknownAccount()
    {
        var alias = await _accountService.EnsurePrimaryAccountAliasAsync(Guid.NewGuid());

        alias.Should().BeNull();
    }

    private static AccountService CreateAccountService(InMemoryDatabaseService databaseService)
    {
        var preferencesService = new Mock<IPreferencesService>();
        var signatureService = new Mock<ISignatureService>();
        var mimeFileService = new Mock<IMimeFileService>();
        var contactPictureFileService = new Mock<IPictureStorageService>();

        return new AccountService(
            databaseService,
            signatureService.Object,
            Mock.Of<IAuthenticationProvider>(),
            mimeFileService.Object,
            preferencesService.Object,
            contactPictureFileService.Object,
            credentialPersistence: Wino.Core.Tests.Helpers.TestAccountCredentialPersistence.Instance);
    }
}
