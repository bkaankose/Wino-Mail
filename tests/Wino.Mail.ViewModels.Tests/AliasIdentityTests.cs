using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using MimeKit;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public class AliasIdentityTests
{
    [Theory]
    [InlineData(" Sales@Example.com ", "sales@example.com", false)]
    [InlineData("sales@example.com", " SALES@example.com ", false)]
    [InlineData("missing@example.com", "sales@example.com", true)]
    public async Task Composer_SelectsTheMatchingOrDefaultObjectFromItsCollection(string draftFrom, string aliasAddress, bool expectDefault)
    {
        var account = new MailAccount { Id = Guid.NewGuid(), SenderName = "Account" };
        var primary = new MailAccountAlias { AliasAddress = "root@example.com", IsPrimary = true };
        var alias = new MailAccountAlias { AliasAddress = aliasAddress };
        var accounts = new Mock<IAccountService>();
        accounts.Setup(s => s.GetAccountAsync(account.Id)).ReturnsAsync(account);
        accounts.Setup(s => s.GetAccountAliasesAsync(account.Id)).ReturnsAsync([primary, alias]);
        var vm = Composer(accounts.Object, Mock.Of<IDraftSaveService>());
        vm.CurrentMailDraftItem = new MailItemViewModel(new MailCopy { UniqueId = Guid.NewGuid(), AssignedAccount = account, FromAddress = draftFrom });

        // Exercise the real account initialization without navigation's unrelated network/file work.
        var initialize = typeof(ComposePageViewModel).GetMethod("InitializeComposerAccountAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        (await (Task<bool>)initialize.Invoke(vm, null)!).Should().BeTrue();

        vm.SelectedAlias.Should().BeSameAs(expectDefault ? primary : alias);
        vm.AvailableAliases.Should().Contain(a => ReferenceEquals(a, vm.SelectedAlias));
        accounts.Verify(s => s.GetPrimaryAccountAliasAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task SaveDraft_AfterAliasSwitch_PersistsMatchingMimeAndLocalSender()
    {
        var save = new Mock<IDraftSaveService>();
        DraftUpdateSnapshot? captured = null;
        MailCopy? metadata = null;
        save.Setup(s => s.SaveAsync(It.IsAny<DraftUpdateSnapshot>(), It.IsAny<MailCopy>()))
            .Callback<DraftUpdateSnapshot, MailCopy>((snapshot, mail) => { captured = snapshot; metadata = mail; })
            .ReturnsAsync(true);
        var vm = Composer(Mock.Of<IAccountService>(), save.Object);
        vm.ComposingAccount = new MailAccount { Id = Guid.NewGuid(), SenderName = "Account" };
        vm.CurrentMailDraftItem = new MailItemViewModel(new MailCopy
        {
            UniqueId = Guid.NewGuid(), AssignedAccount = vm.ComposingAccount,
            FromAddress = "old@example.com", FromName = "Old name"
        });
        vm.CurrentMimeMessage = new MimeMessage();
        vm.SelectedAlias = new MailAccountAlias { AliasAddress = "new@example.com", AliasSenderName = "New name", ReplyToAddress = "reply@example.com" };
        vm.GetHTMLBodyFunction = () => Task.FromResult("<p>Draft</p>");

        await vm.SaveDraftAsync();

        captured.Should().NotBeNull();
        using var mime = captured!.OpenMime();
        var sender = mime.From.Mailboxes.Single();
        sender.Address.Should().Be("new@example.com");
        sender.Name.Should().Be("New name");
        metadata!.FromAddress.Should().Be(sender.Address);
        metadata.FromName.Should().Be(sender.Name);
        vm.CurrentMailDraftItem.FromName.Should().Be(sender.Name);
        mime.ReplyTo.Mailboxes.Single().Address.Should().Be("reply@example.com");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AliasSetting_UsesTargetedWriteAndReloadsAfterSuccessOrFailure(bool fails)
    {
        var accountId = Guid.NewGuid();
        var stale = new MailAccountAlias { Id = Guid.NewGuid(), AccountId = accountId, AliasAddress = "root@example.com", IsSmimeEncryptionEnabled = true };
        var reloaded = new MailAccountAlias { Id = stale.Id, AccountId = accountId, AliasAddress = stale.AliasAddress, IsSmimeEncryptionEnabled = !fails };
        var accounts = new Mock<IAccountService>();
        accounts.Setup(s => s.GetAccountAliasesAsync(accountId)).ReturnsAsync([reloaded]);
        accounts.Setup(s => s.SetAliasEncryptionAsync(accountId, stale.Id, true))
            .Returns(fails ? Task.FromException(new IOException("Write failed")) : Task.CompletedTask);
        var dialogs = new Mock<IMailDialogService>();
        var logger = new Mock<IWinoLogger>();
        var vm = new AliasManagementPageViewModel(dialogs.Object, accounts.Object, Certificates(), logger.Object)
        {
            Account = new MailAccount { Id = accountId }, AccountAliases = [stale]
        };

        await vm.SetAliasSmimeEncryption(stale, true);

        accounts.Verify(s => s.SetAliasEncryptionAsync(accountId, stale.Id, true), Times.Once);
        accounts.Verify(s => s.UpdateAccountAliasesAsync(It.IsAny<Guid>(), It.IsAny<List<MailAccountAlias>>()), Times.Never);
        vm.AccountAliases.Single().Should().BeSameAs(reloaded);
        vm.AccountAliases.Single().IsSmimeEncryptionEnabled.Should().Be(!fails);
        dialogs.Verify(s => s.InfoBarMessage(It.IsAny<string>(), It.IsAny<string>(), InfoBarMessageType.Error), fails ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task DefaultSelection_AndCertificateClearing_DoNotSubmitStaleAliasLists()
    {
        var alias = new MailAccountAlias { Id = Guid.NewGuid(), AccountId = Guid.NewGuid(), AliasAddress = "root@example.com", IsPrimary = true };
        var accounts = new Mock<IAccountService>();
        accounts.Setup(s => s.GetAccountAliasesAsync(alias.AccountId)).ReturnsAsync([alias]);
        var vm = new AliasManagementPageViewModel(Mock.Of<IMailDialogService>(), accounts.Object, Certificates(), Mock.Of<IWinoLogger>())
        {
            Account = new MailAccount { Id = alias.AccountId }, AccountAliases = [alias]
        };

        await vm.SetAliasPrimaryCommand.ExecuteAsync(alias);
        await vm.SetSelectedSigningCertificate(alias, null!);

        accounts.Verify(s => s.SetDefaultAccountAliasAsync(alias.AccountId, alias.Id), Times.Once);
        accounts.Verify(s => s.SetAliasSigningCertificateAsync(alias.AccountId, alias.Id, null!), Times.Once);
        accounts.Verify(s => s.UpdateAccountAliasesAsync(It.IsAny<Guid>(), It.IsAny<List<MailAccountAlias>>()), Times.Never);
    }

    [Fact]
    public async Task AddAlias_WhenServiceFindsConcurrentDuplicate_ReloadsWithoutSuccessMessage()
    {
        var accountId = Guid.NewGuid();
        var alias = new MailAccountAlias { Id = Guid.NewGuid(), AliasAddress = "new@example.com" };
        var created = Mock.Of<ICreateAccountAliasDialog>(d => d.CreatedAccountAlias == alias);
        var dialogs = new Mock<IMailDialogService>();
        dialogs.Setup(s => s.ShowCreateAccountAliasDialogAsync()).ReturnsAsync(created);
        var accounts = new Mock<IAccountService>();
        accounts.Setup(s => s.AddAccountAliasAsync(accountId, alias)).ReturnsAsync(false);
        accounts.Setup(s => s.GetAccountAliasesAsync(accountId)).ReturnsAsync([alias]);
        var vm = new AliasManagementPageViewModel(dialogs.Object, accounts.Object, Certificates(), Mock.Of<IWinoLogger>())
        {
            Account = new MailAccount { Id = accountId }, AccountAliases = []
        };

        await vm.AddNewAliasCommand.ExecuteAsync(null);

        accounts.Verify(s => s.AddAccountAliasAsync(accountId, alias), Times.Once);
        dialogs.Verify(s => s.ShowMessageAsync(It.IsAny<string>(), It.IsAny<string>(), WinoCustomMessageDialogIcon.Warning), Times.Once);
        dialogs.Verify(s => s.InfoBarMessage(It.IsAny<string>(), It.IsAny<string>(), InfoBarMessageType.Success), Times.Never);
        vm.AccountAliases.Should().ContainSingle().Which.Should().BeSameAs(alias);
    }

    private static ISmimeCertificateService Certificates()
    {
        var service = new Mock<ISmimeCertificateService>();
        service.Setup(s => s.GetCertificates(It.IsAny<StoreName>(), It.IsAny<StoreLocation>(), It.IsAny<string>()))
            .Returns(Array.Empty<X509Certificate2>());
        return service.Object;
    }

    private static ComposePageViewModel Composer(IAccountService accounts, IDraftSaveService save)
        => new(Mock.Of<IMailDialogService>(), Mock.Of<IMailService>(), Mock.Of<IMimeFileService>(), Mock.Of<IFileService>(),
            Mock.Of<INativeAppService>(), Mock.Of<IFolderService>(), accounts, Mock.Of<IEmailTemplateService>(),
            Mock.Of<IWinoRequestDelegator>(), Mock.Of<IContactService>(), Mock.Of<IFontService>(), Mock.Of<IPreferencesService>(),
            Certificates(), Mock.Of<IShareActivationService>(), Mock.Of<IDraftSyncRetryService>(), Mock.Of<IDraftUpdateCoordinator>(),
            new DraftUpdateRegistry(), save, Mock.Of<IRecipientSuggestionService>(), Mock.Of<IRecipientHistoryService>());
}
