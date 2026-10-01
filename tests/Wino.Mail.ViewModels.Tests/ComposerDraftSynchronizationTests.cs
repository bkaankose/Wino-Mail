using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public class ComposerDraftSynchronizationTests
{
    [Fact]
    public async Task Initialization_WhenMappingFinishedBeforeNavigation_ClearsSavingWithoutOverwritingEdits()
    {
        var (vm, mails, _, draft, registry) = CreateComposer();
        var notifications = new List<string?>();
        vm.CurrentMailDraftItem.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        var mapped = MappedCopy(draft);
        registry.ConfirmMapping(draft.AssignedAccount.Id, draft.UniqueId, DraftUpdateIdentity.From(mapped));

        await InvokeAsync(vm, "InitializeCurrentDraftAsync");

        vm.IsDraftBusy.Should().BeFalse();
        vm.IsLocalDraft.Should().BeFalse();
        notifications.Should().Contain(nameof(MailItemViewModel.IsLocalDraft));
        draft.Id.Should().Be(mapped.Id);
        draft.ImapUid.Should().Be(mapped.ImapUid);
        draft.ImapUidValidity.Should().Be(mapped.ImapUidValidity);
        draft.Subject.Should().Be("Unsaved subject edit");
        draft.PreviewText.Should().Be("Unsaved body edit");
        mails.Verify(s => s.GetSingleMailItemAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task StateRefresh_WhenAnotherRequestIsPending_KeepsSavingVisible()
    {
        var (vm, mails, synchronizer, draft, registry) = CreateComposer();
        registry.ConfirmMapping(draft.AssignedAccount.Id, draft.UniqueId, DraftUpdateIdentity.From(MappedCopy(draft)));
        synchronizer.Setup(s => s.HasPendingOperation(draft.UniqueId)).Returns(true);

        await InvokeAsync(vm, "InitializeCurrentDraftAsync");

        vm.IsLocalDraft.Should().BeFalse();
        vm.IsDraftBusy.Should().BeTrue();
    }

    [Fact]
    public async Task Mapping_AfterInitialization_UpdatesIdentityAndClearsSaving()
    {
        var (vm, mails, _, draft, registry) = CreateComposer();
        await InvokeAsync(vm, "InitializeCurrentDraftAsync");
        vm.IsDraftBusy.Should().BeTrue();

        registry.ConfirmMapping(draft.AssignedAccount.Id, draft.UniqueId, DraftUpdateIdentity.From(MappedCopy(draft)));

        vm.IsLocalDraft.Should().BeFalse();
        vm.IsDraftBusy.Should().BeFalse();
        draft.ImapUid.Should().Be(42);
        mails.Verify(s => s.GetSingleMailItemAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Mapping_AfterSwitchingDraft_DoesNotMutatePreviousOrNewDraft()
    {
        var (vm, _, _, draft, registry) = CreateComposer();
        await InvokeAsync(vm, "InitializeCurrentDraftAsync");
        var nextDraft = new MailCopy
        {
            UniqueId = Guid.NewGuid(), AssignedAccount = draft.AssignedAccount,
            IsDraft = true, DraftId = "localDraft_next", CreationDate = DateTime.UtcNow
        };
        vm.CurrentMailDraftItem = new MailItemViewModel(nextDraft);
        await InvokeAsync(vm, "InitializeCurrentDraftAsync");

        registry.ConfirmMapping(draft.AssignedAccount.Id, draft.UniqueId, DraftUpdateIdentity.From(MappedCopy(draft)));

        draft.IsLocalDraft.Should().BeTrue();
        nextDraft.IsLocalDraft.Should().BeTrue();
        vm.IsDraftBusy.Should().BeTrue();
    }

    [Fact]
    public async Task Mapping_AfterUnregistering_IsRetainedForReopening()
    {
        var (vm, _, _, draft, registry) = CreateComposer();
        await InvokeAsync(vm, "InitializeCurrentDraftAsync");
        typeof(ComposePageViewModel).GetMethod("UnregisterRecipients", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
        registry.ConfirmMapping(draft.AssignedAccount.Id, draft.UniqueId, DraftUpdateIdentity.From(MappedCopy(draft)));
        draft.IsLocalDraft.Should().BeTrue();

        await InvokeAsync(vm, "InitializeCurrentDraftAsync");

        vm.IsLocalDraft.Should().BeFalse();
        vm.IsDraftBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Delayed_mail_update_cannot_restore_obsolete_identity()
    {
        var (vm, _, _, draft, registry) = CreateComposer();
        var obsolete = MappedCopy(draft);
        await InvokeAsync(vm, "InitializeCurrentDraftAsync");
        registry.ConfirmMapping(draft.AssignedAccount.Id, draft.UniqueId,
            new("Drafts_43", obsolete.DraftId, "thread", 43, 123));

        typeof(ComposePageViewModel).GetMethod("OnMailUpdated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, new object[] { obsolete, EntityUpdateSource.Server,
                MailCopyChangeFlags.Id | MailCopyChangeFlags.DraftId | MailCopyChangeFlags.ThreadId });

        draft.Subject.Should().Be("Unsaved subject edit");
        draft.PreviewText.Should().Be("Unsaved body edit");
        draft.Id.Should().Be("Drafts_43");
        draft.ImapUid.Should().Be(43);
        vm.IsLocalDraft.Should().BeFalse();
        vm.IsDraftBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Queued_initialization_after_navigation_does_not_attach_observer()
    {
        var (vm, _, _, draft, registry) = CreateComposer();
        Action? queued = null;
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new Mock<IDispatcher>();
        dispatcher.Setup(d => d.ExecuteOnUIThread(It.IsAny<Action>())).Returns<Action>(action =>
        {
            queued = action;
            return dispatched.Task;
        });
        vm.Dispatcher = dispatcher.Object;
        var initialization = InvokeAsync(vm, "InitializeCurrentDraftAsync");
        typeof(ComposePageViewModel).GetMethod("UnregisterRecipients", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
        vm.Dispatcher = null;
        queued!();
        dispatched.SetResult();
        await initialization;

        registry.ConfirmMapping(draft.AssignedAccount.Id, draft.UniqueId, DraftUpdateIdentity.From(MappedCopy(draft)));

        draft.IsLocalDraft.Should().BeTrue();
    }

    [Fact]
    public async Task Mail_update_after_sending_does_not_reapply_draft_mapping()
    {
        var (vm, _, _, draft, registry) = CreateComposer();
        registry.ConfirmMapping(draft.AssignedAccount.Id, draft.UniqueId, DraftUpdateIdentity.From(MappedCopy(draft)));
        await InvokeAsync(vm, "InitializeCurrentDraftAsync");
        var sent = MappedCopy(draft);
        sent.IsDraft = false;
        sent.Id = "Sent_1";
        typeof(ComposePageViewModel).GetMethod("OnMailUpdated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, new object[] { sent, EntityUpdateSource.Server, MailCopyChangeFlags.All });

        draft.Id.Should().Be("Sent_1");
        draft.IsDraft.Should().BeFalse();
        vm.IsDraftBusy.Should().BeFalse();
    }

    private static Task InvokeAsync(ComposePageViewModel vm, string method)
        => (Task)typeof(ComposePageViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null)!;

    private static MailCopy MappedCopy(MailCopy draft) => new()
    {
        UniqueId = draft.UniqueId, AssignedAccount = draft.AssignedAccount, IsDraft = true,
        Id = "Drafts_42", DraftId = draft.UniqueId.ToString(), DraftSyncState = DraftSyncState.Synced,
        ImapUid = 42, ImapUidValidity = 123, Subject = "Original subject", PreviewText = "Original body"
    };

    private static (ComposePageViewModel Vm, Mock<IMailService> Mails, Mock<IWinoSynchronizerBase> Synchronizer, MailCopy Draft, DraftUpdateRegistry Registry) CreateComposer()
    {
        var account = new MailAccount { Id = Guid.NewGuid(), ProviderType = MailProviderType.IMAP4 };
        var draft = new MailCopy
        {
            UniqueId = Guid.NewGuid(), AssignedAccount = account, IsDraft = true, DraftId = "localDraft_initial",
            CreationDate = DateTime.UtcNow, Subject = "Unsaved subject edit", PreviewText = "Unsaved body edit"
        };
        var mails = new Mock<IMailService>();
        var synchronizer = new Mock<IWinoSynchronizerBase>();
        var manager = new Mock<ISynchronizationManager>();
        manager.Setup(s => s.GetSynchronizerAsync(account.Id)).ReturnsAsync(synchronizer.Object);
        var templates = new Mock<IEmailTemplateService>();
        templates.Setup(s => s.GetEmailTemplatesAsync()).ReturnsAsync(new List<EmailTemplate>());
        var certificates = new Mock<ISmimeCertificateService>();
        certificates.Setup(s => s.GetCertificates(It.IsAny<StoreName>(), It.IsAny<StoreLocation>(), It.IsAny<string>()))
            .Returns(Array.Empty<X509Certificate2>());
        var registry = new DraftUpdateRegistry();
        var vm = new ComposePageViewModel(Mock.Of<IMailDialogService>(), mails.Object, Mock.Of<IMimeFileService>(),
            Mock.Of<IFileService>(), Mock.Of<INativeAppService>(), Mock.Of<IFolderService>(), Mock.Of<IAccountService>(),
            templates.Object, Mock.Of<IWinoRequestDelegator>(), Mock.Of<IContactService>(), Mock.Of<IPreferencesService>(),
            certificates.Object, Mock.Of<IActivationStateService>(), Mock.Of<IDraftSyncRetryService>(),
            Mock.Of<IDraftUpdateCoordinator>(), registry, Mock.Of<IDraftSaveService>(),
            Mock.Of<IRecipientSuggestionService>(), Mock.Of<IRecipientHistoryService>(), synchronizationManager: manager.Object);
        vm.CurrentMailDraftItem = new MailItemViewModel(draft);
        return (vm, mails, synchronizer, draft, registry);
    }
}
