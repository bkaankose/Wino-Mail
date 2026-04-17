using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI.Controls;
using EmailValidation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using MimeKit;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI.Core.Preview;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Reader;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Controls;
using Wino.Mail.WinUI.Extensions;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Messaging.Client.Shell;
using Wino.Views.Abstract;

namespace Wino.Views.Mail;

public sealed partial class ComposePage : ComposePageAbstract,
    IAiHtmlActionHost,
    IPopoutClient,
    IRecipient<ApplicationThemeChanged>
{
    private const int InitialFocusRetryCount = 3;

    private bool _isPoppedOut;
    private bool _isInitialFocusHandled;
    private readonly Dictionary<TokenizingTextBox, List<AccountContact>> _recipientSuggestions = [];

    public bool SupportsPopOut => !_isPoppedOut;
    public event EventHandler<PopOutRequestedEventArgs>? PopOutRequested;
    public event EventHandler<PopoutHostActionRequestedEventArgs>? HostActionRequested;

    public WebView2 GetWebView() => WebViewEditor.GetUnderlyingWebView();

    public Visibility GetAiActionsToggleVisibility(bool isHidden) => isHidden ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GetPopOutButtonVisibility() => SupportsPopOut ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GetAiActionsPanelVisibility(bool? isChecked, bool isHidden)
        => !isHidden && isChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private readonly List<IDisposable> _disposables = [];

    public ComposePage()
    {
        InitializeComponent();
        ViewModel.CloseRequested += ViewModel_CloseRequested;
    }

    public HostedPopoutDescriptor GetPopoutDescriptor()
    {
        var title = string.IsNullOrWhiteSpace(ViewModel.Subject) ? Translator.Draft : ViewModel.Subject;
        var draftId = ViewModel.CurrentMailDraftItem?.MailCopy?.UniqueId.ToString("N") ?? title;

        return new HostedPopoutDescriptor(
            $"compose-{draftId}",
            title,
            1180,
            860,
            760,
            600,
            nameof(ComposePage));
    }

    public void OnPopoutStateChanged(bool isPoppedOut)
    {
        _isPoppedOut = isPoppedOut;
        Bindings.Update();
    }

    public WinoIconGlyph GetEditorThemeIcon(bool isDarkMode) => isDarkMode ? WinoIconGlyph.LightEditor : WinoIconGlyph.DarkEditor;

    public string GetEditorThemeToolTip(bool isDarkMode) => isDarkMode ? Translator.Composer_LightTheme : Translator.Composer_DarkTheme;

    private void ToggleEditorThemeClicked(object sender, RoutedEventArgs e)
    {
        WebViewEditor.ToggleEditorTheme();
    }

    private async void EmailTemplateSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox || comboBox.SelectedItem is not EmailTemplate template)
            return;

        await WebViewEditor.RenderHtmlAsync(template.HtmlContent);
        comboBox.SelectedItem = null;
    }

    private async void GlobalFocusManagerGotFocus(object? sender, FocusManagerGotFocusEventArgs e)
    {
        // In order to delegate cursor to the inner editor for WebView2.
        // When the control got focus, we invoke script to focus the editor.
        // This is not done on the WebView2 handlers, because somehow it is
        // repeatedly focusing itself, even though when it has the focus already.

        if (e.NewFocusedElement == WebViewEditor)
        {
            await WebViewEditor.FocusEditorAsync(true);
        }
    }

    private IDisposable GetSuggestionBoxDisposable(TokenizingTextBox box)
    {
        return Observable.FromEventPattern<TypedEventHandler<AutoSuggestBox, AutoSuggestBoxTextChangedEventArgs>, AutoSuggestBoxTextChangedEventArgs>(
            x => box.TextChanged += x,
            x => box.TextChanged -= x)
                .Throttle(TimeSpan.FromMilliseconds(120))
                .ObserveOn(SynchronizationContext.Current!)
                .Subscribe(t =>
                {
                    if (t.EventArgs.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                    {
                        if (t.Sender is AutoSuggestBox senderBox && senderBox.Text.Length >= 2)
                        {
                            _ = ViewModel.ContactService.GetAddressInformationAsync(senderBox.Text).ContinueWith(x =>
                            {
                                _ = ViewModel.ExecuteUIThread(() =>
                                {
                                    var addresses = x.Result ?? [];

                                    _recipientSuggestions[box] = addresses;
                                    senderBox.ItemsSource = addresses;
                                });
                            });
                        }
                        else
                        {
                            _recipientSuggestions[box] = [];
                        }
                    }
                });
    }

    private void OnComposeGridDragOver(object sender, DragEventArgs e)
    {
        ViewModel.IsDraggingOverComposerGrid = true;
    }

    private void OnComposeGridDragLeave(object sender, DragEventArgs e)
    {
        ViewModel.IsDraggingOverComposerGrid = false;
    }

    private void OnFileDropGridDragOver(object sender, DragEventArgs e)
    {
        ViewModel.IsDraggingOverFilesDropZone = true;

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = Translator.ComposerAttachmentsDragDropAttach_Message;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        e.DragUIOverride.IsContentVisible = true;
    }

    private void OnFileDropGridDragLeave(object sender, DragEventArgs e)
    {
        ViewModel.IsDraggingOverFilesDropZone = false;
    }

    private async void OnFileDropGridFileDropped(object sender, DragEventArgs e)
    {
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var storageItems = await e.DataView.GetStorageItemsAsync();
                var files = storageItems.OfType<StorageFile>();

                await AttachFiles(files);
            }
        }
        // State should be reset even when an exception occurs, otherwise the UI will be stuck in a dragging state.
        finally
        {
            ViewModel.IsDraggingOverComposerGrid = false;
            ViewModel.IsDraggingOverFilesDropZone = false;
        }
    }

    private void OnImageDropGridDragEnter(object sender, DragEventArgs e)
    {
        bool isValid = false;
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            // We can't use async/await here because DragUIOverride becomes inaccessible.
            // https://github.com/microsoft/microsoft-ui-xaml/issues/9296
            var files = e.DataView.GetStorageItemsAsync().GetAwaiter().GetResult().OfType<StorageFile>();

            foreach (var file in files)
            {
                if (IsValidImageFile(file))
                {
                    isValid = true;
                }
            }
        }

        e.AcceptedOperation = isValid ? DataPackageOperation.Copy : DataPackageOperation.None;

        if (isValid)
        {
            ViewModel.IsDraggingOverImagesDropZone = true;
            e.DragUIOverride.Caption = Translator.ComposerAttachmentsDragDropAttach_Message;
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
            e.DragUIOverride.IsContentVisible = true;
        }
    }

    private void OnImageDropGridDragLeave(object sender, DragEventArgs e)
    {
        ViewModel.IsDraggingOverImagesDropZone = false;
    }

    private async void OnImageDropGridImageDropped(object sender, DragEventArgs e)
    {
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var storageItems = await e.DataView.GetStorageItemsAsync();
                var files = storageItems.OfType<StorageFile>();

                var imagesInformation = new List<ImageInfo>();

                foreach (var file in files)
                {
                    if (IsValidImageFile(file))
                    {
                        imagesInformation.Add(new ImageInfo
                        {
                            Data = await GetDataURL(file),
                            Name = file.Name
                        });
                    }
                }

                await WebViewEditor.InsertImagesAsync(imagesInformation);
            }
        }
        // State should be reset even when an exception occurs, otherwise the UI will be stuck in a dragging state.
        finally
        {
            ViewModel.IsDraggingOverComposerGrid = false;
            ViewModel.IsDraggingOverImagesDropZone = false;
        }

        static async Task<string> GetDataURL(StorageFile file)
        {
            return $"data:image/{file.FileType.Replace(".", "")};base64,{Convert.ToBase64String(await file.ToByteArrayAsync())}";
        }
    }

    private async Task AttachFiles(IEnumerable<StorageFile> files)
    {
        if (files?.Any() != true) return;

        // Convert files to MailAttachmentViewModel.
        foreach (var file in files)
        {
            var sharedFile = await file.ToSharedFileAsync();

            ViewModel.IncludedAttachments.Add(new MailAttachmentViewModel(sharedFile));
        }
    }

    private static bool IsValidImageFile(StorageFile file)
    {
        string[] allowedTypes = [".jpg", ".jpeg", ".png"];
        var fileType = file.FileType.ToLower();

        return allowedTypes.Contains(fileType);
    }

    private void DisposeDisposables()
    {
        if (_disposables.Count != 0)
            _disposables.ForEach(a => a.Dispose());
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // FocusManager.GotFocus += GlobalFocusManagerGotFocus;

        var webView = GetWebView();

        if (webView != null)
        {
            var anim = ConnectedAnimationService.GetForCurrentView().GetAnimation("WebViewConnectedAnimation");

            anim?.TryStart(webView);
        }

        _disposables.Add(GetSuggestionBoxDisposable(ToBox));
        _disposables.Add(GetSuggestionBoxDisposable(CCBox));
        _disposables.Add(GetSuggestionBoxDisposable(BccBox));
        _disposables.Add(WebViewEditor);

        ViewModel.GetHTMLBodyFunction = WebViewEditor.GetHtmlBodyAsync;
        ViewModel.RenderHtmlBodyAsyncFunc = RenderComposeHtmlAsync;
    }

    private void ShowCCBCCClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.IsCCBCCVisible = true;
    }

    private void PopOutButton_Click(object sender, RoutedEventArgs e)
    {
        PopOutRequested?.Invoke(this, PopOutRequestedEventArgs.Default);
    }

    private void ViewModel_CloseRequested(object? sender, EventArgs e)
    {
        HostActionRequested?.Invoke(this, new PopoutHostActionRequestedEventArgs(PopoutHostActionKind.CloseHostedInstance));
    }

    private async void ComposeAiActionsToggleButton_Checked(object sender, RoutedEventArgs e)
    {
        await ComposeAiActionsPanel.RefreshAvailabilityAsync();
    }

    private async void TokenItemAdding(TokenizingTextBox sender, TokenItemAddingEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var suggestedContact = GetFirstSuggestedContact(sender);
            var tokenText = suggestedContact?.Address ?? args.TokenText;
            var addressCollection = sender.Tag?.ToString() switch
            {
                "ToBox" => ViewModel.ToItems,
                "CCBox" => ViewModel.CCItems,
                "BCCBox" => ViewModel.BCCItems,
                _ => null
            };

            if (suggestedContact == null && !EmailValidator.Validate(tokenText))
            {
                args.Cancel = true;
                ViewModel.NotifyInvalidEmail(args.TokenText);
                return;
            }

            AccountContact? addedItem = null;

            if (suggestedContact != null)
            {
                addedItem = addressCollection?.Any(a => string.Equals(a.Address, suggestedContact.Address, StringComparison.OrdinalIgnoreCase)) == true
                    ? null
                    : suggestedContact;
            }
            else
            {
                addedItem = sender.Tag?.ToString() switch
                {
                    "ToBox" => await ViewModel.GetAddressInformationAsync(tokenText, ViewModel.ToItems),
                    "CCBox" => await ViewModel.GetAddressInformationAsync(tokenText, ViewModel.CCItems),
                    "BCCBox" => await ViewModel.GetAddressInformationAsync(tokenText, ViewModel.BCCItems),
                    _ => null
                };
            }

            if (addedItem == null)
            {
                args.Cancel = true;
                ViewModel.NotifyAddressExists();
            }
            else
            {
                args.Item = addedItem;
            }
        }
        finally
        {
            _recipientSuggestions[sender] = [];
            deferral.Complete();
        }
    }

    void IRecipient<ApplicationThemeChanged>.Receive(ApplicationThemeChanged message)
    {
        WebViewEditor.IsEditorDarkMode = message.IsUnderlyingThemeDark;
    }

    public async Task RefreshDraftAsync(MailItemViewModel draftMailItemViewModel)
    {
        if (draftMailItemViewModel == null || !draftMailItemViewModel.IsDraft) return;

        // Reset the initial focus flag for the newly loaded draft.
        _isInitialFocusHandled = false;
        await ViewModel.RefreshDraftAsync(draftMailItemViewModel);
        await ApplyInitialFocusAsync();
    }

    private void ImportanceClicked(object sender, RoutedEventArgs e)
    {
        ImportanceFlyout.Hide();
        ImportanceSplitButton.IsChecked = true;

        if (sender is Button senderButton && senderButton.Tag is MessageImportance importance)
        {
            ViewModel.SelectedMessageImportance = importance;
            if (ImportanceSplitButton.Content is Viewbox viewbox &&
                viewbox.Child is SymbolIcon symbolIcon &&
                senderButton.Content is SymbolIcon contentIcon)
            {
                symbolIcon.Symbol = contentIcon.Symbol;
            }
        }
    }

    private async void AddressBoxLostFocus(object sender, RoutedEventArgs e)
    {
        // Automatically add current text as item if it is valid mail address.

        if (sender is TokenizingTextBox tokenizingTextBox)
        {
            if (tokenizingTextBox.Items.LastOrDefault() is not ITokenStringContainer info) return;

            var currentText = info.Text;

            if (!string.IsNullOrEmpty(currentText) && EmailValidator.Validate(currentText))
            {
                var addressCollection = tokenizingTextBox.Tag?.ToString() switch
                {
                    "ToBox" => ViewModel.ToItems,
                    "CCBox" => ViewModel.CCItems,
                    "BCCBox" => ViewModel.BCCItems,
                    _ => null
                };

                AccountContact? addedItem = null;

                if (addressCollection != null)
                    addedItem = await ViewModel.GetAddressInformationAsync(currentText, addressCollection);

                // Item has already been added.
                if (addedItem == null)
                {
                    tokenizingTextBox.Text = string.Empty;
                }
                else if (addressCollection != null)
                {
                    addressCollection.Add(addedItem);
                    tokenizingTextBox.Text = string.Empty;
                }
            }
        }
    }

    private AccountContact? GetFirstSuggestedContact(TokenizingTextBox box)
        => _recipientSuggestions.TryGetValue(box, out var suggestions)
            ? suggestions.FirstOrDefault()
            : null;

    private void ComposerLoaded(object sender, RoutedEventArgs e)
    {
        if (ShouldFocusRecipients())
        {
            ToBox.Focus(FocusState.Programmatic);
        }
    }

    private void CCBBCGotFocus(object sender, RoutedEventArgs e)
    {
        if (ShouldFocusRecipients() && !_isInitialFocusHandled)
        {
            _isInitialFocusHandled = true;
            ToBox.Focus(FocusState.Programmatic);
        }
    }

    protected override async void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);

        FocusManager.GotFocus -= GlobalFocusManagerGotFocus;
        ComposeAiActionsPanel.CancelPendingOperation();
        await ViewModel.UpdateMimeChangesAsync();
        ViewModel.RenderHtmlBodyAsyncFunc = null;

        DisposeDisposables();
    }

    public async Task<string?> GetCurrentHtmlAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var html = await WebViewEditor.GetHtmlBodyAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return html;
    }

    public async Task ApplyHtmlResultAsync(string html, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await WebViewEditor.RenderHtmlAsync(html);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public Task<string?> TryGetCachedTranslationHtmlAsync(string languageCode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }

    public Task SaveCachedTranslationHtmlAsync(string languageCode, string html, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<string?> TryGetCachedSummaryTextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }

    public Task SaveCachedSummaryTextAsync(string summary, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public string GetSuggestedSummaryFileName() => "email-summary.txt";

    private void OpenAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.CommandParameter is MailAttachmentViewModel attachment)
        {
            ViewModel.OpenAttachmentCommand.Execute(attachment);
        }
    }

    private void SaveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.CommandParameter is MailAttachmentViewModel attachment)
        {
            ViewModel.SaveAttachmentCommand.Execute(attachment);
        }
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is MailAttachmentViewModel attachment)
        {
            ViewModel.RemoveAttachmentCommand.Execute(attachment);
        }
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        WeakReferenceMessenger.Default.Register<ApplicationThemeChanged>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        WeakReferenceMessenger.Default.Unregister<ApplicationThemeChanged>(this);
    }

    // TODO: Save mime on closing the app.
    private async void OnClose(object sender, SystemNavigationCloseRequestedPreviewEventArgs e)
    {
        var deferral = e.GetDeferral();

        try
        {
            await ViewModel.UpdateMimeChangesAsync();
        }
        finally { deferral.Complete(); }
    }

    private bool ShouldFocusRecipients()
        => !ShouldFocusEditor();

    private bool ShouldFocusEditor()
    {
        var inReplyTo = ViewModel.CurrentMimeMessage?.InReplyTo;

        if (string.IsNullOrWhiteSpace(inReplyTo))
        {
            inReplyTo = ViewModel.CurrentMailDraftItem?.MailCopy?.InReplyTo;
        }

        if (string.IsNullOrWhiteSpace(inReplyTo) && ViewModel.CurrentMimeMessage?.Headers.Contains(HeaderId.InReplyTo) == true)
        {
            inReplyTo = ViewModel.CurrentMimeMessage.Headers[HeaderId.InReplyTo];
        }

        return !string.IsNullOrWhiteSpace(inReplyTo);
    }

    private async Task ApplyInitialFocusAsync()
    {
        if (_isInitialFocusHandled)
        {
            return;
        }

        _isInitialFocusHandled = true;

        for (var attempt = 0; attempt < InitialFocusRetryCount; attempt++)
        {
            if (ShouldFocusEditor())
            {
                await WebViewEditor.FocusEditorAsync(true);

                if (FocusManager.GetFocusedElement() is WebView2)
                {
                    return;
                }
            }
            else
            {
                ToBox.Focus(FocusState.Programmatic);

                if (FocusManager.GetFocusedElement() == ToBox)
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
    }

    private async Task RenderComposeHtmlAsync(string html)
    {
        await WebViewEditor.RenderHtmlAsync(html);
        await ApplyInitialFocusAsync();
    }
}
