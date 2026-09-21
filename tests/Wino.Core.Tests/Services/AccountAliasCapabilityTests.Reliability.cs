using FluentAssertions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Xunit;

namespace Wino.Core.Tests.Services;

public partial class AccountAliasCapabilityTests
{
    [Fact]
    public async Task ReplacementFailure_RollsBackDeletedAndInsertedRows()
    {
        var original = Alias(Guid.NewGuid(), "original@example.com", primary: true, root: true);
        await _accountService.UpdateAccountAliasesAsync(original.AccountId, [original]);
        var replacement = Alias(original.AccountId, "replacement@example.com");

        var write = () => _accountService.UpdateAccountAliasesAsync(original.AccountId, [replacement, replacement]);
        await write.Should().ThrowAsync<Exception>();

        (await _accountService.GetAccountAliasesAsync(original.AccountId)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(original);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshAndSettings_InEitherOrder_PreserveSelectedDefaultAndCertificate(bool refreshFirst)
    {
        var account = new MailAccount { Id = Guid.NewGuid(), Address = "root@example.com" };
        var root = Alias(account.Id, account.Address, primary: true, root: true);
        var selected = Alias(account.Id, "sales@example.com");
        await _accountService.UpdateAccountAliasesAsync(account.Id, [root, selected]);

        async Task Refresh() => await _accountService.UpdateRemoteAliasInformationAsync(account,
        [
            new RemoteAccountAlias { AliasAddress = root.AliasAddress, IsPrimary = true, IsRootAlias = true },
            new RemoteAccountAlias { AliasAddress = selected.AliasAddress, AliasSenderName = "Sales", ReplyToAddress = "reply@example.com" },
            new RemoteAccountAlias { AliasAddress = "new@example.com", IsPrimary = true, IsRootAlias = true }
        ]);
        async Task Edit()
        {
            await _accountService.SetDefaultAccountAliasAsync(account.Id, selected.Id);
            await _accountService.SetAliasSigningCertificateAsync(account.Id, selected.Id, "certificate");
            await _accountService.SetAliasEncryptionAsync(account.Id, selected.Id, true);
        }

        if (refreshFirst) { await Refresh(); await Edit(); }
        else { await Edit(); await Refresh(); }

        var aliases = await _accountService.GetAccountAliasesAsync(account.Id);
        aliases.Should().HaveCount(3);
        aliases.Where(a => a.IsPrimary).Should().ContainSingle().Which.Id.Should().Be(selected.Id);
        aliases.Where(a => a.IsRootAlias).Should().ContainSingle().Which.Id.Should().Be(root.Id);
        var saved = aliases.Single(a => a.Id == selected.Id);
        saved.SelectedSigningCertificateThumbprint.Should().Be("certificate");
        saved.IsSmimeEncryptionEnabled.Should().BeTrue();
        saved.AliasSenderName.Should().Be("Sales");
        saved.ReplyToAddress.Should().Be("reply@example.com");
    }

    [Fact]
    public async Task Refresh_PreservesMalformedLegacyFlags_UntilExplicitDefaultSelection()
    {
        var account = new MailAccount { Id = Guid.NewGuid(), Address = "root@example.com" };
        var first = Alias(account.Id, account.Address, primary: true, root: true);
        var second = Alias(account.Id, "second@example.com", primary: true, root: true);
        await _accountService.UpdateAccountAliasesAsync(account.Id, [first, second]);

        await _accountService.UpdateRemoteAliasInformationAsync(account,
            [new RemoteAccountAlias { AliasAddress = first.AliasAddress }]);
        var aliases = await _accountService.GetAccountAliasesAsync(account.Id);
        aliases.Should().OnlyContain(a => a.IsPrimary && a.IsRootAlias);

        await _accountService.SetDefaultAccountAliasAsync(account.Id, second.Id);
        aliases = await _accountService.GetAccountAliasesAsync(account.Id);
        aliases.Where(a => a.IsPrimary).Should().ContainSingle().Which.Id.Should().Be(second.Id);
        aliases.Should().OnlyContain(a => a.IsRootAlias);
    }

    [Fact]
    public async Task Refresh_WithoutDefault_PinsExistingFallbackInsteadOfNewProviderDefault()
    {
        var account = new MailAccount { Id = Guid.NewGuid(), Address = "z@example.com" };
        var fallback = Alias(account.Id, account.Address);
        await _accountService.UpdateAccountAliasesAsync(account.Id, [fallback]);

        await _accountService.UpdateRemoteAliasInformationAsync(account,
            [new RemoteAccountAlias { AliasAddress = "a@example.com", IsPrimary = true, IsRootAlias = true }]);

        var aliases = await _accountService.GetAccountAliasesAsync(account.Id);
        aliases.Where(a => a.IsPrimary).Should().ContainSingle().Which.Id.Should().Be(fallback.Id);
        aliases.Should().OnlyContain(a => !a.IsRootAlias);
    }

    [Theory]
    [InlineData(AliasSendCapability.Unknown, AliasSendCapability.Denied)]
    [InlineData(AliasSendCapability.Confirmed, AliasSendCapability.Confirmed)]
    public async Task Refresh_RetainsDenialUnlessProviderHasDefiniteInformation(AliasSendCapability remote, AliasSendCapability expected)
    {
        var account = new MailAccount { Id = Guid.NewGuid(), Address = "root@example.com" };
        var alias = Alias(account.Id, account.Address, primary: true, root: true);
        await _accountService.UpdateAccountAliasesAsync(account.Id, [alias]);
        await _accountService.UpdateAliasSendCapabilityAsync(account.Id, alias.AliasAddress, AliasSendCapability.Denied);

        await _accountService.UpdateRemoteAliasInformationAsync(account,
            [new RemoteAccountAlias { AliasAddress = alias.AliasAddress, SendCapability = remote }]);

        (await _accountService.GetAccountAliasesAsync(account.Id)).Single().SendCapability.Should().Be(expected);
    }

    [Fact]
    public async Task RefreshFailure_RollsBackEarlierUpdatesAndNewAliases()
    {
        var account = new MailAccount { Id = Guid.NewGuid(), Address = "root@example.com" };
        var original = Alias(account.Id, account.Address, primary: true, root: true);
        original.AliasSenderName = "Original";
        await _accountService.UpdateAccountAliasesAsync(account.Id, [original]);
        await _databaseService.Connection.ExecuteAsync("CREATE TRIGGER fail_alias_insert BEFORE INSERT ON MailAccountAlias BEGIN SELECT RAISE(ABORT, 'test failure'); END");

        var refresh = () => _accountService.UpdateRemoteAliasInformationAsync(account,
        [
            new RemoteAccountAlias { AliasAddress = original.AliasAddress, AliasSenderName = "Changed" },
            new RemoteAccountAlias { AliasAddress = "new@example.com" }
        ]);
        await refresh.Should().ThrowAsync<Exception>();
        (await _accountService.GetAccountAliasesAsync(account.Id)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(original);
    }

    [Fact]
    public async Task TargetedSettings_ChangeOnlyRequestedFields_AndRejectOtherAccounts()
    {
        var original = Alias(Guid.NewGuid(), "root@example.com", primary: true, root: true);
        original.AliasSenderName = "Name";
        original.ReplyToAddress = "reply@example.com";
        original.SendCapability = AliasSendCapability.Denied;
        original.IsSmimeEncryptionEnabled = true;
        await _accountService.UpdateAccountAliasesAsync(original.AccountId, [original]);
        await _accountService.SetAliasSigningCertificateAsync(original.AccountId, original.Id, "certificate");
        var saved = (await _accountService.GetAccountAliasesAsync(original.AccountId)).Single();
        saved.Should().BeEquivalentTo(original, o => o.Excluding(a => a.SelectedSigningCertificateThumbprint));
        saved.SelectedSigningCertificateThumbprint.Should().Be("certificate");

        await _accountService.SetAliasEncryptionAsync(original.AccountId, original.Id, false);
        await _accountService.SetAliasSigningCertificateAsync(original.AccountId, original.Id, null!);
        saved = (await _accountService.GetAccountAliasesAsync(original.AccountId)).Single();
        saved.IsSmimeEncryptionEnabled.Should().BeFalse();
        saved.SelectedSigningCertificateThumbprint.Should().BeNull();

        var wrongAccount = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _accountService.SetDefaultAccountAliasAsync(wrongAccount, original.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _accountService.SetAliasSigningCertificateAsync(wrongAccount, original.Id, "wrong"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _accountService.SetAliasEncryptionAsync(wrongAccount, original.Id, true));
        (await _accountService.GetAccountAliasesAsync(original.AccountId)).Single().Should().BeEquivalentTo(saved);
    }

    [Fact]
    public async Task MissingDefaultTarget_DoesNotClearCurrentDefault()
    {
        var original = Alias(Guid.NewGuid(), "root@example.com", primary: true);
        await _accountService.UpdateAccountAliasesAsync(original.AccountId, [original]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _accountService.SetDefaultAccountAliasAsync(original.AccountId, Guid.NewGuid()));
        (await _accountService.GetPrimaryAccountAliasAsync(original.AccountId)).Id.Should().Be(original.Id);
    }

    [Fact]
    public async Task AddAlias_RejectsNormalizedDuplicates_WithoutConsolidatingLegacyRows()
    {
        var accountId = Guid.NewGuid();
        var first = Alias(accountId, " Sales@Example.com ", primary: true);
        var second = Alias(accountId, "sales@example.com");
        await _accountService.UpdateAccountAliasesAsync(accountId, [first, second]);

        (await _accountService.AddAccountAliasAsync(accountId, Alias(accountId, " SALES@example.com "))).Should().BeFalse();
        (await _accountService.GetAccountAliasesAsync(accountId)).Should().HaveCount(2);
        (await _accountService.AddAccountAliasAsync(accountId, Alias(accountId, " sales+tag@example.com "))).Should().BeTrue();
        (await _accountService.GetAccountAliasesAsync(accountId)).Should().Contain(a => a.AliasAddress == "sales+tag@example.com");
    }

    [Fact]
    public async Task ConcurrentAdds_OnlyOneNormalizedAddressIsInserted()
    {
        var accountId = Guid.NewGuid();
        var results = await Task.WhenAll(
            _accountService.AddAccountAliasAsync(accountId, Alias(accountId, "Sales@example.com")),
            _accountService.AddAccountAliasAsync(accountId, Alias(accountId, " sales@example.com ")));
        results.Should().ContainSingle(result => result);
        (await _accountService.GetAccountAliasesAsync(accountId)).Should().ContainSingle();
    }

    [Fact]
    public async Task EmptyAccountRefresh_KeepsExistingInitializationPolicy()
    {
        var account = new MailAccount { Id = Guid.NewGuid(), Address = "root@example.com" };
        await _accountService.UpdateRemoteAliasInformationAsync(account, []);
        var root = (await _accountService.GetAccountAliasesAsync(account.Id)).Single();
        root.AliasAddress.Should().Be(account.Address);
        root.IsPrimary.Should().BeTrue();
        root.IsRootAlias.Should().BeTrue();
    }

    private static MailAccountAlias Alias(Guid accountId, string address, bool primary = false, bool root = false)
        => new() { Id = Guid.NewGuid(), AccountId = accountId, AliasAddress = address, IsPrimary = primary, IsRootAlias = root };
}
