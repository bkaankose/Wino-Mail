using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MailKit;
using Microsoft.AppCenter.Crashes;
using MimeKit;
using Serilog;
using Wino.Core;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Reader;
using Wino.Core.Extensions;
using Wino.Core.Services;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Messages;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Server;

namespace Wino.Mail.ViewModels
{
    public partial class MailRenderingPageViewModel : BaseViewModel,
        IRecipient<NewMailItemRenderingRequestedEvent>,
        ITransferProgress // For listening IMAP message download progress.
    {
        private readonly IUnderlyingThemeService _underlyingThemeService;

        private readonly IMimeFileService _mimeFileService;
        private readonly Core.Domain.Interfaces.IMailService _mailService;
        private readonly IFileService _fileService;
        private readonly IWinoRequestDelegator _requestDelegator;
        private readonly IClipboardService _clipboardService;
        private readonly IUnsubscriptionService _unsubscriptionService;
        private readonly IWinoServerConnectionManager _winoServerConnectionManager;
        private bool forceImageLoading = false;

        private MailItemViewModel initializedMailItemViewModel = null;
        private MimeMessageInformation initializedMimeMessageInformation = null;

        #region Properties

        public bool ShouldDisplayDownloadProgress => IsIndetermineProgress || (CurrentDownloadPercentage > 0 && CurrentDownloadPercentage <= 100);
        public bool CanUnsubscribe => CurrentRenderModel?.UnsubscribeInfo?.CanUnsubscribe ?? false;
        public bool IsJunkMail => initializedMailItemViewModel?.AssignedFolder != null && initializedMailItemViewModel.AssignedFolder.SpecialFolderType == SpecialFolderType.Junk;

        public bool IsImageRenderingDisabled
        {
            get
            {
                if (IsJunkMail)
                {
                    return !forceImageLoading;
                }
                else
                {
                    return !CurrentRenderModel?.MailRenderingOptions?.LoadImages ?? false;
                }
            }
        }

        private bool isDarkWebviewRenderer;
        public bool IsDarkWebviewRenderer
        {
            get => isDarkWebviewRenderer;
            set
            {
                if (SetProperty(ref isDarkWebviewRenderer, value))
                {
                    InitializeCommandBarItems();
                }
            }
        }

        public bool HasMultipleAttachments => Attachments.Count > 1;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShouldDisplayDownloadProgress))]
        private bool isIndetermineProgress;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShouldDisplayDownloadProgress))]
        private double currentDownloadPercentage;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanUnsubscribe))]
        private MailRenderModel currentRenderModel;

        [ObservableProperty]
        private string subject;

        [ObservableProperty]
        private string fromAddress;

        [ObservableProperty]
        private string fromName;

        [ObservableProperty]
        private DateTime creationDate;


        public ObservableCollection<AddressInformation> ToItems { get; set; } = new ObservableCollection<AddressInformation>();
        public ObservableCollection<AddressInformation> CCItemsItems { get; set; } = new ObservableCollection<AddressInformation>();
        public ObservableCollection<AddressInformation> BCCItems { get; set; } = new ObservableCollection<AddressInformation>();
        public ObservableCollection<MailAttachmentViewModel> Attachments { get; set; } = new ObservableCollection<MailAttachmentViewModel>();
        public ObservableCollection<MailOperationMenuItem> MenuItems { get; set; } = new ObservableCollection<MailOperationMenuItem>();

        #endregion

        public INativeAppService NativeAppService { get; }
        public IStatePersistanceService StatePersistenceService { get; }
        public IPreferencesService PreferencesService { get; }

        public MailRenderingPageViewModel(IDialogService dialogService,
                                          INativeAppService nativeAppService,
                                          IUnderlyingThemeService underlyingThemeService,
                                          IMimeFileService mimeFileService,
                                          Core.Domain.Interfaces.IMailService mailService,
                                          IFileService fileService,
                                          IWinoRequestDelegator requestDelegator,
                                          IStatePersistanceService statePersistenceService,
                                          IClipboardService clipboardService,
                                          IUnsubscriptionService unsubscriptionService,
                                          IPreferencesService preferencesService,
                                          IWinoServerConnectionManager winoServerConnectionManager) : base(dialogService)
        {
            NativeAppService = nativeAppService;
            StatePersistenceService = statePersistenceService;
            PreferencesService = preferencesService;
            _winoServerConnectionManager = winoServerConnectionManager;
            _clipboardService = clipboardService;
            _unsubscriptionService = unsubscriptionService;
            _underlyingThemeService = underlyingThemeService;
            _mimeFileService = mimeFileService;
            _mailService = mailService;
            _fileService = fileService;
            _requestDelegator = requestDelegator;
        }


        [RelayCommand]
        private async Task CopyClipboard(string copyText)
        {
            try
            {
                await _clipboardService.CopyClipboardAsync(copyText);

                DialogService.InfoBarMessage(Translator.ClipboardTextCopied_Title, string.Format(Translator.ClipboardTextCopied_Message, copyText), InfoBarMessageType.Information);
            }
            catch (Exception)
            {
                DialogService.InfoBarMessage(Translator.GeneralTitle_Error, string.Format(Translator.ClipboardTextCopyFailed_Message, copyText), InfoBarMessageType.Error);
            }
        }

        [RelayCommand]
        private async Task ForceImageLoading()
        {
            forceImageLoading = true;

            if (initializedMailItemViewModel == null && initializedMimeMessageInformation == null) return;

            await RenderAsync(initializedMimeMessageInformation);
        }

        [RelayCommand]
        private async Task UnsubscribeAsync()
        {
            if (!(CurrentRenderModel?.UnsubscribeInfo?.CanUnsubscribe ?? false)) return;

            bool confirmed;

            // Try to unsubscribe by http first.
            if (CurrentRenderModel.UnsubscribeInfo.HttpLink is not null)
            {
                if (!Uri.IsWellFormedUriString(CurrentRenderModel.UnsubscribeInfo.HttpLink, UriKind.RelativeOrAbsolute))
                {
                    DialogService.InfoBarMessage(Translator.Info_UnsubscribeLinkInvalidTitle, Translator.Info_UnsubscribeLinkInvalidMessage, InfoBarMessageType.Error);
                    return;
                }

                // Support for List-Unsubscribe-Post header. It can be done without launching browser.
                // https://datatracker.ietf.org/doc/html/rfc8058
                if (CurrentRenderModel.UnsubscribeInfo.IsOneClick)
                {
                    confirmed = await DialogService.ShowConfirmationDialogAsync(string.Format(Translator.DialogMessage_UnsubscribeConfirmationOneClickMessage, FromName), Translator.DialogMessage_UnsubscribeConfirmationTitle, Translator.Unsubscribe);
                    if (!confirmed) return;

                    bool isOneClickUnsubscribed = await _unsubscriptionService.OneClickUnsubscribeAsync(CurrentRenderModel.UnsubscribeInfo);

                    if (isOneClickUnsubscribed)
                    {
                        DialogService.InfoBarMessage(Translator.Unsubscribe, string.Format(Translator.Info_UnsubscribeSuccessMessage, FromName), InfoBarMessageType.Success);
                    }
                    else
                    {
                        DialogService.InfoBarMessage(Translator.GeneralTitle_Error, Translator.Info_UnsubscribeErrorMessage, InfoBarMessageType.Error);
                    }
                }
                else
                {
                    confirmed = await DialogService.ShowConfirmationDialogAsync(string.Format(Translator.DialogMessage_UnsubscribeConfirmationGoToWebsiteMessage, FromName), Translator.DialogMessage_UnsubscribeConfirmationTitle, Translator.DialogMessage_UnsubscribeConfirmationGoToWebsiteConfirmButton);
                    if (!confirmed) return;

                    await NativeAppService.LaunchUriAsync(new Uri(CurrentRenderModel.UnsubscribeInfo.HttpLink));
                }
            }
            else if (CurrentRenderModel.UnsubscribeInfo.MailToLink is not null)
            {
                confirmed = await DialogService.ShowConfirmationDialogAsync(string.Format(Translator.DialogMessage_UnsubscribeConfirmationMailtoMessage, FromName, new string(CurrentRenderModel.UnsubscribeInfo.MailToLink.Skip(7).ToArray())), Translator.DialogMessage_UnsubscribeConfirmationTitle, Translator.Unsubscribe);

                if (!confirmed) return;

                // TODO: Implement automatic mail send after user confirms the action.
                // Currently it will launch compose page and user should manually press send button.
                await NativeAppService.LaunchUriAsync(new Uri(CurrentRenderModel.UnsubscribeInfo.MailToLink));
            }
        }

        [RelayCommand]
        private async Task OperationClicked(MailOperationMenuItem menuItem)
        {
            if (menuItem == null) return;

            await HandleMailOperationAsync(menuItem.Operation);
        }

        private async Task HandleMailOperationAsync(MailOperation operation)
        {
            // Toggle theme
            if (operation == MailOperation.DarkEditor || operation == MailOperation.LightEditor)
                IsDarkWebviewRenderer = !IsDarkWebviewRenderer;
            else if (operation == MailOperation.SaveAs)
            {
                // Save as PDF
                var pickedFolder = await DialogService.PickWindowsFolderAsync();

                if (!string.IsNullOrEmpty(pickedFolder))
                {
                    var fullPath = Path.Combine(pickedFolder, $"{initializedMailItemViewModel.FromAddress}.pdf");
                    Messenger.Send(new SaveAsPDFRequested(fullPath));
                }
            }
            else if (operation == MailOperation.Reply || operation == MailOperation.ReplyAll || operation == MailOperation.Forward)
            {
                if (initializedMailItemViewModel == null) return;

                // Create new draft.
                var draftOptions = new DraftCreationOptions()
                {
                    Reason = operation switch
                    {
                        MailOperation.Reply => DraftCreationReason.Reply,
                        MailOperation.ReplyAll => DraftCreationReason.ReplyAll,
                        MailOperation.Forward => DraftCreationReason.Forward,
                        _ => DraftCreationReason.Empty
                    },
                    ReferencedMessage = new ReferencedMessage()
                    {
                        MimeMessage = initializedMimeMessageInformation.MimeMessage,
                        MailCopy = initializedMailItemViewModel.MailCopy
                    }
                };

                var (draftMailCopy, draftBase64MimeMessage) = await _mailService.CreateDraftAsync(initializedMailItemViewModel.AssignedAccount, draftOptions).ConfigureAwait(false);

                var draftPreparationRequest = new DraftPreparationRequest(initializedMailItemViewModel.AssignedAccount, draftMailCopy, draftBase64MimeMessage, initializedMailItemViewModel.MailCopy);

                await _requestDelegator.ExecuteAsync(draftPreparationRequest);

            }
            else if (initializedMailItemViewModel != null)
            {
                // All other operations require a mail item.
                var prepRequest = new MailOperationPreperationRequest(operation, initializedMailItemViewModel.MailCopy);
                await _requestDelegator.ExecuteAsync(prepRequest);
            }
        }

        private CancellationTokenSource renderCancellationTokenSource = new CancellationTokenSource();

        public override async void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            Attachments.CollectionChanged -= AttachmentsUpdated;
            Attachments.CollectionChanged += AttachmentsUpdated;

            renderCancellationTokenSource.Cancel();

            initializedMailItemViewModel = null;
            initializedMimeMessageInformation = null;

            // This page can be accessed for 2 purposes.
            // 1. Rendering a mail item when the user selects.
            // 2. Rendering an existing EML file with MimeMessage.

            // MimeMessage rendering must be readonly and no command bar items must be shown except common
            // items like dark/light editor, zoom, print etc.

            // Configure common rendering properties first.
            IsDarkWebviewRenderer = _underlyingThemeService.IsUnderlyingThemeDark();

            await ResetPagePropertiesAsync();

            renderCancellationTokenSource = new CancellationTokenSource();

            // Mime content might not be available for now and might require a download.
            try
            {
                if (parameters is MailItemViewModel selectedMailItemViewModel)
                    await RenderAsync(selectedMailItemViewModel, renderCancellationTokenSource.Token);
                else if (parameters is MimeMessageInformation mimeMessageInformation)
                    await RenderAsync(mimeMessageInformation);

                InitializeCommandBarItems();
            }
            catch (OperationCanceledException)
            {
                Log.Information("Canceled mail rendering.");
            }
            catch (Exception ex)
            {
                DialogService.InfoBarMessage(Translator.Info_MailRenderingFailedTitle, string.Format(Translator.Info_MailRenderingFailedMessage, ex.Message), InfoBarMessageType.Error);

                Crashes.TrackError(ex);
                Log.Error(ex, "Render Failed");
            }
        }

        private async void AttachmentsUpdated(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            await ExecuteUIThread(() => { OnPropertyChanged(nameof(HasMultipleAttachments)); });
        }

        private async Task HandleSingleItemDownloadAsync(MailItemViewModel mailItemViewModel)
        {
            try
            {
                // To show the progress on the UI.
                CurrentDownloadPercentage = 1;

                var package = new DownloadMissingMessageRequested(mailItemViewModel.AssignedAccount.Id, mailItemViewModel.MailCopy);
                await _winoServerConnectionManager.GetResponseAsync<bool, DownloadMissingMessageRequested>(package);
            }
            catch (OperationCanceledException)
            {
                Log.Information("MIME download is canceled.");
            }
            catch (Exception ex)
            {
                DialogService.InfoBarMessage(Translator.GeneralTitle_Error, ex.Message, InfoBarMessageType.Error);
            }
            finally
            {
                ResetProgress();
            }
        }

        private async Task RenderAsync(MailItemViewModel mailItemViewModel, CancellationToken cancellationToken = default)
        {
            var isMimeExists = await _mimeFileService.IsMimeExistAsync(mailItemViewModel.AssignedAccount.Id, mailItemViewModel.MailCopy.FileId);

            if (!isMimeExists)
            {
                await HandleSingleItemDownloadAsync(mailItemViewModel);
            }

            // Find the MIME for this item and render it.
            var mimeMessageInformation = await _mimeFileService.GetMimeMessageInformationAsync(mailItemViewModel.MailCopy.FileId,
                                                                                               mailItemViewModel.AssignedAccount.Id,
                                                                                               cancellationToken).ConfigureAwait(false);

            if (mimeMessageInformation == null)
            {
                DialogService.InfoBarMessage(Translator.Info_MessageCorruptedTitle, Translator.Info_MessageCorruptedMessage, InfoBarMessageType.Error);
                return;
            }

            initializedMailItemViewModel = mailItemViewModel;
            await RenderAsync(mimeMessageInformation);
        }

        private async Task RenderAsync(MimeMessageInformation mimeMessageInformation)
        {
            var message = mimeMessageInformation.MimeMessage;
            var messagePath = mimeMessageInformation.Path;

            initializedMimeMessageInformation = mimeMessageInformation;

            // TODO: Handle S/MIME decryption.
            // initializedMimeMessageInformation.MimeMessage.Body is MultipartSigned

            var renderingOptions = PreferencesService.GetRenderingOptions();

            await ExecuteUIThread(() =>
            {
                Attachments.Clear();

                Subject = message.Subject;

                // TODO: FromName and FromAddress is probably not correct here for mail lists.
                FromAddress = message.From.Mailboxes.FirstOrDefault()?.Address ?? Translator.UnknownAddress;
                FromName = message.From.Mailboxes.FirstOrDefault()?.Name ?? Translator.UnknownSender;
                CreationDate = message.Date.DateTime;

                // Extract to,cc and bcc
                LoadAddressInfo(message.To, ToItems);
                LoadAddressInfo(message.Cc, CCItemsItems);
                LoadAddressInfo(message.Bcc, BCCItems);

                // Automatically disable images for Junk folder to prevent pixel tracking.
                // This can only work for selected mail item rendering, not for EML file rendering.
                if (initializedMailItemViewModel != null &&
                    initializedMailItemViewModel.AssignedFolder.SpecialFolderType == SpecialFolderType.Junk)
                {
                    renderingOptions.LoadImages = false;
                }

                // Load images if forced.
                if (forceImageLoading)
                {
                    renderingOptions.LoadImages = true;
                }

                CurrentRenderModel = _mimeFileService.GetMailRenderModel(message, messagePath, renderingOptions);

                Messenger.Send(new HtmlRenderingRequested(CurrentRenderModel.RenderHtml));

                foreach (var attachment in CurrentRenderModel.Attachments)
                {
                    Attachments.Add(new MailAttachmentViewModel(attachment));
                }

                OnPropertyChanged(nameof(IsImageRenderingDisabled));

                StatePersistenceService.IsReadingMail = true;
            });
        }

        public override void OnNavigatedFrom(NavigationMode mode, object parameters)
        {
            base.OnNavigatedFrom(mode, parameters);

            Attachments.CollectionChanged -= AttachmentsUpdated;

            renderCancellationTokenSource.Cancel();
            CurrentDownloadPercentage = 0d;

            initializedMailItemViewModel = null;
            initializedMimeMessageInformation = null;

            forceImageLoading = false;

            ToItems.Clear();
            CCItemsItems.Clear();
            BCCItems.Clear();
            Attachments.Clear();
            MenuItems.Clear();

            StatePersistenceService.IsReadingMail = false;
        }

        private void LoadAddressInfo(InternetAddressList list, ObservableCollection<AddressInformation> collection)
        {
            collection.Clear();

            foreach (var item in list)
            {
                if (item is MailboxAddress mailboxAddress)
                    collection.Add(mailboxAddress.ToAddressInformation());
                else if (item is GroupAddress groupAddress)
                    LoadAddressInfo(groupAddress.Members, collection);
            }
        }

        private void ResetProgress()
        {
            CurrentDownloadPercentage = 0;
            IsIndetermineProgress = false;
        }

        private async Task ResetPagePropertiesAsync()
        {
            await ExecuteUIThread(() =>
            {
                ResetProgress();

                ToItems.Clear();
                CCItemsItems.Clear();
                BCCItems.Clear();
                Attachments.Clear();

                // Dispose existing content first.
                Messenger.Send(new CancelRenderingContentRequested());
            });
        }

        private void InitializeCommandBarItems()
        {
            MenuItems.Clear();

            // Add light/dark editor theme switch.
            if (IsDarkWebviewRenderer)
                MenuItems.Add(MailOperationMenuItem.Create(MailOperation.LightEditor));
            else
                MenuItems.Add(MailOperationMenuItem.Create(MailOperation.DarkEditor));

            // Save As PDF
            MenuItems.Add(MailOperationMenuItem.Create(MailOperation.SaveAs, true, true));

            if (initializedMailItemViewModel == null)
                return;

            MenuItems.Add(MailOperationMenuItem.Create(MailOperation.Seperator));

            // You can't do these to draft items.
            if (!initializedMailItemViewModel.IsDraft)
            {
                // Reply
                MenuItems.Add(MailOperationMenuItem.Create(MailOperation.Reply));

                // Reply All
                MenuItems.Add(MailOperationMenuItem.Create(MailOperation.ReplyAll));

                // Forward
                MenuItems.Add(MailOperationMenuItem.Create(MailOperation.Forward));
            }

            // Archive - Unarchive
            if (initializedMailItemViewModel.AssignedFolder.SpecialFolderType == SpecialFolderType.Archive)
                MenuItems.Add(MailOperationMenuItem.Create(MailOperation.UnArchive));
            else
                MenuItems.Add(MailOperationMenuItem.Create(MailOperation.Archive));

            // Delete
            MenuItems.Add(MailOperationMenuItem.Create(MailOperation.SoftDelete));

            // Flag - Clear Flag
            if (initializedMailItemViewModel.IsFlagged)
                MenuItems.Add(MailOperationMenuItem.Create(MailOperation.ClearFlag));
            else
                MenuItems.Add(MailOperationMenuItem.Create(MailOperation.SetFlag));

            // Secondary items.

            // Read - Unread
            if (initializedMailItemViewModel.IsRead)
                MenuItems.Add(MailOperationMenuItem.Create(MailOperation.MarkAsUnread, true, false));
            else
                MenuItems.Add(MailOperationMenuItem.Create(MailOperation.MarkAsRead, true, false));
        }

        protected override async void OnMailUpdated(MailCopy updatedMail)
        {
            base.OnMailUpdated(updatedMail);

            if (initializedMailItemViewModel == null) return;

            // Check if the updated mail is the same mail item we are rendering.
            // This is done with UniqueId to include FolderId into calculations.
            if (initializedMailItemViewModel.UniqueId != updatedMail.UniqueId) return;

            // Mail operation might change the mail item like mark read/unread or change flag.
            // So we need to update the mail item view model when this happens.
            // Also command bar items must be re-initialized since the items loaded based on the mail item.

            await ExecuteUIThread(() => { InitializeCommandBarItems(); });
        }

        [RelayCommand]
        private async Task OpenAttachmentAsync(MailAttachmentViewModel attachmentViewModel)
        {
            try
            {
                var fileFolderPath = Path.Combine(initializedMimeMessageInformation.Path, attachmentViewModel.FileName);
                var directoryInfo = new DirectoryInfo(initializedMimeMessageInformation.Path);

                var fileExists = File.Exists(fileFolderPath);

                if (!fileExists)
                    await SaveAttachmentInternalAsync(attachmentViewModel, initializedMimeMessageInformation.Path);

                await LaunchFileInternalAsync(fileFolderPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, WinoErrors.OpenAttachment);
                Crashes.TrackError(ex);

                DialogService.InfoBarMessage(Translator.Info_AttachmentOpenFailedTitle, Translator.Info_AttachmentOpenFailedMessage, InfoBarMessageType.Error);
            }
        }

        [RelayCommand]
        private async Task SaveAttachmentAsync(MailAttachmentViewModel attachmentViewModel)
        {
            if (attachmentViewModel == null)
                return;

            try
            {
                attachmentViewModel.IsBusy = true;

                var pickedPath = await DialogService.PickWindowsFolderAsync();

                if (string.IsNullOrEmpty(pickedPath)) return;

                await SaveAttachmentInternalAsync(attachmentViewModel, pickedPath);

                DialogService.InfoBarMessage(Translator.Info_AttachmentSaveSuccessTitle, Translator.Info_AttachmentSaveSuccessMessage, InfoBarMessageType.Success);
            }
            catch (Exception ex)
            {
                Log.Error(ex, WinoErrors.SaveAttachment);
                Crashes.TrackError(ex);

                DialogService.InfoBarMessage(Translator.Info_AttachmentSaveFailedTitle, Translator.Info_AttachmentSaveFailedMessage, InfoBarMessageType.Error);
            }
            finally
            {
                attachmentViewModel.IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task SaveAllAttachmentsAsync()
        {
            var pickedPath = await DialogService.PickWindowsFolderAsync();

            if (string.IsNullOrEmpty(pickedPath)) return;

            try
            {

                foreach (var attachmentViewModel in Attachments)
                {
                    await SaveAttachmentInternalAsync(attachmentViewModel, pickedPath);
                }

                DialogService.InfoBarMessage(Translator.Info_AttachmentSaveSuccessTitle, Translator.Info_AttachmentSaveSuccessMessage, InfoBarMessageType.Success);
            }
            catch (Exception ex)
            {
                Log.Error(ex, WinoErrors.SaveAttachment);
                Crashes.TrackError(ex);

                DialogService.InfoBarMessage(Translator.Info_AttachmentSaveFailedTitle, Translator.Info_AttachmentSaveFailedMessage, InfoBarMessageType.Error);
            }
        }

        // Returns created file path.
        private async Task<string> SaveAttachmentInternalAsync(MailAttachmentViewModel attachmentViewModel, string saveFolderPath)
        {
            var fullFilePath = Path.Combine(saveFolderPath, attachmentViewModel.FileName);
            var stream = await _fileService.GetFileStreamAsync(saveFolderPath, attachmentViewModel.FileName);

            using (stream)
            {
                await attachmentViewModel.MimeContent.DecodeToAsync(stream);
            }

            return fullFilePath;
        }

        private async Task LaunchFileInternalAsync(string filePath)
        {
            try
            {
                await NativeAppService.LaunchFileAsync(filePath);
            }
            catch (Exception ex)
            {
                DialogService.InfoBarMessage(Translator.Info_FileLaunchFailedTitle, ex.Message, InfoBarMessageType.Error);
            }
        }

        void ITransferProgress.Report(long bytesTransferred, long totalSize)
            => _ = ExecuteUIThread(() => { CurrentDownloadPercentage = bytesTransferred * 100 / Math.Max(1, totalSize); });

        // For upload.
        void ITransferProgress.Report(long bytesTransferred) { }

        public async void Receive(NewMailItemRenderingRequestedEvent message)
        {
            try
            {
                await RenderAsync(message.MailItemViewModel, renderCancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Information("Canceled mail rendering.");
            }
            catch (Exception ex)
            {
                DialogService.InfoBarMessage(Translator.Info_MailRenderingFailedTitle, string.Format(Translator.Info_MailRenderingFailedMessage, ex.Message), InfoBarMessageType.Error);

                Crashes.TrackError(ex);
                Log.Error(ex, "Render Failed");
            }
        }
    }
}
