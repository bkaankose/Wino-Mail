using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MimeKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Extensions;
using Wino.Core.Services;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Server;

namespace Wino.Mail.ViewModels
{
    public partial class ComposePageViewModel : BaseViewModel
    {
        public Func<Task<string>> GetHTMLBodyFunction;

        // When we send the message or discard it, we need to block the mime update
        // Update is triggered when we leave the page.
        private bool isUpdatingMimeBlocked = false;

        private bool canSendMail => ComposingAccount != null && !IsLocalDraft && CurrentMimeMessage != null;

        [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
        [NotifyCanExecuteChangedFor(nameof(SendCommand))]
        [ObservableProperty]
        private MimeMessage currentMimeMessage = null;

        private readonly BodyBuilder bodyBuilder = new BodyBuilder();

        public bool IsLocalDraft => CurrentMailDraftItem?.MailCopy?.IsLocalDraft ?? true;

        #region Properties

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLocalDraft))]
        [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
        [NotifyCanExecuteChangedFor(nameof(SendCommand))]
        private MailItemViewModel currentMailDraftItem;

        [ObservableProperty]
        private bool isImportanceSelected;

        [ObservableProperty]
        private MessageImportance selectedMessageImportance;

        [ObservableProperty]
        private bool isCCBCCVisible;

        [ObservableProperty]
        private string subject;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
        [NotifyCanExecuteChangedFor(nameof(SendCommand))]
        private MailAccount composingAccount;

        [ObservableProperty]
        private List<MailAccountAlias> availableAliases;

        [ObservableProperty]
        private MailAccountAlias selectedAlias;

        [ObservableProperty]
        private bool isDraggingOverComposerGrid;

        [ObservableProperty]
        private bool isDraggingOverFilesDropZone;

        [ObservableProperty]
        private bool isDraggingOverImagesDropZone;

        public ObservableCollection<MailAttachmentViewModel> IncludedAttachments { get; set; } = [];
        public ObservableCollection<MailAccount> Accounts { get; set; } = [];
        public ObservableCollection<AccountContact> ToItems { get; set; } = [];
        public ObservableCollection<AccountContact> CCItems { get; set; } = [];
        public ObservableCollection<AccountContact> BCCItems { get; set; } = [];

        #endregion

        public INativeAppService NativeAppService { get; }

        private readonly IMailService _mailService;
        private readonly IMimeFileService _mimeFileService;
        private readonly IFolderService _folderService;
        private readonly IAccountService _accountService;
        private readonly IWinoRequestDelegator _worker;
        public readonly IFontService FontService;
        public readonly IPreferencesService PreferencesService;
        private readonly IWinoServerConnectionManager _winoServerConnectionManager;
        public readonly IContactService ContactService;

        public ComposePageViewModel(IDialogService dialogService,
                                    IMailService mailService,
                                    IMimeFileService mimeFileService,
                                    INativeAppService nativeAppService,
                                    IFolderService folderService,
                                    IAccountService accountService,
                                    IWinoRequestDelegator worker,
                                    IContactService contactService,
                                    IFontService fontService,
                                    IPreferencesService preferencesService,
                                    IWinoServerConnectionManager winoServerConnectionManager) : base(dialogService)
        {
            NativeAppService = nativeAppService;
            ContactService = contactService;
            FontService = fontService;
            PreferencesService = preferencesService;

            _folderService = folderService;
            _mailService = mailService;
            _mimeFileService = mimeFileService;
            _accountService = accountService;
            _worker = worker;
            _winoServerConnectionManager = winoServerConnectionManager;
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
                await DialogService.ShowMessageAsync(Translator.DialogMessage_ComposerMissingRecipientMessage,
                                                     Translator.DialogMessage_ComposerValidationFailedTitle,
                                                     WinoCustomMessageDialogIcon.Warning);
                return;
            }

            if (string.IsNullOrEmpty(Subject))
            {
                var isConfirmed = await DialogService.ShowConfirmationDialogAsync(Translator.DialogMessage_EmptySubjectConfirmationMessage, Translator.DialogMessage_EmptySubjectConfirmation, Translator.Buttons_Yes);

                if (!isConfirmed) return;
            }

            if (SelectedAlias == null)
            {
                DialogService.InfoBarMessage(Translator.DialogMessage_AliasNotSelectedTitle, Translator.DialogMessage_AliasNotSelectedMessage, InfoBarMessageType.Error);
                return;
            }

            // Save mime changes before sending.
            await UpdateMimeChangesAsync().ConfigureAwait(false);

            isUpdatingMimeBlocked = true;

            var assignedAccount = CurrentMailDraftItem.AssignedAccount;
            var sentFolder = await _folderService.GetSpecialFolderByAccountIdAsync(assignedAccount.Id, SpecialFolderType.Sent);

            using MemoryStream memoryStream = new();
            CurrentMimeMessage.WriteTo(FormatOptions.Default, memoryStream);
            byte[] buffer = memoryStream.GetBuffer();
            int count = (int)memoryStream.Length;

            var base64EncodedMessage = Convert.ToBase64String(buffer);
            var draftSendPreparationRequest = new SendDraftPreparationRequest(CurrentMailDraftItem.MailCopy,
                                                                              SelectedAlias,
                                                                              sentFolder,
                                                                              CurrentMailDraftItem.AssignedFolder,
                                                                              CurrentMailDraftItem.AssignedAccount.Preferences,
                                                                              base64EncodedMessage);

            await _worker.ExecuteAsync(draftSendPreparationRequest);
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
        {
            if (ComposingAccount == null)
            {
                DialogService.InfoBarMessage(Translator.Info_MessageCorruptedTitle, Translator.Info_MessageCorruptedMessage, InfoBarMessageType.Error);
                return;
            }

            var confirmation = await DialogService.ShowConfirmationDialogAsync(Translator.DialogMessage_DiscardDraftConfirmationMessage,
                                                                               Translator.DialogMessage_DiscardDraftConfirmationTitle,
                                                                               Translator.Buttons_Yes);

            if (confirmation)
            {
                isUpdatingMimeBlocked = true;

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
        }

        public override void OnNavigatedFrom(NavigationMode mode, object parameters)
        {
            base.OnNavigatedFrom(mode, parameters);

            /// Do not put any code here.
            /// Make sure to use Page's OnNavigatedTo instead.
        }

        public override async void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            if (parameters != null && parameters is MailItemViewModel mailItem)
            {
                CurrentMailDraftItem = mailItem;

                await TryPrepareComposeAsync(true);
            }
        }

        private async Task<bool> InitializeComposerAccountAsync()
        {
            if (CurrentMailDraftItem == null) return false;

            if (ComposingAccount != null) return true;

            var composingAccount = await _accountService.GetAccountAsync(CurrentMailDraftItem.AssignedAccount.Id).ConfigureAwait(false);
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

            primaryAlias ??= await _accountService.GetPrimaryAccountAliasAsync(ComposingAccount.Id).ConfigureAwait(false);

            await ExecuteUIThread(() =>
            {
                ComposingAccount = composingAccount;
                AvailableAliases = aliases;
                SelectedAlias = primaryAlias;
            });

            return true;
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

                    var package = new DownloadMissingMessageRequested(CurrentMailDraftItem.AssignedAccount.Id, CurrentMailDraftItem.MailCopy);
                    var downloadResponse = await _winoServerConnectionManager.GetResponseAsync<bool, DownloadMissingMessageRequested>(package);

                    if (downloadResponse.IsSuccess)
                    {
                        goto retry;
                    }
                }
                else
                    DialogService.InfoBarMessage(Translator.Info_ComposerMissingMIMETitle, Translator.Info_ComposerMissingMIMEMessage, InfoBarMessageType.Error);

                return;
            }
            catch (IOException)
            {
                DialogService.InfoBarMessage(Translator.Busy, Translator.Exception_MailProcessing, InfoBarMessageType.Warning);
            }
            catch (ComposerMimeNotFoundException)
            {
                DialogService.InfoBarMessage(Translator.Info_ComposerMissingMIMETitle, Translator.Info_ComposerMissingMIMEMessage, InfoBarMessageType.Error);
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

                if (replyingMime.Cc.Any() || replyingMime.Bcc.Any())
                    IsCCBCCVisible = true;

                Subject = replyingMime.Subject;

                Messenger.Send(new CreateNewComposeMailRequested(renderModel));
            });
        }

        private void LoadAttachments()
        {
            if (CurrentMimeMessage == null) return;

            foreach (var attachment in CurrentMimeMessage.Attachments)
            {
                if (attachment.IsAttachment && attachment is MimePart attachmentPart)
                {
                    IncludedAttachments.Add(new MailAttachmentViewModel(attachmentPart));
                }
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
            CurrentMimeMessage.From.Add(new MailboxAddress(ComposingAccount.SenderName, SelectedAlias.AliasAddress));
        }

        private void SaveReplyToAddress()
        {
            if (SelectedAlias == null) return;

            if (!string.IsNullOrEmpty(SelectedAlias.ReplyToAddress))
            {
                if (!CurrentMimeMessage.ReplyTo.Any(a => a is MailboxAddress mailboxAddress && mailboxAddress.Address == SelectedAlias.ReplyToAddress))
                {
                    CurrentMimeMessage.ReplyTo.Clear();
                    CurrentMimeMessage.ReplyTo.Add(new MailboxAddress(SelectedAlias.ReplyToAddress, SelectedAlias.ReplyToAddress));
                }
            }
        }

        private void SaveAddressInfo(IEnumerable<AccountContact> addresses, InternetAddressList list)
        {
            list.Clear();

            foreach (var item in addresses)
                list.Add(new MailboxAddress(item.Name, item.Address));
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
            DialogService.InfoBarMessage(Translator.Info_ContactExistsTitle, Translator.Info_ContactExistsMessage, InfoBarMessageType.Warning);
        }

        public void NotifyInvalidEmail(string address)
        {
            DialogService.InfoBarMessage(Translator.Info_InvalidAddressTitle, string.Format(Translator.Info_InvalidAddressMessage, address), InfoBarMessageType.Warning);
        }

        protected override async void OnMailUpdated(MailCopy updatedMail)
        {
            base.OnMailUpdated(updatedMail);

            if (CurrentMailDraftItem == null) return;

            if (updatedMail.UniqueId == CurrentMailDraftItem.UniqueId)
            {
                await ExecuteUIThread(() =>
                {
                    CurrentMailDraftItem.MailCopy = updatedMail;

                    DiscardCommand.NotifyCanExecuteChanged();
                    SendCommand.NotifyCanExecuteChanged();
                });
            }
        }
    }
}
