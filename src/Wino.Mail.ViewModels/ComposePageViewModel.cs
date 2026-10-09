using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MimeKit;
using MimeKit.Cryptography;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Launch;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Attachments;
using Wino.Core.Domain.Models.Common;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Extensions;
using Wino.Core.Services;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

public partial class ComposePageViewModel : MailBaseViewModel,
    IRecipient<SynchronizationActionsCompleted>,
    IRecipient<AccountSynchronizerStateChanged>
{
    public event EventHandler CloseRequested;

    private static readonly TimeSpan LocalDraftRetryGracePeriod = TimeSpan.FromSeconds(15);

    public Func<Task<string>> GetHTMLBodyFunction;
    public Func<string, Task> RenderHtmlBodyAsyncFunc { get; set; }

    /// <summary>Wino Intelligence rewrite for this draft. Hidden unless the account is eligible.</summary>
    public ComposerRewriteSession RewriteSession { get; }

    public override async Task KeyboardShortcutHook(KeyboardShortcutTriggerDetails args)
    {
        if (args.Handled || args.Mode != WinoApplicationMode.Mail)
            return;

        if (args.Action == KeyboardShortcutAction.Send)
        {
            await SendAsync();
            args.Handled = true;
        }
    }

    // When we send the message or discard it, we need to block the mime update
    // Update is triggered when we leave the page.
    private bool isUpdatingMimeBlocked = false;

    private bool canSendMail => ComposingAccount != null && !IsLocalDraft && CurrentMimeMessage != null && !IsDraftBusy;
    private bool canSendWithRequestedSmime => canSendMail && CanUseRequestedSmime;
    private bool canSendLocalDraftToServer => ComposingAccount != null && IsLocalDraft && CurrentMimeMessage != null && !IsDraftBusy && !IsRetryingSendToServer;

    [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendToServerCommand))]
    [ObservableProperty]
    public partial MimeMessage CurrentMimeMessage { get; set; } = null;

    private readonly BodyBuilder bodyBuilder = new BodyBuilder();

    public bool IsLocalDraft => CurrentMailDraftItem?.MailCopy?.IsLocalDraft ?? true;

    #region Properties

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalDraft))]
    [NotifyPropertyChangedFor(nameof(ShouldShowSendToServerButton))]
    [NotifyPropertyChangedFor(nameof(ShouldShowSendButton))]
    [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendToServerCommand))]
    public partial MailItemViewModel CurrentMailDraftItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShouldShowSendToServerButton))]
    [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendToServerCommand))]
    public partial bool IsDraftBusy { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendToServerCommand))]
    public partial bool IsRetryingSendToServer { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DraftSyncErrorMessage))]
    public partial bool IsDraftSyncFailed { get; set; }

    [ObservableProperty]
    public partial bool IsImportanceSelected { get; set; }

    [ObservableProperty]
    public partial MessageImportance SelectedMessageImportance { get; set; }

    [ObservableProperty]
    public partial bool IsCCBCCVisible { get; set; }

    [ObservableProperty]
    public partial string Subject { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendToServerCommand))]
    public partial MailAccount ComposingAccount { get; set; }

    [ObservableProperty]
    public partial List<MailAccountAlias> AvailableAliases { get; set; }
    [ObservableProperty]
    public partial MailAccountAlias SelectedAlias { get; set; }
    [ObservableProperty]
    public partial bool IsDraggingOverComposerGrid { get; set; }
    [ObservableProperty]
    public partial bool IsDraggingOverFilesDropZone { get; set; }
    [ObservableProperty]
    public partial bool IsDraggingOverImagesDropZone { get; set; }
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial bool IsSmimeSignatureEnabled { get; set; }
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial bool IsSmimeEncryptionEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsReadReceiptRequested { get; set; }

    [ObservableProperty]
    public partial X509Certificate2 SelectedSigningCertificate { get; set; }

    public ObservableCollection<X509Certificate2> AvailableCertificates = [];

    public bool AreCertificatesAvailable => IsSmimeAvailable && AvailableCertificates.Count > 0;

    public ObservableCollection<EmailTemplate> AvailableEmailTemplates { get; } = [];
    public ObservableCollection<MailAttachmentViewModel> IncludedAttachments { get; } = [];

    public string AttachmentsSummary
    {
        get
        {
            var totalSize = IncludedAttachments.Sum(a => (long)(a.Content?.Length ?? 0)).GetBytesReadable();

            return IncludedAttachments.Count == 1
                ? string.Format(Translator.Composer_AttachmentsSummarySingle, totalSize)
                : string.Format(Translator.Composer_AttachmentsSummary, IncludedAttachments.Count, totalSize);
        }
    }
    public ObservableCollection<MailAccount> Accounts { get; set; } = [];
    public ObservableCollection<AccountContact> ToItems { get; set; } = [];
    public ObservableCollection<AccountContact> CCItems { get; set; } = [];
    public ObservableCollection<AccountContact> BCCItems { get; set; } = [];
    public bool ShouldShowSendToServerButton => IsLocalDraft && !IsDraftBusy;
    public bool ShouldShowSendButton => !IsLocalDraft;
    public string DraftSyncErrorMessage
    {
        get
        {
            var error = CurrentMailDraftItem?.MailCopy?.LastDraftSyncError;
            return string.IsNullOrWhiteSpace(error)
                ? Translator.Draft_SyncFailedInfoBarMessage
                : $"{Translator.Draft_SyncFailedInfoBarMessage}\n{error}";
        }
    }

    #endregion


    private readonly IMailDialogService _dialogService;
    private readonly IMailService _mailService;
    private readonly IMimeFileService _mimeFileService;
    private readonly IFileService _fileService;
    private readonly IFolderService _folderService;
    private readonly IAccountService _accountService;
    private readonly IEmailTemplateService _emailTemplateService;
    private readonly IWinoRequestDelegator _worker;
    public readonly IPreferencesService PreferencesService;
    public readonly IContactService ContactService;
    public readonly IRecipientSuggestionService RecipientSuggestionService;
    private readonly IRecipientHistoryService _recipientHistoryService;
    public readonly ISmimeCertificateService _smimeCertificateService;
    public bool IsSmimeAvailable { get; }
    private readonly IActivationStateService _activationStateService;
    private readonly IDraftSyncRetryService _draftSyncRetryService;
    private readonly IDraftUpdateCoordinator _draftUpdates;
    private readonly DraftUpdateRegistry _draftRegistry;
    private IDisposable _draftMappingSubscription;
    private DraftMappingLifecycle _observedDraftMapping;
    private int _draftMappingGeneration;
    private readonly IDraftSaveService _draftSaveService;
    private readonly IAttachmentFileService _attachmentFileService;
    private readonly ISynchronizationManager _synchronizationManager;

    public ComposePageViewModel(IMailDialogService dialogService,
                                IMailService mailService,
                                IMimeFileService mimeFileService,
                                IFileService fileService,
                                IFolderService folderService,
                                IAccountService accountService,
                                IEmailTemplateService emailTemplateService,
                                IWinoRequestDelegator worker,
                                IContactService contactService,
                                IPreferencesService preferencesService,
                                ISmimeCertificateService smimeCertificateService,
                                IPlatformCapabilities platformCapabilities,
                                IActivationStateService activationStateService,
                                IDraftSyncRetryService draftSyncRetryService,
                                IDraftUpdateCoordinator draftUpdates, DraftUpdateRegistry draftRegistry,
                                IDraftSaveService draftSaveService,
                                IRecipientSuggestionService recipientSuggestionService,
                                IRecipientHistoryService recipientHistoryService,
                                IAttachmentFileService attachmentFileService = null,
                                IWinoIntelligenceCoordinator intelligenceCoordinator = null,
                                ISynchronizationManager synchronizationManager = null,
                                ISignatureService signatureService = null)
    {
        _signatureService = signatureService;
        ContactService = contactService;
        RecipientSuggestionService = recipientSuggestionService;
        _recipientHistoryService = recipientHistoryService;
        PreferencesService = preferencesService;

        _folderService = folderService;
        _dialogService = dialogService;
        _mailService = mailService;
        _mimeFileService = mimeFileService;
        _fileService = fileService;
        _accountService = accountService;
        _emailTemplateService = emailTemplateService;
        _worker = worker;
        _smimeCertificateService = smimeCertificateService;
        IsSmimeAvailable = platformCapabilities.Smime;
        _activationStateService = activationStateService;
        _draftSyncRetryService = draftSyncRetryService;
        _draftUpdates = draftUpdates;
        _draftRegistry = draftRegistry;
        _draftSaveService = draftSaveService;
        _attachmentFileService = attachmentFileService;
        _synchronizationManager = synchronizationManager ?? SynchronizationManager.Instance;

        RewriteSession = new ComposerRewriteSession(
            intelligenceCoordinator,
            async () => GetHTMLBodyFunction == null ? null : await GetHTMLBodyFunction(),
            html => RenderHtmlBodyAsyncFunc?.Invoke(html) ?? Task.CompletedTask,
            () => ComposingAccount?.Id,
            error =>
            {
                if (RewriteErrorHandler != null) RewriteErrorHandler(error);
                else _dialogService.InfoBarMessage(Translator.Composer_AiErrorTitle, error, InfoBarMessageType.Error);
            });

        IncludedAttachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(AttachmentsSummary));

        foreach (var cert in IsSmimeAvailable
            ? _smimeCertificateService.GetCertificates(emailAddress: SelectedAlias?.AliasAddress)
            : Array.Empty<X509Certificate2>())
        {
            if (cert != null)
            {
                AvailableCertificates.Add(cert);
            }
        }
    }

    private bool CanUseRequestedSmime => IsSmimeAvailable || (!IsSmimeSignatureEnabled && !IsSmimeEncryptionEnabled);

    private void ReleaseSigningCertificates()
    {
        SelectedSigningCertificate = null;
        foreach (var certificate in AvailableCertificates) certificate.Dispose();
        AvailableCertificates.Clear();
        OnPropertyChanged(nameof(AreCertificatesAvailable));
    }

    partial void OnSelectedAliasChanged(MailAccountAlias value)
    {
        ReleaseSigningCertificates();
        if (!IsSmimeAvailable)
        {
            IsSmimeSignatureEnabled = false;
            IsSmimeEncryptionEnabled = false;
            return;
        }

        if (value != null)
        {
            IsSmimeSignatureEnabled = value.SelectedSigningCertificateThumbprint != null;
            IsSmimeEncryptionEnabled = value.IsSmimeEncryptionEnabled;

            AvailableCertificates.Clear();
            var certs = _smimeCertificateService.GetCertificates(emailAddress: SelectedAlias.AliasAddress);
            foreach (var cert in certs)
            {
                AvailableCertificates.Add(cert);
            }
            SelectedSigningCertificate = AvailableCertificates
                .Where(c => c.Thumbprint == SelectedAlias.SelectedSigningCertificateThumbprint).FirstOrDefault() ?? AvailableCertificates.FirstOrDefault();
        }
        OnPropertyChanged(nameof(AreCertificatesAvailable));
    }

    partial void OnSelectedSigningCertificateChanged(X509Certificate2 value)
    {
        IsSmimeSignatureEnabled = IsSmimeAvailable && value != null;
    }

    [RelayCommand]
    private async Task OpenAttachmentAsync(MailAttachmentViewModel attachmentViewModel)
    {
        if (string.IsNullOrEmpty(attachmentViewModel.FilePath)) return;

        try
        {
            attachmentViewModel.IsBusy = true;
            var detection = await attachmentViewModel.GetInspectionAsync();
            var source = attachmentViewModel.CreateFileSource(AttachmentFileOrigin.Local);
            var result = await _attachmentFileService.OpenAsync(
                source,
                Path.GetTempPath(),
                detection,
                mismatchApproved: false);

            if (result.Status == AttachmentFileOperationStatus.ConfirmationRequired)
            {
                var approved = await _dialogService.ShowWinoCustomMessageDialogAsync(
                    Translator.Attachment_ContentTypeMismatchTitle,
                    string.Format(
                        Translator.Attachment_ContentTypeMismatchMessage,
                        attachmentViewModel.FileName,
                        detection.Description ?? detection.Label ?? Translator.Attachment_UnknownContentType),
                    Translator.Attachment_OpenAnyway,
                    WinoCustomMessageDialogIcon.Warning,
                    Translator.Buttons_Cancel);

                if (approved)
                {
                    result = await _attachmentFileService.OpenAsync(
                        source,
                        Path.GetTempPath(),
                        detection,
                        mismatchApproved: true);
                }
            }

            if (result.Status is AttachmentFileOperationStatus.Failed or AttachmentFileOperationStatus.PolicyBlocked or AttachmentFileOperationStatus.Unavailable)
                throw new IOException(result.ErrorMessage);
        }
        catch
        {
            _dialogService.InfoBarMessage(Translator.Info_FailedToOpenFileTitle, Translator.Info_FailedToOpenFileMessage, InfoBarMessageType.Error);
        }
        finally
        {
            attachmentViewModel.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAttachmentAsync(MailAttachmentViewModel attachmentViewModel)
    {
        if (attachmentViewModel.Content == null) return;
        var pickedFilePath = await _dialogService.PickFilePathAsync(attachmentViewModel.FileName);
        if (string.IsNullOrWhiteSpace(pickedFilePath)) return;

        try
        {
            var result = await _attachmentFileService.SaveAsync(
                attachmentViewModel.CreateFileSource(AttachmentFileOrigin.Local),
                Path.GetDirectoryName(pickedFilePath)!,
                Path.GetFileName(pickedFilePath));

            if (!result.IsSuccess)
                throw new IOException(result.ErrorMessage);
        }
        catch
        {
            _dialogService.InfoBarMessage(Translator.Info_FailedToOpenFileTitle, Translator.Info_FailedToOpenFileMessage, InfoBarMessageType.Error);
        }
    }

    [RelayCommand]
    private async Task AttachFilesAsync()
    {
        var pickedFiles = await _dialogService.PickFilesAsync("*");

        if (pickedFiles?.Count == 0) return;

        foreach (var file in pickedFiles)
        {
            AddAttachment(file);
        }
    }

    public void AddAttachment(SharedFile sharedFile)
    {
        var attachmentViewModel = new MailAttachmentViewModel(sharedFile);
        IncludedAttachments.Add(attachmentViewModel);
        BeginAttachmentInspection(attachmentViewModel, AttachmentFileOrigin.Local);
    }

    [RelayCommand]
    private void RemoveAttachment(MailAttachmentViewModel attachmentViewModel)
        => IncludedAttachments.Remove(attachmentViewModel);

    [RelayCommand]
    private void RemoveAllAttachments() => IncludedAttachments.Clear();

    [RelayCommand(CanExecute = nameof(canSendWithRequestedSmime))]
    private async Task SendAsync()
    {
        bool shouldSign = IsSmimeSignatureEnabled;
        bool shouldEncrypt = IsSmimeEncryptionEnabled;
        if (!IsSmimeAvailable && (shouldSign || shouldEncrypt))
        {
            _dialogService.InfoBarMessage(Translator.Info_UnsupportedFunctionalityTitle,
                Translator.Info_UnsupportedFunctionalityDescription, InfoBarMessageType.Warning);
            return;
        }

        // TODO: More detailed mail validations.

        if (!ToItems.Any())
        {
            await _dialogService.ShowMessageAsync(Translator.DialogMessage_ComposerMissingRecipientMessage,
                                                 Translator.DialogMessage_ComposerValidationFailedTitle,
                                                 WinoCustomMessageDialogIcon.Warning);
            return;
        }

        if (string.IsNullOrEmpty(Subject))
        {
            var isConfirmed = await _dialogService.ShowConfirmationDialogAsync(Translator.DialogMessage_EmptySubjectConfirmationMessage, Translator.DialogMessage_EmptySubjectConfirmation, Translator.Buttons_Yes);

            if (!isConfirmed) return;
        }

        if (SelectedAlias == null)
        {
            _dialogService.InfoBarMessage(Translator.DialogMessage_AliasNotSelectedTitle, Translator.DialogMessage_AliasNotSelectedMessage, InfoBarMessageType.Error);
            return;
        }

        // Save mime changes before sending.
        if (!await UpdateMimeChangesAsync().ConfigureAwait(false)) return;

        isUpdatingMimeBlocked = true;
        var currentDraft = await _draftUpdates.StopAsync(ComposingAccount.Id, CurrentMailDraftItem.UniqueId).ConfigureAwait(false);
        if (currentDraft != null)
            await ExecuteUIThread(() => DraftUpdateIdentity.From(currentDraft).Apply(CurrentMailDraftItem.MailCopy));

        var assignedAccount = CurrentMailDraftItem.MailCopy.AssignedAccount;
        var sentFolder = await _folderService.GetSpecialFolderByAccountIdAsync(assignedAccount.Id, SpecialFolderType.Sent);


        if (shouldSign || shouldEncrypt)
        {
            var ownedCertificates = new List<X509Certificate2>();
            IReadOnlyList<X509Certificate2> GetSendCertificates(string email, SmimeCertificatePurpose purpose = SmimeCertificatePurpose.Personal)
            {
                var certificates = _smimeCertificateService.GetCertificates(purpose, email);
                ownedCertificates.AddRange(certificates);
                return certificates;
            }

            try
            {
                // Load alias certs
                var certs = GetSendCertificates(SelectedAlias.AliasAddress);
                using var secureMimeContext = _smimeCertificateService.CreateContext();

                if (shouldSign)
                {
                    var signingCertificate = !string.IsNullOrEmpty(SelectedAlias.SelectedSigningCertificateThumbprint)
                        ? certs.FirstOrDefault(c => c?.Thumbprint == SelectedAlias.SelectedSigningCertificateThumbprint)
                        : null;

                    var signer = new CmsSigner(signingCertificate) { DigestAlgorithm = DigestAlgorithm.Sha1 };

                    if (shouldEncrypt)
                    {
                        var recipients = new CmsRecipientCollection();
                        var cmsRecipients = CurrentMimeMessage.To.Mailboxes
                            .Select(mailbox => new CmsRecipient(
                                GetSendCertificates(mailbox.Address).FirstOrDefault() ?? GetSendCertificates(mailbox.Address, SmimeCertificatePurpose.Recipient).FirstOrDefault()
                            ));
                        foreach (var recipient in cmsRecipients)
                        {
                            recipients.Add(recipient);
                        }

                        CurrentMimeMessage.Body = ApplicationPkcs7Mime.SignAndEncrypt(
                            secureMimeContext,
                            signer,
                            recipients,
                            CurrentMimeMessage.Body);
                    }
                    else
                    {
                        // CurrentMimeMessage.Body = MultipartSigned.Create(signer, CurrentMimeMessage.Body);
                        CurrentMimeMessage.Body = ApplicationPkcs7Mime.Sign(
                            secureMimeContext,
                            signer,
                            CurrentMimeMessage.Body);
                    }
                }
                else if (shouldEncrypt)
                {
                    // var encryptionCertificate = !string.IsNullOrEmpty(SelectedAlias.SelectedEncryptionCertificateThumbprint)
                    //     ? certs.FirstOrDefault(c => c?.Thumbprint == SelectedAlias.SelectedEncryptionCertificateThumbprint)
                    //     : null;
                    // Encrypt the message if encryption certificate is selected.
                    CurrentMimeMessage.Body = ApplicationPkcs7Mime.Encrypt(
                        secureMimeContext,
                        CurrentMimeMessage.To.Mailboxes,
                        CurrentMimeMessage.Body);
                }

            }
            finally
            {
                foreach (var certificate in ownedCertificates) certificate.Dispose();
            }
        }

        using MemoryStream memoryStream = new();
        CurrentMimeMessage.WriteTo(FormatOptions.Default, memoryStream);
        var base64EncodedMessage = Convert.ToBase64String(memoryStream.ToArray());
        var draftSendPreparationRequest = new SendDraftPreparationRequest(CurrentMailDraftItem.MailCopy,
                                                                          SelectedAlias,
                                                                          sentFolder,
                                                                          CurrentMailDraftItem.MailCopy.AssignedFolder,
                                                                          CurrentMailDraftItem.MailCopy.AssignedAccount.Preferences,
                                                                          base64EncodedMessage);

        await ExecuteUIThread(() =>
        {
            IsDraftBusy = true;
        });

        await _worker.ExecuteAsync(draftSendPreparationRequest);
    }

    [RelayCommand(CanExecute = nameof(canSendLocalDraftToServer))]
    private async Task SendToServerAsync()
    {
        if (CurrentMailDraftItem?.MailCopy == null || ComposingAccount == null || CurrentMimeMessage == null)
            return;

        try
        {
            await ExecuteUIThread(() =>
            {
                IsRetryingSendToServer = true;
                IsDraftBusy = true;
                NotifyComposeActionStateChanged();
            });

            await UpdateMimeChangesAsync().ConfigureAwait(false);

            await _draftSyncRetryService.RetryNowAsync(CurrentMailDraftItem.MailCopy).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dialogService.InfoBarMessage(Translator.Info_RequestCreationFailedTitle, ex.Message, InfoBarMessageType.Error);
        }
        finally
        {
            await ExecuteUIThread(() =>
            {
                IsRetryingSendToServer = false;
            });

            await UpdatePendingOperationStateAsync().ConfigureAwait(false);

            await ExecuteUIThread(() =>
            {
                NotifyComposeActionStateChanged();
            });
        }
    }

    public async Task<bool> UpdateMimeChangesAsync()
    {
        Guid accountId = Guid.Empty;
        Guid uniqueId = Guid.Empty;
        await ExecuteUIThread(() =>
        {
            if (!isUpdatingMimeBlocked && CurrentMimeMessage != null && ComposingAccount != null && CurrentMailDraftItem != null)
            {
                accountId = ComposingAccount.Id;
                uniqueId = CurrentMailDraftItem.UniqueId;
            }
        }).ConfigureAwait(false);
        if (accountId == Guid.Empty) return false;

        var entry = _draftRegistry.Get(accountId, uniqueId);
        await entry.SaveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            MailCopy metadata = null;
            byte[] bytes = null;
            await ExecuteUIThreadAsync(async () =>
            {
                if (isUpdatingMimeBlocked || CurrentMailDraftItem?.UniqueId != uniqueId || ComposingAccount?.Id != accountId) return;

                SaveAddressInfo(ToItems, CurrentMimeMessage.To);
                SaveAddressInfo(CCItems, CurrentMimeMessage.Cc);
                SaveAddressInfo(BCCItems, CurrentMimeMessage.Bcc);
                SaveImportance();
                SaveSubject();
                SaveFromAddress();
                SaveReadReceiptRequest();
                SaveReplyToAddress();
                await SaveAttachmentsAsync();
                await SaveBodyAsync();
                UpdateMailCopyProperties();

                var mail = CurrentMailDraftItem.MailCopy;
                metadata = new MailCopy
                {
                    UniqueId = uniqueId, FileId = mail.FileId, Id = mail.Id, FolderId = mail.FolderId,
                    MessageId = mail.MessageId, Subject = mail.Subject, PreviewText = mail.PreviewText,
                    FromAddress = mail.FromAddress, FromName = mail.FromName, HasAttachments = mail.HasAttachments,
                    Importance = mail.Importance, IsDraft = true
                };
                using var stream = new MemoryStream();
                CurrentMimeMessage.WriteTo(stream);
                bytes = stream.ToArray();
            }).ConfigureAwait(false);
            if (metadata == null) return false;

            var snapshot = new DraftUpdateSnapshot(accountId, uniqueId, bytes);
            return await _draftSaveService.SaveAsync(snapshot, metadata).ConfigureAwait(false);
        }
        finally { entry.SaveLock.Release(); }
    }

    private void UpdateMailCopyProperties()
    {
        CurrentMailDraftItem.Subject = CurrentMimeMessage.Subject;
        CurrentMailDraftItem.PreviewText = CurrentMimeMessage.TextBody;
        CurrentMailDraftItem.FromAddress = SelectedAlias.AliasAddress;

        // The local copy takes its sender from the MIME that was just written, so the list and
        // the message can never disagree about who the draft is from.
        CurrentMailDraftItem.FromName = CurrentMimeMessage.From.Mailboxes.FirstOrDefault()?.Name
            ?? SelectedAlias.AliasSenderName
            ?? ComposingAccount?.SenderName;
        CurrentMailDraftItem.HasAttachments = CurrentMimeMessage.Attachments.Any();
    }

    private async Task SaveAttachmentsAsync()
    {
        bodyBuilder.Attachments.Clear();

        foreach (var path in IncludedAttachments)
        {
            if (path.Content == null) continue;

            await bodyBuilder.Attachments.AddAsync(path.FileName, new MemoryStream(path.Content));
        }
    }

    private void SaveImportance()
    {
        CurrentMimeMessage.Importance = IsImportanceSelected ? SelectedMessageImportance : MessageImportance.Normal;
    }

    private void SaveSubject()
    {
        if (Subject != null)
        {
            CurrentMimeMessage.Subject = Subject;
        }
    }

    private async Task SaveBodyAsync()
    {
        if (GetHTMLBodyFunction != null)
        {
            bodyBuilder.SetHtmlBody(await GetHTMLBodyFunction());
        }

        CurrentMimeMessage.Body = bodyBuilder.ToMessageBody();
    }

    [RelayCommand(CanExecute = nameof(canSendMail))]
    private async Task DiscardAsync()
        => await DiscardDraftAsync();

    public Task SaveDraftAsync()
        => UpdateMimeChangesAsync();

    public async Task DiscardDraftAsync(bool requireConfirmation = true)
    {
        if (ComposingAccount == null)
        {
            _dialogService.InfoBarMessage(Translator.Info_MessageCorruptedTitle, Translator.Info_MessageCorruptedMessage, InfoBarMessageType.Error);
            return;
        }

        var confirmation = !requireConfirmation || await _dialogService.ShowConfirmationDialogAsync(Translator.DialogMessage_DiscardDraftConfirmationMessage,
                                                                                                    Translator.DialogMessage_DiscardDraftConfirmationTitle,
                                                                                                    Translator.Buttons_Yes);

        if (!confirmation)
        {
            return;
        }

        isUpdatingMimeBlocked = true;
        var currentDraft = await _draftUpdates.StopAsync(ComposingAccount.Id, CurrentMailDraftItem.UniqueId).ConfigureAwait(false);
        if (currentDraft != null)
            await ExecuteUIThread(() => DraftUpdateIdentity.From(currentDraft).Apply(CurrentMailDraftItem.MailCopy));

        try
        {
            // Don't send delete request for local drafts. Just delete the record and mime locally.
            if (CurrentMailDraftItem.MailCopy.IsLocalDraft)
            {
                var mappedDraft = await _mailService
                    .DiscardLocalDraftAsync(ComposingAccount.Id, CurrentMailDraftItem.UniqueId)
                    .ConfigureAwait(false);

                if (mappedDraft != null)
                {
                    var deletePackage = new MailOperationPreperationRequest(
                        MailOperation.HardDelete,
                        mappedDraft,
                        ignoreHardDeleteProtection: true);
                    await _worker.ExecuteAsync(deletePackage).ConfigureAwait(false);
                }
            }
            else
            {
                var deletePackage = new MailOperationPreperationRequest(MailOperation.HardDelete, CurrentMailDraftItem.MailCopy, ignoreHardDeleteProtection: true);
                await _worker.ExecuteAsync(deletePackage).ConfigureAwait(false);
            }
        }
        catch
        {
            isUpdatingMimeBlocked = false;
            throw;
        }
    }

    public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        ReleaseSigningCertificates();
        base.OnNavigatedFrom(mode, parameters);
    }

    //public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    //{
    //    base.OnNavigatedFrom(mode, parameters);

    //    /// Do not put any code here.
    //    /// Make sure to use Page's OnNavigatedTo instead.
    //}

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
        => await InitializeNavigationAsync(mode, parameters);

    public async Task InitializeNavigationAsync(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (parameters != null && parameters is MailItemViewModel mailItem)
        {
            CurrentMailDraftItem = mailItem;

            await InitializeCurrentDraftAsync().ConfigureAwait(false);
        }
    }

    public async Task RefreshDraftAsync(MailItemViewModel draftMailItemViewModel)
    {
        if (draftMailItemViewModel == null || !draftMailItemViewModel.IsDraft) return;

        // Save current draft before switching.
        await UpdateMimeChangesAsync().ConfigureAwait(false);

        // Reset state for the new draft.
        await ExecuteUIThread(() =>
        {
            isUpdatingMimeBlocked = false;
            ComposingAccount = null;
            IncludedAttachments.Clear();
            CurrentMailDraftItem = draftMailItemViewModel;
        }).ConfigureAwait(false);

        await InitializeCurrentDraftAsync().ConfigureAwait(false);
    }

    private async Task InitializeCurrentDraftAsync()
    {
        await ObserveDraftMappingAsync().ConfigureAwait(false);
        // These initialization paths are independent. Run service/file work together,
        // then let each path commit only its small UI-bound portion through the dispatcher.
        await Task.WhenAll(
            UpdatePendingOperationStateAsync(),
            LoadEmailTemplatesAsync(),
            TryPrepareComposeAsync(true))
            .ConfigureAwait(false);
    }

    private async Task ObserveDraftMappingAsync()
    {
        Task initialRefresh = Task.CompletedTask;
        var generation = Volatile.Read(ref _draftMappingGeneration);
        await ExecuteUIThread(() =>
        {
            if (generation != Volatile.Read(ref _draftMappingGeneration)) return;

            StopObservingDraftMapping();
            var draft = CurrentMailDraftItem;
            if (draft?.MailCopy?.AssignedAccount == null || !draft.IsDraft) return;

            var mapping = _draftRegistry.Get(draft.MailCopy.AssignedAccount.Id, draft.UniqueId).Mapping;
            mapping.Initialize(draft.MailCopy);
            _observedDraftMapping = mapping;
            _draftMappingSubscription = mapping.Observe(state =>
                initialRefresh = RefreshDraftMappingAsync(draft, state));
        }).ConfigureAwait(false);
        await initialRefresh.ConfigureAwait(false);
    }

    private async Task RefreshDraftMappingAsync(MailItemViewModel draft, DraftMappingLifecycle mapping)
    {
        try
        {
            await ExecuteUIThread(() => ApplyDraftMapping(draft, mapping)).ConfigureAwait(false);

            await UpdatePendingOperationStateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not refresh composer draft mapping.");
        }
    }

    private void ApplyDraftMapping(MailItemViewModel draft, DraftMappingLifecycle mapping)
    {
        if (mapping == null || draft?.IsDraft != true || !ReferenceEquals(_observedDraftMapping, mapping) ||
            !ReferenceEquals(CurrentMailDraftItem, draft)) return;

        if (!mapping.ApplyTo(draft.MailCopy)) return;

        draft.UpdateFrom(draft.MailCopy, MailCopyChangeFlags.Id | MailCopyChangeFlags.DraftId |
            MailCopyChangeFlags.ThreadId | MailCopyChangeFlags.DraftSyncState);
        IsDraftSyncFailed = draft.IsDraftSyncFailed;
        OnPropertyChanged(nameof(DraftSyncErrorMessage));
        NotifyComposeActionStateChanged();
    }

    private void StopObservingDraftMapping()
    {
        Interlocked.Increment(ref _draftMappingGeneration);
        _observedDraftMapping = null;
        _draftMappingSubscription?.Dispose();
        _draftMappingSubscription = null;
    }

    private async Task LoadEmailTemplatesAsync()
    {
        var templates = await _emailTemplateService.GetEmailTemplatesAsync().ConfigureAwait(false);

        await ExecuteUIThread(() =>
        {
            AvailableEmailTemplates.Clear();

            foreach (var template in templates)
            {
                AvailableEmailTemplates.Add(template);
            }
        });
    }

    public async void Receive(SynchronizationActionsCompleted message)
    {
        if (!ShouldTrackDraftSynchronizationState(message.AccountId))
            return;

        await UpdatePendingOperationStateAsync().ConfigureAwait(false);
    }

    public async void Receive(AccountSynchronizerStateChanged message)
    {
        if (message.NewState != AccountSynchronizerState.Idle || !ShouldTrackDraftSynchronizationState(message.AccountId))
            return;

        await UpdatePendingOperationStateAsync().ConfigureAwait(false);
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        Messenger.Register<SynchronizationActionsCompleted>(this);
        Messenger.Register<AccountSynchronizerStateChanged>(this);
    }

    protected override void UnregisterRecipients()
    {
        StopObservingDraftMapping();
        base.UnregisterRecipients();

        Messenger.Unregister<SynchronizationActionsCompleted>(this);
        Messenger.Unregister<AccountSynchronizerStateChanged>(this);
    }

    private async Task<bool> InitializeComposerAccountAsync()
    {
        if (CurrentMailDraftItem == null) return false;

        if (ComposingAccount != null) return true;

        var composingAccount = await _accountService.GetAccountAsync(CurrentMailDraftItem.MailCopy.AssignedAccount.Id).ConfigureAwait(false);
        if (composingAccount == null) return false;

        var aliases = await _accountService.GetAccountAliasesAsync(composingAccount.Id).ConfigureAwait(false);

        if (aliases == null || !aliases.Any()) return false;

        // The alias comes from the message itself where the draft names one, matched on the
        // normalized address so stored casing or stray spaces still find it.
        MailAccountAlias selectedAlias = null;

        if (!string.IsNullOrWhiteSpace(CurrentMailDraftItem.FromAddress))
        {
            var draftAddress = CurrentMailDraftItem.FromAddress.Trim();
            selectedAlias = aliases.Find(a =>
                string.Equals(a.AliasAddress?.Trim(), draftAddress, StringComparison.OrdinalIgnoreCase));
        }

        // The fallback is taken from this same list rather than re-read from the service, so the
        // selection is always one of the objects the picker is bound to.
        selectedAlias ??= aliases.Find(a => a.IsPrimary) ?? aliases[0];

        await ExecuteUIThread(() =>
        {
            ComposingAccount = composingAccount;
            AvailableAliases = aliases;
            SelectedAlias = selectedAlias;
        });

        return true;
    }

    private async Task UpdatePendingOperationStateAsync()
    {
        MailItemViewModel draft = null;
        Guid accountId = Guid.Empty;
        await ExecuteUIThread(() =>
        {
            draft = CurrentMailDraftItem;
            accountId = draft?.MailCopy?.AssignedAccount?.Id ?? Guid.Empty;
        }).ConfigureAwait(false);

        if (draft?.MailCopy == null || !draft.MailCopy.IsDraft)
        {
            await ExecuteUIThread(() =>
            {
                if (!ReferenceEquals(CurrentMailDraftItem, draft)) return;

                IsDraftBusy = false;
                NotifyComposeActionStateChanged();
            });
            return;
        }

        IWinoSynchronizerBase synchronizer = null;
        if (accountId != Guid.Empty)
            synchronizer = await _synchronizationManager.GetSynchronizerAsync(accountId).ConfigureAwait(false);

        await ExecuteUIThread(() =>
        {
            if (!ReferenceEquals(CurrentMailDraftItem, draft)) return;

            var hasPendingOperation = synchronizer?.HasPendingOperation(draft.UniqueId) ?? false;
            var keepBusyForInitialGracePeriod = !hasPendingOperation && draft.IsLocalDraft &&
                IsWithinLocalDraftRetryGracePeriod(draft.MailCopy);
            IsDraftBusy = hasPendingOperation || keepBusyForInitialGracePeriod;
            NotifyComposeActionStateChanged();
        });
    }

    private async Task TryPrepareComposeAsync(bool downloadIfNeeded)
    {
        if (CurrentMailDraftItem == null) return;

        bool isComposerInitialized = await InitializeComposerAccountAsync().ConfigureAwait(false);

        if (!isComposerInitialized) return;

    retry:

        // Replying existing message.
        MimeMessageInformation mimeMessageInformation = null;

        try
        {
            mimeMessageInformation = await _mimeFileService.GetMimeMessageInformationAsync(CurrentMailDraftItem.MailCopy.FileId, ComposingAccount.Id).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            if (downloadIfNeeded)
            {
                downloadIfNeeded = false;

                // Download missing MIME message using SynchronizationManager
                await SynchronizationManager.Instance.DownloadMimeMessageAsync(
                    CurrentMailDraftItem.MailCopy,
                    CurrentMailDraftItem.MailCopy.AssignedAccount.Id).ConfigureAwait(false);

                goto retry;
            }
            else
                _dialogService.InfoBarMessage(Translator.Info_ComposerMissingMIMETitle, Translator.Info_ComposerMissingMIMEMessage, InfoBarMessageType.Error);

            return;
        }
        catch (IOException)
        {
            _dialogService.InfoBarMessage(Translator.Busy, Translator.Exception_MailProcessing, InfoBarMessageType.Warning);
        }
        catch (ComposerMimeNotFoundException)
        {
            _dialogService.InfoBarMessage(Translator.Info_ComposerMissingMIMETitle, Translator.Info_ComposerMissingMIMEMessage, InfoBarMessageType.Error);
        }

        if (mimeMessageInformation == null)
            return;

        var replyingMime = mimeMessageInformation.MimeMessage;
        var mimeFilePath = mimeMessageInformation.Path;

        var renderModel = _mimeFileService.GetMailRenderModel(replyingMime, mimeFilePath);
        var toItems = await ResolveAddressInfoAsync(replyingMime.To).ConfigureAwait(false);
        var ccItems = await ResolveAddressInfoAsync(replyingMime.Cc).ConfigureAwait(false);
        var bccItems = await ResolveAddressInfoAsync(replyingMime.Bcc).ConfigureAwait(false);

        await ExecuteUIThread(() =>
        {
            // Extract information

            CurrentMimeMessage = replyingMime;

            ToItems.Clear();
            CCItems.Clear();
            BCCItems.Clear();

            foreach (var contact in toItems)
                ToItems.Add(contact);
            foreach (var contact in ccItems)
                CCItems.Add(contact);
            foreach (var contact in bccItems)
                BCCItems.Add(contact);

            LoadAttachments();
            ApplyPendingSharedAttachments();

            if (replyingMime.Cc.Any() || replyingMime.Bcc.Any())
                IsCCBCCVisible = true;

            Subject = replyingMime.Subject;
            IsReadReceiptRequested = replyingMime.HasReadReceiptRequest();

            Messenger.Send(new CreateNewComposeMailRequested(renderModel));
        }).ConfigureAwait(false);

        if (RenderHtmlBodyAsyncFunc != null)
        {
            await ExecuteUIThreadAsync(() => RenderHtmlBodyAsyncFunc(renderModel.RenderHtml))
                .ConfigureAwait(false);
        }
    }

    private void LoadAttachments()
    {
        if (CurrentMimeMessage == null) return;

        IncludedAttachments.Clear();

        foreach (var attachment in CurrentMimeMessage.Attachments)
        {
            if (attachment.IsAttachment && attachment is MimePart attachmentPart)
            {
                var attachmentViewModel = new MailAttachmentViewModel(attachmentPart);
                IncludedAttachments.Add(attachmentViewModel);
                BeginAttachmentInspection(attachmentViewModel, AttachmentFileOrigin.Local);
            }
        }
    }

    private void ApplyPendingSharedAttachments()
    {
        var draftUniqueId = CurrentMailDraftItem?.MailCopy?.UniqueId ?? Guid.Empty;

        if (draftUniqueId == Guid.Empty)
            return;

        var shareRequest = _activationStateService.ConsumePendingComposeShareRequest(draftUniqueId);

        if (shareRequest?.Files == null || shareRequest.Files.Count == 0)
            return;

        foreach (var sharedFile in shareRequest.Files)
        {
            AddAttachment(sharedFile);
        }
    }

    private void BeginAttachmentInspection(MailAttachmentViewModel attachmentViewModel, AttachmentFileOrigin origin)
    {
        if (_attachmentFileService == null)
            return;

        var inspection = _attachmentFileService.InspectAsync(attachmentViewModel.CreateFileSource(origin)).AsTask();
        attachmentViewModel.BeginInspection(inspection);
        _ = ApplyAttachmentInspectionAsync(attachmentViewModel, inspection);
    }

    private async Task ApplyAttachmentInspectionAsync(
        MailAttachmentViewModel attachmentViewModel,
        Task<ContentTypeDetectionResult> inspection)
    {
        try
        {
            var detection = await inspection.ConfigureAwait(false);
            await ExecuteUIThread(() => attachmentViewModel.ContentTypeDetection = detection).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<List<AccountContact>> ResolveAddressInfoAsync(InternetAddressList list)
    {
        var contacts = new List<AccountContact>();

        foreach (var item in list)
        {
            if (item is MailboxAddress mailboxAddress)
            {
                var foundContact = await ContactService.GetContactByAddressAsync(ComposingAccount?.Id, mailboxAddress.Address).ConfigureAwait(false);

                // Keep the address the mail used; the contact only supplies the name and picture.
                contacts.Add(foundContact is null
                    ? new AccountContact() { Name = mailboxAddress.Name, Address = mailboxAddress.Address }
                    : RecipientSuggestion.ForTypedAddress(mailboxAddress.Address, foundContact));
            }
            else if (item is GroupAddress groupAddress)
            {
                contacts.AddRange(await ResolveAddressInfoAsync(groupAddress.Members).ConfigureAwait(false));
            }
        }

        return contacts;
    }

    private void SaveFromAddress()
    {
        if (SelectedAlias == null) return;

        CurrentMimeMessage.From.Clear();

        // Try to get the sender name from the alias. If not, fallback to account sender name.
        var senderName = SelectedAlias.AliasSenderName ?? ComposingAccount.SenderName;

        CurrentMimeMessage.From.Add(new MailboxAddress(senderName, SelectedAlias.AliasAddress));
    }

    private void SaveReplyToAddress()
    {
        if (SelectedAlias == null || CurrentMimeMessage == null) return;

        CurrentMimeMessage.ReplyTo.Clear();

        if (!string.IsNullOrEmpty(SelectedAlias.ReplyToAddress))
        {
            CurrentMimeMessage.ReplyTo.Add(new MailboxAddress(SelectedAlias.ReplyToAddress, SelectedAlias.ReplyToAddress));
        }
    }

    private void SaveReadReceiptRequest()
    {
        if (CurrentMimeMessage == null)
            return;

        var receiptAddress = SelectedAlias?.AliasAddress ?? ComposingAccount?.Address ?? string.Empty;
        CurrentMimeMessage.SetReadReceiptRequest(receiptAddress, IsReadReceiptRequested);
    }

    private void SaveAddressInfo(IEnumerable<AccountContact> addresses, InternetAddressList list)
    {
        list.Clear();

        foreach (var item in addresses)
            list.Add(new MailboxAddress(item.Name, item.Address));
    }

    public async Task<AccountContact> GetAddressInformationAsync(string tokenText, ObservableCollection<AccountContact> collection)
    {
        var address = tokenText?.Trim();
        if (string.IsNullOrEmpty(address) || ContainsAddress(collection, address))
            return null;

        // A known contact supplies the name, but the typed address is kept: it may be a
        // secondary address of that contact.
        var knownContact = await ContactService.GetContactByAddressAsync(ComposingAccount?.Id, address).ConfigureAwait(false);
        return RecipientSuggestion.ForTypedAddress(address, knownContact);
    }

    public static bool ContainsAddress(IEnumerable<AccountContact> collection, string address, AccountContact except = null)
        => collection?.Any(item => !ReferenceEquals(item, except) &&
                                   string.Equals(item.Address?.Trim(), address?.Trim(), StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// Adds a recipient unless the address is already in the field. Returns false for a duplicate.
    /// </summary>
    public bool TryAddRecipient(ObservableCollection<AccountContact> collection, AccountContact recipient)
    {
        if (collection is null || recipient is null || string.IsNullOrWhiteSpace(recipient.Address) || ContainsAddress(collection, recipient.Address))
            return false;

        collection.Add(recipient);
        return true;
    }

    /// <summary>
    /// Stops suggesting a remembered correspondent for the composing account.
    /// </summary>
    public async Task SuppressSuggestionAsync(RecipientSuggestion suggestion)
    {
        if (suggestion is not { CanSuppress: true } || string.IsNullOrWhiteSpace(suggestion.Address))
            return;

        var accountId = ComposingAccount?.Id ?? suggestion.MailAccountId;
        if (accountId == Guid.Empty)
            return;

        await _recipientHistoryService.SuppressAsync(accountId, suggestion.Address).ConfigureAwait(false);
    }

    public void NotifyAddressExists()
    {
        _dialogService.InfoBarMessage(Translator.Info_ContactExistsTitle, Translator.Info_ContactExistsMessage, InfoBarMessageType.Warning);
    }

    public void NotifyInvalidEmail(string address)
    {
        _dialogService.InfoBarMessage(Translator.Info_InvalidAddressTitle, string.Format(Translator.Info_InvalidAddressMessage, address), InfoBarMessageType.Warning);
    }

    protected override async void OnMailUpdated(MailCopy updatedMail, EntityUpdateSource source, MailCopyChangeFlags changedProperties)
    {
        base.OnMailUpdated(updatedMail, source, changedProperties);

        await ExecuteUIThread(() =>
        {
            var draft = CurrentMailDraftItem;
            if (draft?.UniqueId != updatedMail.UniqueId ||
                draft.MailCopy.AssignedAccount?.Id != updatedMail.AssignedAccount?.Id) return;

            const MailCopyChangeFlags mappingFlags = MailCopyChangeFlags.Id | MailCopyChangeFlags.DraftId |
                MailCopyChangeFlags.ThreadId | MailCopyChangeFlags.DraftSyncState;
            if (changedProperties != MailCopyChangeFlags.None && (changedProperties & ~mappingFlags) == 0)
            {
                // Mapping notifications carry a full row, but must not replace unsaved content.
                if ((changedProperties & MailCopyChangeFlags.DraftSyncState) != 0)
                {
                    draft.MailCopy.DraftSyncState = updatedMail.DraftSyncState;
                    draft.MailCopy.DraftSyncAttemptCount = updatedMail.DraftSyncAttemptCount;
                    draft.MailCopy.LastDraftSyncAttemptUtc = updatedMail.LastDraftSyncAttemptUtc;
                    draft.MailCopy.LastDraftSyncError = updatedMail.LastDraftSyncError;
                    draft.UpdateFrom(draft.MailCopy, MailCopyChangeFlags.DraftSyncState);
                }
            }
            else
            {
                draft.UpdateFrom(updatedMail, changedProperties);
            }

            // A queued mail notification may carry an older identity than the committed mapping.
            ApplyDraftMapping(draft, _observedDraftMapping);
            IsDraftSyncFailed = draft.IsDraftSyncFailed;
            OnPropertyChanged(nameof(DraftSyncErrorMessage));
        }).ConfigureAwait(false);

        await UpdatePendingOperationStateAsync().ConfigureAwait(false);
    }

    partial void OnCurrentMailDraftItemChanged(MailItemViewModel value)
    {
        StopObservingDraftMapping();
        IsDraftSyncFailed = value?.MailCopy?.IsDraftSyncFailed == true;
        OnPropertyChanged(nameof(DraftSyncErrorMessage));

        // A rewrite belongs to the draft it was made from.
        RewriteSession.Reset();
    }

    partial void OnComposingAccountChanged(MailAccount value) => _ = RefreshRewriteAvailabilityAsync();

    /// <summary>
    /// Re-checks rewrite eligibility for the composing account. Called when the account changes and
    /// when Wino Intelligence access changes.
    /// </summary>
    public async Task RefreshRewriteAvailabilityAsync()
    {
        try
        {
            await ExecuteUIThreadAsync(() => RewriteSession.RefreshAvailabilityAsync());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not refresh composer rewrite availability.");
        }
    }

    protected override async void OnDraftFailed(MailCopy draftMail, MailAccount account)
    {
        base.OnDraftFailed(draftMail, account);

        if (CurrentMailDraftItem?.MailCopy?.UniqueId != draftMail.UniqueId)
            return;

        await ExecuteUIThread(() =>
        {
            CurrentMailDraftItem.UpdateFrom(draftMail, MailCopyChangeFlags.DraftSyncState);
            IsDraftSyncFailed = true;
            IsDraftBusy = false;
            OnPropertyChanged(nameof(DraftSyncErrorMessage));
            NotifyComposeActionStateChanged();
        });
    }

    protected override async void OnMailRemoved(MailCopy removedMail, EntityUpdateSource source)
    {
        base.OnMailRemoved(removedMail, source);

        if (CurrentMailDraftItem?.MailCopy == null)
            return;

        if (CurrentMailDraftItem.MailCopy.UniqueId != removedMail.UniqueId)
            return;

        await ExecuteUIThread(() => CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    private void NotifyComposeActionStateChanged()
    {
        OnPropertyChanged(nameof(IsLocalDraft));
        OnPropertyChanged(nameof(ShouldShowSendToServerButton));
        OnPropertyChanged(nameof(ShouldShowSendButton));

        DiscardCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        SendToServerCommand.NotifyCanExecuteChanged();
    }

    private bool ShouldTrackDraftSynchronizationState(Guid accountId)
    {
        if (accountId == Guid.Empty)
            return false;

        var currentDraftAccountId = CurrentMailDraftItem?.MailCopy?.AssignedAccount?.Id
                                    ?? ComposingAccount?.Id
                                    ?? Guid.Empty;

        return currentDraftAccountId != Guid.Empty && currentDraftAccountId == accountId;
    }

    private bool IsWithinLocalDraftRetryGracePeriod(MailCopy localDraft)
    {
        if (localDraft?.DraftSyncState == DraftSyncState.SyncFailed)
            return false;

        if (localDraft == null || localDraft.CreationDate == default)
            return false;

        var elapsed = DateTime.UtcNow - localDraft.CreationDate;

        // Clock skew safety.
        if (elapsed < TimeSpan.Zero)
            return true;

        return elapsed < LocalDraftRetryGracePeriod;
    }
}
