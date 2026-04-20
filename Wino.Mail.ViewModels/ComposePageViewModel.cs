using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MimeKit;
using MimeKit.Cryptography;
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
using Wino.Core.Extensions;
using Wino.Core.Services;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

public partial class ComposePageViewModel : MailBaseViewModel,
    IRecipient<SynchronizationActionsAdded>,
    IRecipient<SynchronizationActionsCompleted>,
    IRecipient<AccountSynchronizerStateChanged>
{
    public event EventHandler CloseRequested;

    private static readonly TimeSpan LocalDraftRetryGracePeriod = TimeSpan.FromSeconds(15);

    public Func<Task<string>> GetHTMLBodyFunction;
    public Func<string, Task> RenderHtmlBodyAsyncFunc { get; set; }

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
    private bool canSendLocalDraftToServer => ComposingAccount != null && IsLocalDraft && CurrentMimeMessage != null && !IsDraftBusy && !IsRetryingSendToServer;

    [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendToServerCommand))]
    [ObservableProperty]
    private MimeMessage currentMimeMessage = null;

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
    public partial bool IsSmimeSignatureEnabled { get; set; }
    [ObservableProperty]
    public partial bool IsSmimeEncryptionEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsReadReceiptRequested { get; set; }

    [ObservableProperty]
    public partial X509Certificate2 SelectedSigningCertificate { get; set; }

    public ObservableCollection<X509Certificate2> AvailableCertificates = [];

    public bool AreCertificatesAvailable => AvailableCertificates.Count > 0;

    public ObservableCollection<EmailTemplate> AvailableEmailTemplates { get; } = [];
    public ObservableCollection<MailAttachmentViewModel> IncludedAttachments { get; set; } = [];
    public ObservableCollection<MailAccount> Accounts { get; set; } = [];
    public ObservableCollection<AccountContact> ToItems { get; set; } = [];
    public ObservableCollection<AccountContact> CCItems { get; set; } = [];
    public ObservableCollection<AccountContact> BCCItems { get; set; } = [];
    public bool ShouldShowSendToServerButton => IsLocalDraft && !IsDraftBusy;
    public bool ShouldShowSendButton => !IsLocalDraft;

    #endregion

    public INativeAppService NativeAppService { get; }

    private readonly IMailDialogService _dialogService;
    private readonly IMailService _mailService;
    private readonly IMimeFileService _mimeFileService;
    private readonly IFileService _fileService;
    private readonly IFolderService _folderService;
    private readonly IAccountService _accountService;
    private readonly IEmailTemplateService _emailTemplateService;
    private readonly IWinoRequestDelegator _worker;
    public readonly IFontService FontService;
    public readonly IPreferencesService PreferencesService;
    public readonly IContactService ContactService;
    public readonly ISmimeCertificateService _smimeCertificateService;
    private readonly IShareActivationService _shareActivationService;

    public ComposePageViewModel(IMailDialogService dialogService,
                                IMailService mailService,
                                IMimeFileService mimeFileService,
                                IFileService fileService,
                                INativeAppService nativeAppService,
                                IFolderService folderService,
                                IAccountService accountService,
                                IEmailTemplateService emailTemplateService,
                                IWinoRequestDelegator worker,
                                IContactService contactService,
                                IFontService fontService,
                                IPreferencesService preferencesService,
                                ISmimeCertificateService smimeCertificateService,
                                IShareActivationService shareActivationService)
    {
        NativeAppService = nativeAppService;
        ContactService = contactService;
        FontService = fontService;
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
        _shareActivationService = shareActivationService;

        foreach (var cert in _smimeCertificateService.GetCertificates(emailAddress: SelectedAlias?.AliasAddress))
        {
            if (cert != null)
            {
                AvailableCertificates.Add(cert);
            }
        }
    }

    partial void OnSelectedAliasChanged(MailAccountAlias value)
    {
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
    }

    partial void OnSelectedSigningCertificateChanged(X509Certificate2 value)
    {
        IsSmimeSignatureEnabled = value != null;
    }

    [RelayCommand]
    private async Task OpenAttachmentAsync(MailAttachmentViewModel attachmentViewModel)
    {
        if (string.IsNullOrEmpty(attachmentViewModel.FilePath)) return;

        try
        {
            await NativeAppService.LaunchFileAsync(attachmentViewModel.FilePath);
        }
        catch
        {
            _dialogService.InfoBarMessage(Translator.Info_FailedToOpenFileTitle, Translator.Info_FailedToOpenFileMessage, InfoBarMessageType.Error);
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
            await _fileService.CopyFileAsync(attachmentViewModel.FilePath, pickedFilePath);
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
            var attachmentViewModel = new MailAttachmentViewModel(file);
            IncludedAttachments.Add(attachmentViewModel);
        }
    }

    [RelayCommand]
    private void RemoveAttachment(MailAttachmentViewModel attachmentViewModel)
        => IncludedAttachments.Remove(attachmentViewModel);

    [RelayCommand(CanExecute = nameof(canSendMail))]
    private async Task SendAsync()
    {
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
        await UpdateMimeChangesAsync().ConfigureAwait(false);

        isUpdatingMimeBlocked = true;

        var assignedAccount = CurrentMailDraftItem.MailCopy.AssignedAccount;
        var sentFolder = await _folderService.GetSpecialFolderByAccountIdAsync(assignedAccount.Id, SpecialFolderType.Sent);


        // Load alias certs
        var certs = _smimeCertificateService.GetCertificates(emailAddress: SelectedAlias.AliasAddress);

        if (IsSmimeSignatureEnabled)
        {
            var signingCertificate = !string.IsNullOrEmpty(SelectedAlias.SelectedSigningCertificateThumbprint)
                ? certs.FirstOrDefault(c => c?.Thumbprint == SelectedAlias.SelectedSigningCertificateThumbprint)
                : null;

            var signer = new CmsSigner(signingCertificate) { DigestAlgorithm = DigestAlgorithm.Sha1 };

            if (IsSmimeEncryptionEnabled)
            {
                var recipients = new CmsRecipientCollection();
                var cmsRecipients = CurrentMimeMessage.To.Mailboxes
                    .Select(mailbox => new CmsRecipient(
                        _smimeCertificateService.GetCertificates(emailAddress: mailbox.Address).FirstOrDefault() ?? _smimeCertificateService.GetCertificates(StoreName.AddressBook, emailAddress: mailbox.Address).FirstOrDefault()
                    ));
                foreach (var recipient in cmsRecipients)
                {
                    recipients.Add(recipient);
                }

                CurrentMimeMessage.Body = ApplicationPkcs7Mime.SignAndEncrypt(signer, recipients, CurrentMimeMessage.Body);
            }
            else
            {
                // CurrentMimeMessage.Body = MultipartSigned.Create(signer, CurrentMimeMessage.Body);
                CurrentMimeMessage.Body = ApplicationPkcs7Mime.Sign(signer, CurrentMimeMessage.Body);
            }
        }
        else if (IsSmimeEncryptionEnabled)
        {
            // var encryptionCertificate = !string.IsNullOrEmpty(SelectedAlias.SelectedEncryptionCertificateThumbprint)
            //     ? certs.FirstOrDefault(c => c?.Thumbprint == SelectedAlias.SelectedEncryptionCertificateThumbprint)
            //     : null;
            // Encrypt the message if encryption certificate is selected.
            CurrentMimeMessage.Body = ApplicationPkcs7Mime.Encrypt(CurrentMimeMessage.To.Mailboxes, CurrentMimeMessage.Body);
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

            var localDraftCopy = CurrentMailDraftItem.MailCopy;
            var (retryReason, referenceMailCopy) = await ResolveRetryDraftContextAsync().ConfigureAwait(false);
            var draftPreparationRequest = new DraftPreparationRequest(
                localDraftCopy.AssignedAccount ?? ComposingAccount,
                localDraftCopy,
                CurrentMimeMessage.GetBase64MimeMessage(),
                retryReason,
                referenceMailCopy);

            await _worker.ExecuteAsync(draftPreparationRequest).ConfigureAwait(false);
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

    public async Task UpdateMimeChangesAsync()
    {
        if (isUpdatingMimeBlocked || CurrentMimeMessage == null || ComposingAccount == null || CurrentMailDraftItem == null) return;

        // Save recipients.

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
        await UpdateMailCopyAsync();

        // Save mime file.
        await _mimeFileService.SaveMimeMessageAsync(CurrentMailDraftItem.MailCopy.FileId, CurrentMimeMessage, ComposingAccount.Id).ConfigureAwait(false);
    }

    private async Task UpdateMailCopyAsync()
    {
        CurrentMailDraftItem.Subject = CurrentMimeMessage.Subject;
        CurrentMailDraftItem.PreviewText = CurrentMimeMessage.TextBody;
        CurrentMailDraftItem.FromAddress = SelectedAlias.AliasAddress;
        CurrentMailDraftItem.HasAttachments = CurrentMimeMessage.Attachments.Any();

        // Update database.
        await _mailService.UpdateMailAsync(CurrentMailDraftItem.MailCopy);
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

        try
        {
            // Don't send delete request for local drafts. Just delete the record and mime locally.
            if (CurrentMailDraftItem.MailCopy.IsLocalDraft)
            {
                await _mailService.DeleteMailAsync(ComposingAccount.Id, CurrentMailDraftItem.Id);
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

    //public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    //{
    //    base.OnNavigatedFrom(mode, parameters);

    //    /// Do not put any code here.
    //    /// Make sure to use Page's OnNavigatedTo instead.
    //}

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (parameters != null && parameters is MailItemViewModel mailItem)
        {
            CurrentMailDraftItem = mailItem;

            await UpdatePendingOperationStateAsync();
            await LoadEmailTemplatesAsync();
            await TryPrepareComposeAsync(true);
        }
    }

    public async Task RefreshDraftAsync(MailItemViewModel draftMailItemViewModel)
    {
        if (draftMailItemViewModel == null || !draftMailItemViewModel.IsDraft) return;

        // Save current draft before switching.
        await UpdateMimeChangesAsync();

        // Reset state for the new draft.
        isUpdatingMimeBlocked = false;
        ComposingAccount = null;
        IncludedAttachments.Clear();

        // Set the new draft item and prepare it.
        CurrentMailDraftItem = draftMailItemViewModel;
        await UpdatePendingOperationStateAsync();
        await LoadEmailTemplatesAsync();
        await TryPrepareComposeAsync(true);
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

    public async void Receive(SynchronizationActionsAdded message)
    {
        if (!ShouldTrackDraftSynchronizationState(message.AccountId))
            return;

        await UpdatePendingOperationStateAsync().ConfigureAwait(false);
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

        Messenger.Register<SynchronizationActionsAdded>(this);
        Messenger.Register<SynchronizationActionsCompleted>(this);
        Messenger.Register<AccountSynchronizerStateChanged>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<SynchronizationActionsAdded>(this);
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

        // MailAccountAlias primaryAlias = aliases.Find(a => a.IsPrimary) ?? aliases.First();

        // Auto-select the correct alias from the message itself.
        // If can't, fallback to primary alias.

        MailAccountAlias primaryAlias = null;

        if (!string.IsNullOrEmpty(CurrentMailDraftItem.FromAddress))
        {
            primaryAlias = aliases.Find(a => a.AliasAddress == CurrentMailDraftItem.FromAddress);
        }

        primaryAlias ??= await _accountService.GetPrimaryAccountAliasAsync(composingAccount.Id).ConfigureAwait(false);

        await ExecuteUIThread(() =>
        {
            ComposingAccount = composingAccount;
            AvailableAliases = aliases;
            SelectedAlias = primaryAlias;
        });

        return true;
    }

    private async Task UpdatePendingOperationStateAsync()
    {
        var hasPendingOperation = false;
        var keepBusyForInitialGracePeriod = false;

        if (CurrentMailDraftItem?.MailCopy == null || !CurrentMailDraftItem.MailCopy.IsDraft)
        {
            await ExecuteUIThread(() =>
            {
                IsDraftBusy = false;
                NotifyComposeActionStateChanged();
            });
            return;
        }

        var accountId = CurrentMailDraftItem.MailCopy.AssignedAccount?.Id ?? Guid.Empty;

        if (accountId != Guid.Empty)
        {
            var synchronizer = await SynchronizationManager.Instance.GetSynchronizerAsync(accountId).ConfigureAwait(false);
            hasPendingOperation = synchronizer?.HasPendingOperation(CurrentMailDraftItem.MailCopy.UniqueId) ?? false;
        }

        // Newly created local drafts can have a short period where request queue is empty
        // while folder synchronization/mapping is still in progress.
        // Keep progress visible during this grace period to prevent "Send to server" flicker.
        if (!hasPendingOperation && CurrentMailDraftItem.MailCopy.IsLocalDraft)
        {
            keepBusyForInitialGracePeriod = IsWithinLocalDraftRetryGracePeriod(CurrentMailDraftItem.MailCopy);
        }

        await ExecuteUIThread(() =>
        {
            IsDraftBusy = hasPendingOperation || keepBusyForInitialGracePeriod;
            NotifyComposeActionStateChanged();
        });
    }

    private async Task TryPrepareComposeAsync(bool downloadIfNeeded)
    {
        if (CurrentMailDraftItem == null) return;

        bool isComposerInitialized = await InitializeComposerAccountAsync();

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
                    CurrentMailDraftItem.MailCopy.AssignedAccount.Id);

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

        await ExecuteUIThread(async () =>
        {
            // Extract information

            CurrentMimeMessage = replyingMime;

            ToItems.Clear();
            CCItems.Clear();
            BCCItems.Clear();

            await LoadAddressInfoAsync(replyingMime.To, ToItems);
            await LoadAddressInfoAsync(replyingMime.Cc, CCItems);
            await LoadAddressInfoAsync(replyingMime.Bcc, BCCItems);

            LoadAttachments();
            ApplyPendingSharedAttachments();

            if (replyingMime.Cc.Any() || replyingMime.Bcc.Any())
                IsCCBCCVisible = true;

            Subject = replyingMime.Subject;
            IsReadReceiptRequested = replyingMime.HasReadReceiptRequest();

            Messenger.Send(new CreateNewComposeMailRequested(renderModel));
        });

        if (RenderHtmlBodyAsyncFunc != null)
        {
            await ExecuteUIThread(async () => await RenderHtmlBodyAsyncFunc(renderModel.RenderHtml));
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
                IncludedAttachments.Add(new MailAttachmentViewModel(attachmentPart));
            }
        }
    }

    private void ApplyPendingSharedAttachments()
    {
        var draftUniqueId = CurrentMailDraftItem?.MailCopy?.UniqueId ?? Guid.Empty;

        if (draftUniqueId == Guid.Empty)
            return;

        var shareRequest = _shareActivationService.ConsumePendingComposeShareRequest(draftUniqueId);

        if (shareRequest?.Files == null || shareRequest.Files.Count == 0)
            return;

        foreach (var sharedFile in shareRequest.Files)
        {
            IncludedAttachments.Add(new MailAttachmentViewModel(sharedFile));
        }
    }

    private async Task LoadAddressInfoAsync(InternetAddressList list, ObservableCollection<AccountContact> collection)
    {
        foreach (var item in list)
        {
            if (item is MailboxAddress mailboxAddress)
            {
                var foundContact = await ContactService.GetAddressInformationByAddressAsync(mailboxAddress.Address).ConfigureAwait(false)
                    ?? new AccountContact() { Name = mailboxAddress.Name, Address = mailboxAddress.Address };

                await ExecuteUIThread(() => { collection.Add(foundContact); });
            }
            else if (item is GroupAddress groupAddress)
                await LoadAddressInfoAsync(groupAddress.Members, collection);
        }
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

    private async Task<(DraftCreationReason reason, MailCopy referenceMailCopy)> ResolveRetryDraftContextAsync()
    {
        if (CurrentMimeMessage == null || CurrentMailDraftItem?.MailCopy?.AssignedAccount == null)
            return (DraftCreationReason.Empty, null);

        var inReplyTo = CurrentMimeMessage.InReplyTo;
        if (string.IsNullOrWhiteSpace(inReplyTo) && CurrentMimeMessage.Headers.Contains(HeaderId.InReplyTo))
            inReplyTo = CurrentMimeMessage.Headers[HeaderId.InReplyTo];

        inReplyTo = MailHeaderExtensions.StripAngleBrackets(inReplyTo);
        if (string.IsNullOrWhiteSpace(inReplyTo))
            return (DraftCreationReason.Empty, null);

        var accountId = CurrentMailDraftItem.MailCopy.AssignedAccount.Id;
        var referenceMailCopy = await _mailService.GetMailCopyByMessageIdAsync(accountId, inReplyTo).ConfigureAwait(false);
        if (referenceMailCopy == null)
            return (DraftCreationReason.Empty, null);

        // We cannot perfectly reconstruct original intent (Reply vs ReplyAll) from persisted data.
        // Infer ReplyAll when multiple recipients exist on the local MIME.
        var totalRecipients = CurrentMimeMessage.To.Mailboxes.Count() + CurrentMimeMessage.Cc.Mailboxes.Count();
        var reason = totalRecipients > 1 ? DraftCreationReason.ReplyAll : DraftCreationReason.Reply;

        return (reason, referenceMailCopy);
    }

    public async Task<AccountContact> GetAddressInformationAsync(string tokenText, ObservableCollection<AccountContact> collection)
    {
        // Get model from the service. This will make sure the name is properly included if there is any record.

        var info = await ContactService.GetAddressInformationByAddressAsync(tokenText)
            ?? new AccountContact() { Name = tokenText, Address = tokenText };

        // Don't add if there is already that address in the collection.
        if (collection.Any(a => a.Address == info.Address))
            return null;

        return info;
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

        if (CurrentMailDraftItem == null) return;

        if (updatedMail.UniqueId == CurrentMailDraftItem.MailCopy.UniqueId)
        {
            await ExecuteUIThread(async () =>
            {
                CurrentMailDraftItem.UpdateFrom(updatedMail, changedProperties);
                await UpdatePendingOperationStateAsync();
                NotifyComposeActionStateChanged();
            });
        }
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
        if (localDraft == null || localDraft.CreationDate == default)
            return false;

        var elapsed = DateTime.UtcNow - localDraft.CreationDate;

        // Clock skew safety.
        if (elapsed < TimeSpan.Zero)
            return true;

        return elapsed < LocalDraftRetryGracePeriod;
    }
}
