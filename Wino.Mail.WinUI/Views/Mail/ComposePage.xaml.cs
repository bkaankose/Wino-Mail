using System;
using System.Collections.Generic;
using System.Linq;
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
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Core.Preview;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Reader;
using Wino.Editor;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Controls;
using Wino.Mail.WinUI.Extensions;
using Wino.Mail.WinUI.Helpers;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Messaging.Client.Mails;
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
    private bool _shouldApplyInitialFocus;
    private bool _isNavigatingFrom;
    private CancellationTokenSource? _editorLifecycleCancellationSource;
    private readonly Dictionary<TokenizingTextBox, List<AccountContact>> _recipientSuggestions = [];

    public bool SupportsPopOut => !_isPoppedOut;
    public bool HasEditorKeyboardFocus => WebViewEditor.FocusState != FocusState.Unfocused;
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
        WebViewEditor.IsEditorDarkMode = WinoApplication.Current.UnderlyingThemeService.IsUnderlyingThemeDark();
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

    private async void SubjectTextBoxPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Tab || IsShiftKeyDown())
        {
            return;
        }

        e.Handled = true;
        await WebViewEditor.FocusEditorAsync(true);
    }

    private static bool IsShiftKeyDown()
        => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);

    private IDisposable GetSuggestionBoxDisposable(TokenizingTextBox box)
    {
        return new SuggestionBoxTextDebouncer(box, TimeSpan.FromMilliseconds(120), (senderBox, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            {
                return;
            }

            if (senderBox.Text.Length >= 2)
            {
                _ = ViewModel.ContactService.GetAddressInformationAsync(senderBox.Text).ContinueWith(task =>
                {
                    _ = ViewModel.ExecuteUIThread(() =>
                    {
                        var addresses = task.Result ?? [];

                        _recipientSuggestions[box] = addresses;
                        senderBox.ItemsSource = addresses;
                    });
                });
            }
            else
            {
                _recipientSuggestions[box] = [];
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

                await WebViewEditor.InsertImagesAsync(
                    imagesInformation.Select(image => new EditorImageInfo(image.Data, image.Name)));
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
        if (_disposables.Count == 0)
            return;

        _disposables.ForEach(a => a.Dispose());
        _disposables.Clear();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _isNavigatingFrom = false;
        _editorLifecycleCancellationSource = new CancellationTokenSource();
        _shouldApplyInitialFocus = ConsumeInitialFocusRequest(e.Parameter as MailItemViewModel);
        _isInitialFocusHandled = false;

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

        ViewModel.GetHTMLBodyFunction = GetEditorHtmlBodyAsync;
        var editorLifecycleToken = _editorLifecycleCancellationSource.Token;
        ViewModel.RenderHtmlBodyAsyncFunc = html => RenderComposeHtmlAsync(html, editorLifecycleToken);
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
        if (_isPoppedOut)
        {
            HostActionRequested?.Invoke(this, new PopoutHostActionRequestedEventArgs(PopoutHostActionKind.CloseHostedInstance));
            return;
        }

        WeakReferenceMessenger.Default.Send(new DisposeRenderingFrameRequested());
    }

    private void CopyContactAddress_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { CommandParameter: string address } || string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(address);
        Clipboard.SetContent(package);
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

        _shouldApplyInitialFocus = ConsumeInitialFocusRequest(draftMailItemViewModel);
        _isInitialFocusHandled = false;
        await ViewModel.RefreshDraftAsync(draftMailItemViewModel);
        await ApplyInitialFocusAsync();
    }

    private void ImportanceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: MessageImportance importance })
        {
            return;
        }

        ViewModel.SelectedMessageImportance = importance;

        // Normal is the absence of an importance header, not a third value to write.
        ViewModel.IsImportanceSelected = importance != MessageImportance.Normal;

        // Keep the toolbar icon in sync with the choice so the tab does not have to be opened to read it.
        ImportanceButtonIcon.Symbol = importance switch
        {
            MessageImportance.Low => Symbol.Priority,
            _ => Symbol.Important
        };
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

        _isNavigatingFrom = true;
        _editorLifecycleCancellationSource?.Cancel();
        ViewModel.RenderHtmlBodyAsyncFunc = null;
        ComposeAiActionsPanel.CancelPendingOperation();

        try
        {
            await ViewModel.UpdateMimeChangesAsync();
        }
        catch (ObjectDisposedException) when (_isNavigatingFrom)
        {
            // A second navigation can finish tearing down the editor while this
            // navigation is still saving the draft. The editor content was already
            // captured by the winning teardown, so disposal is expected here.
        }
        finally
        {
            ViewModel.GetHTMLBodyFunction = null;
            DisposeDisposables();
            _editorLifecycleCancellationSource?.Dispose();
            _editorLifecycleCancellationSource = null;
        }
    }

    private async Task<string> GetEditorHtmlBodyAsync()
    {
        try
        {
            return await WebViewEditor.GetHtmlBodyAsync() ?? string.Empty;
        }
        catch (ObjectDisposedException) when (_isNavigatingFrom)
        {
            return ViewModel.CurrentMimeMessage?.HtmlBody ?? string.Empty;
        }
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
        => _shouldApplyInitialFocus && !ShouldFocusEditor();

    private static bool ConsumeInitialFocusRequest(MailItemViewModel? draft)
    {
        if (draft is not { ShouldFocusComposerOnOpen: true })
        {
            return false;
        }

        draft.ShouldFocusComposerOnOpen = false;
        return true;
    }

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
        if (_isInitialFocusHandled || !_shouldApplyInitialFocus)
        {
            _isInitialFocusHandled = true;
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

                if (ReferenceEquals(FocusManager.GetFocusedElement(), ToBox))
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
    }

    private async Task RenderComposeHtmlAsync(string html, CancellationToken editorLifecycleToken)
    {
        if (editorLifecycleToken.IsCancellationRequested)
            return;

        try
        {
            await WebViewEditor.SetDefaultTypographyAsync(
                ViewModel.PreferencesService.ComposerFont,
                ViewModel.PreferencesService.ComposerFontSize);

            if (editorLifecycleToken.IsCancellationRequested)
                return;

            await WebViewEditor.RenderHtmlAsync(html);

            if (!editorLifecycleToken.IsCancellationRequested)
                await ApplyInitialFocusAsync();
        }
        catch (ObjectDisposedException) when (editorLifecycleToken.IsCancellationRequested || _isNavigatingFrom)
        {
            // Draft deletion can navigate away while initialization/rendering is
            // still in flight. Disposal is the expected completion of that work.
        }
    }
}
