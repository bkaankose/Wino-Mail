using System.Reflection;
using MimeKit;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Platform;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class SmimeCapabilityTests
{
    [Fact]
    public async Task UnavailableCertificateSettingsRejectCommandsAndDirectCallsWithoutNativeAccess()
    {
        var certificates = new Mock<ISmimeCertificateService>(MockBehavior.Strict);
        var dialogs = new Mock<IDialogServiceBase>(MockBehavior.Strict);
        var vm = new SignatureAndEncryptionPageViewModel(dialogs.Object, certificates.Object,
            Mock.Of<IFileService>(), new PlatformCapabilities());

        Assert.False(vm.ImportPersonalCertificatesCommand.CanExecute(null));
        Assert.False(vm.RemoveRecipientCertificatesCommand.CanExecute(null));
        Assert.False(vm.ExportPersonalCertificatesCommand.CanExecute(null));
        await vm.ImportPersonalCertificatesAsync();
        await vm.ImportRecipientCertificatesAsync();
        await vm.RemovePersonalCertificatesAsync();
        await vm.RemoveRecipientCertificatesAsync();
        await vm.ExportPersonalCertificatesAsync();
        await vm.ExportRecipientCertificatesAsync();
        certificates.VerifyNoOtherCalls();
        dialogs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnavailableAliasSettingsPreserveStoredValuesAndNeverWrite()
    {
        var accounts = new Mock<IAccountService>(MockBehavior.Strict);
        var certificates = new Mock<ISmimeCertificateService>(MockBehavior.Strict);
        var vm = new AliasManagementPageViewModel(Mock.Of<IMailDialogService>(), accounts.Object,
            certificates.Object, new PlatformCapabilities(), Mock.Of<IWinoLogger>());
        var alias = new MailAccountAlias { IsSmimeEncryptionEnabled = true, SelectedSigningCertificateThumbprint = "persisted" };

        await vm.SetAliasSmimeEncryption(alias, false);
        await vm.SetSelectedSigningCertificate(alias, null!);
        var row = new AliasManagementItem(alias, smimeAvailable: false);
        Assert.False(row.IsSmimeAvailable);
        Assert.False(row.IsSmimeEncryptionEnabled);
        Assert.True(alias.IsSmimeEncryptionEnabled);
        Assert.Equal("persisted", alias.SelectedSigningCertificateThumbprint);
        accounts.VerifyNoOtherCalls();
        certificates.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UnsupportedComposerFlagsCannotEnterNativeSigningOrEncryption(bool sign, bool encrypt)
    {
        var certificates = new Mock<ISmimeCertificateService>(MockBehavior.Strict);
        var dialogs = new Mock<IMailDialogService>();
        var worker = new Mock<IWinoRequestDelegator>(MockBehavior.Strict);
        var vm = Composer(dialogs.Object, certificates.Object, worker.Object);
        var alias = new MailAccountAlias
        {
            AliasAddress = "sender@example.com", IsSmimeEncryptionEnabled = true,
            SelectedSigningCertificateThumbprint = "persisted"
        };
        vm.SelectedAlias = alias;
        Assert.False(vm.IsSmimeSignatureEnabled);
        Assert.False(vm.IsSmimeEncryptionEnabled);
        Assert.True(alias.IsSmimeEncryptionEnabled);
        Assert.Equal("persisted", alias.SelectedSigningCertificateThumbprint);

        vm.ComposingAccount = new MailAccount { Id = Guid.NewGuid() };
        vm.CurrentMailDraftItem = new MailItemViewModel(new MailCopy { UniqueId = Guid.NewGuid(), DraftId = "server-draft" });
        vm.CurrentMimeMessage = new MimeMessage();
        Assert.True(vm.SendCommand.CanExecute(null));
        vm.IsSmimeSignatureEnabled = sign;
        vm.IsSmimeEncryptionEnabled = encrypt;
        Assert.False(vm.SendCommand.CanExecute(null));
        var send = typeof(ComposePageViewModel).GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)send.Invoke(vm, null)!;
        certificates.VerifyNoOtherCalls();
        worker.VerifyNoOtherCalls();
        Assert.Contains(dialogs.Invocations, invocation => invocation.Method.Name == nameof(IMailDialogService.InfoBarMessage));
    }

    private static ComposePageViewModel Composer(IMailDialogService dialogs, ISmimeCertificateService certificates, IWinoRequestDelegator worker)
        => new(dialogs, Mock.Of<IMailService>(), Mock.Of<IMimeFileService>(), Mock.Of<IFileService>(),
            Mock.Of<IFolderService>(), Mock.Of<IAccountService>(), Mock.Of<IEmailTemplateService>(), worker,
            Mock.Of<IContactService>(), Mock.Of<IPreferencesService>(), certificates, new PlatformCapabilities(),
            Mock.Of<IActivationStateService>(), Mock.Of<IDraftSyncRetryService>(), Mock.Of<IDraftUpdateCoordinator>(),
            new DraftUpdateRegistry(), Mock.Of<IDraftSaveService>(), Mock.Of<IRecipientSuggestionService>(),
            Mock.Of<IRecipientHistoryService>());
}
