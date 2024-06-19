using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
using Wino.Core.Domain.Models.Reader;
using Wino.Core.Extensions;
using Wino.Core.Messages.Mails;
using Wino.Core.Services;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels
{
    public partial class ComposePageViewModel : BaseViewModel
    {
        public Func<Task<string>> GetHTMLBodyFunction;

        // When we send the message or discard it, we need to block the mime update
        // Update is triggered when we leave the page.
        private bool isUpdatingMimeBlocked = false;

        public bool CanSendMail => ComposingAccount != null && !IsLocalDraft && currentMimeMessage != null;

        private MimeMessage currentMimeMessage = null;
        private readonly BodyBuilder bodyBuilder = new BodyBuilder();

        public bool IsLocalDraft => CurrentMailDraftItem != null
                                    && !string.IsNullOrEmpty(CurrentMailDraftItem.DraftId)
                                    && CurrentMailDraftItem.DraftId.StartsWith(Constants.LocalDraftStartPrefix);


        #region Properties

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLocalDraft))]
        [NotifyPropertyChangedFor(nameof(CanSendMail))]
        private MailItemViewModel currentMailDraftItem;

        [ObservableProperty]
        private bool isImportanceSelected;

        [ObservableProperty]
        private MessageImportance selectedMessageImportance;

        [ObservableProperty]
        private bool isCCBCCVisible = true;

        [ObservableProperty]
        private string subject;

        [ObservableProperty]
        private MailAccount composingAccount;

        [ObservableProperty]
        private bool isDraggingOverComposerGrid;

        [ObservableProperty]
        private bool isDraggingOverDropZone;

        public ObservableCollection<MailAttachmentViewModel> IncludedAttachments { get; set; } = new ObservableCollection<MailAttachmentViewModel>();

        public ObservableCollection<MailAccount> Accounts { get; set; } = new ObservableCollection<MailAccount>();
        public ObservableCollection<AddressInformation> ToItems { get; set; } = new ObservableCollection<AddressInformation>();
        public ObservableCollection<AddressInformation> CCItemsItems { get; set; } = new ObservableCollection<AddressInformation>();
        public ObservableCollection<AddressInformation> BCCItems { get; set; } = new ObservableCollection<AddressInformation>();


        public List<EditorToolbarSection> ToolbarSections { get; set; } = new List<EditorToolbarSection>()
        {
            new EditorToolbarSection(){ SectionType = EditorToolbarSectionType.Format },
            new EditorToolbarSection(){ SectionType = EditorToolbarSectionType.Insert },
            new EditorToolbarSection(){ SectionType = EditorToolbarSectionType.Draw },
            new EditorToolbarSection(){ SectionType = EditorToolbarSectionType.Options }
        };

        private EditorToolbarSection selectedToolbarSection;

        public EditorToolbarSection SelectedToolbarSection
        {
            get => selectedToolbarSection;
            set => SetProperty(ref selectedToolbarSection, value);
        }

        #endregion

        public INativeAppService NativeAppService { get; }

        private readonly IMailService _mailService;
        private readonly ILaunchProtocolService _launchProtocolService;
        private readonly IMimeFileService _mimeFileService;
        private readonly IStatePersistanceService _statePersistanceService;
        private readonly IFolderService _folderService;
        private readonly IAccountService _accountService;
        private readonly IWinoRequestDelegator _worker;
        public readonly IContactService ContactService;

        public ComposePageViewModel(IDialogService dialogService,
                                    IMailService mailService,
                                    ILaunchProtocolService launchProtocolService,
                                    IMimeFileService mimeFileService,
                                    IStatePersistanceService statePersistanceService,
                                    INativeAppService nativeAppService,
                                    IFolderService folderService,
                                    IAccountService accountService,
                                    IWinoRequestDelegator worker,
                                    IContactService contactService) : base(dialogService)
        {
            NativeAppService = nativeAppService;
            _folderService = folderService;
            ContactService = contactService;

            _mailService = mailService;
            _launchProtocolService = launchProtocolService;
            _mimeFileService = mimeFileService;
            _statePersistanceService = statePersistanceService;
            _accountService = accountService;
            _worker = worker;

            SelectedToolbarSection = ToolbarSections[0];
        }

        [RelayCommand]
        private void RemoveAttachment(MailAttachmentViewModel attachmentViewModel)
            => IncludedAttachments.Remove(attachmentViewModel);

        [RelayCommand]
        private async Task SendAsync()
        {
            // TODO: More detailed mail validations.

            if (!ToItems.Any())
            {
                await DialogService.ShowMessageAsync(Translator.DialogMessage_ComposerMissingRecipientMessage, Translator.DialogMessage_ComposerValidationFailedTitle);
                return;
            }

            if (string.IsNullOrEmpty(Subject))
            {
                var isConfirmed = await DialogService.ShowConfirmationDialogAsync(Translator.DialogMessage_EmptySubjectConfirmationMessage, Translator.DialogMessage_EmptySubjectConfirmation, Translator.Buttons_Yes);

                if (!isConfirmed) return;
            }

            // Save mime changes before sending.
            await UpdateMimeChangesAsync().ConfigureAwait(false);

            isUpdatingMimeBlocked = true;

            var assignedAccount = CurrentMailDraftItem.AssignedAccount;
            var sentFolder = await _folderService.GetSpecialFolderByAccountIdAsync(assignedAccount.Id, SpecialFolderType.Sent);
            var draftSendPreparationRequest = new SendDraftPreparationRequest(CurrentMailDraftItem.MailCopy, currentMimeMessage, CurrentMailDraftItem.AssignedFolder, sentFolder, CurrentMailDraftItem.AssignedAccount.Preferences);

            await _worker.ExecuteAsync(draftSendPreparationRequest);
        }

        private async Task UpdateMimeChangesAsync()
        {
            if (isUpdatingMimeBlocked || currentMimeMessage == null || ComposingAccount == null || CurrentMailDraftItem == null) return;

            // Save recipients.

            SaveAddressInfo(ToItems, currentMimeMessage.To);
            SaveAddressInfo(CCItemsItems, currentMimeMessage.Cc);
            SaveAddressInfo(BCCItems, currentMimeMessage.Bcc);

            SaveImportance();
            SaveSubject();

            await SaveAttachmentsAsync();
            await SaveBodyAsync();
            await UpdateMailCopyAsync();

            // Save mime file.
            await _mimeFileService.SaveMimeMessageAsync(CurrentMailDraftItem.MailCopy.FileId, currentMimeMessage, ComposingAccount.Id).ConfigureAwait(false);
        }

        private async Task UpdateMailCopyAsync()
        {
            CurrentMailDraftItem.Subject = currentMimeMessage.Subject;
            CurrentMailDraftItem.PreviewText = currentMimeMessage.TextBody;

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

        private void SaveImportance() { currentMimeMessage.Importance = IsImportanceSelected ? SelectedMessageImportance : MessageImportance.Normal; }

        private void SaveSubject()
        {
            if (Subject != null)
            {
                currentMimeMessage.Subject = Subject;
            }
        }

        private async Task SaveBodyAsync()
        {
            if (GetHTMLBodyFunction != null)
                bodyBuilder.HtmlBody = Regex.Unescape(await GetHTMLBodyFunction());

            if (!string.IsNullOrEmpty(bodyBuilder.HtmlBody))
                bodyBuilder.TextBody = HtmlAgilityPackExtensions.GetPreviewText(bodyBuilder.HtmlBody);

            if (bodyBuilder.HtmlBody != null && bodyBuilder.TextBody != null)
                currentMimeMessage.Body = bodyBuilder.ToMessageBody();
        }

        [RelayCommand]
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
                if (CurrentMailDraftItem.IsLocalDraft)
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

        public override async void OnNavigatedFrom(NavigationMode mode, object parameters)
        {
            base.OnNavigatedFrom(mode, parameters);

            await UpdateMimeChangesAsync().ConfigureAwait(false);

            await ExecuteUIThread(() => { _statePersistanceService.IsReadingMail = false; });
        }

        public override async void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            if (parameters != null && parameters is MailItemViewModel mailItem)
            {
                await LoadAccountsAsync();

                CurrentMailDraftItem = mailItem;

                _ = TryPrepareComposeAsync(true);
            }

            ToItems.CollectionChanged -= ContactListCollectionChanged;
            ToItems.CollectionChanged += ContactListCollectionChanged;

            _statePersistanceService.IsReadingMail = true;

            // Check if there is any delivering mail address from protocol launch.

            if (_launchProtocolService.MailtoParameters != null)
            {
                // TODO
                //var requestedMailContact = await GetAddressInformationAsync(_launchProtocolService.MailtoParameters, ToItems);

                //if (requestedMailContact != null)
                //{
                //    ToItems.Add(requestedMailContact);
                //}
                //else
                //    DialogService.InfoBarMessage("Invalid Address", "Address is not a valid e-mail address.", InfoBarMessageType.Warning);

                // Clear the address.
                _launchProtocolService.MailtoParameters = null;
            }
        }

        private void ContactListCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
            {
                // Prevent duplicates.
                if (!(sender is ObservableCollection<AddressInformation> list))
                    return;

                foreach (var item in e.NewItems)
                {
                    if (item is AddressInformation addedInfo && list.Count(a => a == addedInfo) > 1)
                    {
                        var addedIndex = list.IndexOf(addedInfo);
                        list.RemoveAt(addedIndex);
                    }
                }
            }
        }

        private async Task LoadAccountsAsync()
        {
            // Load accounts

            var accounts = await _accountService.GetAccountsAsync();

            foreach (var account in accounts)
            {
                Accounts.Add(account);
            }
        }

        private async Task<bool> InitializeComposerAccountAsync()
        {
            if (ComposingAccount != null) return true;

            if (CurrentMailDraftItem == null)
                return false;

            await ExecuteUIThread(() =>
            {
                ComposingAccount = Accounts.FirstOrDefault(a => a.Id == CurrentMailDraftItem.AssignedAccount.Id);
            });

            return ComposingAccount != null;
        }

        private async Task TryPrepareComposeAsync(bool downloadIfNeeded)
        {
            if (CurrentMailDraftItem == null)
                return;

            bool isComposerInitialized = await InitializeComposerAccountAsync();

            if (!isComposerInitialized)
            {
                return;
            }

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
                    // TODO: Folder id needs to be passed.
                    // TODO: Send mail retrieve request.
                    // _worker.Queue(new FetchSingleItemRequest(ComposingAccount.Id, CurrentMailDraftItem.Id, string.Empty));
                }
                //else
                //    DialogService.ShowMIMENotFoundMessage();

                return;
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

            await ExecuteUIThread(() =>
            {
                // Extract information

                ToItems.Clear();
                CCItemsItems.Clear();
                BCCItems.Clear();

                LoadAddressInfo(replyingMime.To, ToItems);
                LoadAddressInfo(replyingMime.Cc, CCItemsItems);
                LoadAddressInfo(replyingMime.Bcc, BCCItems);

                LoadAttachments(replyingMime.Attachments);

                Subject = replyingMime.Subject;

                currentMimeMessage = replyingMime;

                OnPropertyChanged(nameof(CanSendMail));
                Messenger.Send(new CreateNewComposeMailRequested(renderModel));
            });
        }

        private void LoadAttachments(IEnumerable<MimeEntity> mimeEntities)
        {
            foreach (var attachment in mimeEntities)
            {
                if (attachment.IsAttachment && attachment is MimePart attachmentPart)
                {
                    IncludedAttachments.Add(new MailAttachmentViewModel(attachmentPart));
                }
            }
        }

        private void LoadAddressInfo(InternetAddressList list, ObservableCollection<AddressInformation> collection)
        {
            foreach (var item in list)
            {
                if (item is MailboxAddress mailboxAddress)
                    collection.Add(mailboxAddress.ToAddressInformation());
                else if (item is GroupAddress groupAddress)
                    LoadAddressInfo(groupAddress.Members, collection);
            }
        }

        private void SaveAddressInfo(IEnumerable<AddressInformation> addresses, InternetAddressList list)
        {
            list.Clear();

            foreach (var item in addresses)
                list.Add(new MailboxAddress(item.Name, item.Address));
        }

        public async Task<AddressInformation> GetAddressInformationAsync(string tokenText, ObservableCollection<AddressInformation> collection)
        {
            // Get model from the service. This will make sure the name is properly included if there is any record.

            var info = await ContactService.GetAddressInformationByAddressAsync(tokenText);

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
                    CurrentMailDraftItem.Update(updatedMail);
                    OnPropertyChanged(nameof(CanSendMail));
                });
            }
        }
    }
}
