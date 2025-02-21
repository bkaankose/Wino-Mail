using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI.Controls;
using EmailValidation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using MimeKit;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI.Core.Preview;
using Windows.UI.ViewManagement.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Reader;
using Wino.Core.UWP.Extensions;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Client.Shell;
using Wino.Views.Abstract;

namespace Wino.Views;

public sealed partial class ComposePage : ComposePageAbstract,
    IRecipient<CreateNewComposeMailRequested>,
    IRecipient<ApplicationThemeChanged>
{
    public WebView2 GetWebView() => Chromium;

    private readonly TaskCompletionSource<bool> _domLoadedTask = new TaskCompletionSource<bool>();

    private readonly List<IDisposable> _disposables = new List<IDisposable>();
    private readonly SystemNavigationManagerPreview _navManagerPreview = SystemNavigationManagerPreview.GetForCurrentView();

    public ComposePage()
    {
        InitializeComponent();
        _navManagerPreview.CloseRequested += OnClose;

        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00FFFFFF");
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--enable-features=OverlayScrollbar,msOverlayScrollbarWinStyle,msOverlayScrollbarWinStyleAnimation");
    }

    private async void GlobalFocusManagerGotFocus(object sender, FocusManagerGotFocusEventArgs e)
    {
        // In order to delegate cursor to the inner editor for WebView2.
        // When the control got focus, we invoke script to focus the editor.
        // This is not done on the WebView2 handlers, because somehow it is
        // repeatedly focusing itself, even though when it has the focus already.

        if (e.NewFocusedElement == Chromium)
        {
            await WebViewEditor.FocusEditorAsync(false);
        }
    }

    private IDisposable GetSuggestionBoxDisposable(TokenizingTextBox box)
    {
        return Observable.FromEventPattern<TypedEventHandler<AutoSuggestBox, AutoSuggestBoxTextChangedEventArgs>, AutoSuggestBoxTextChangedEventArgs>(
            x => box.TextChanged += x,
            x => box.TextChanged -= x)
                .Throttle(TimeSpan.FromMilliseconds(120))
                .ObserveOn(SynchronizationContext.Current)
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
                                    var addresses = x.Result;

                                    senderBox.ItemsSource = addresses;
                                });
                            });
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

    private bool IsValidImageFile(StorageFile file)
    {
        string[] allowedTypes = new string[] { ".jpg", ".jpeg", ".png" };
        var fileType = file.FileType.ToLower();

        return allowedTypes.Contains(fileType);
    }

    private async void AlignmentChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedItem = AlignmentListView.SelectedItem as ComboBoxItem;
        var alignment = selectedItem.Tag.ToString();

        switch (alignment)
        {
            case "left":
                await InvokeScriptSafeAsync("editor.execCommand('justifyleft')");
                break;
            case "center":
                await InvokeScriptSafeAsync("editor.execCommand('justifycenter')");
                break;
            case "right":
                await InvokeScriptSafeAsync("editor.execCommand('justifyright')");
                break;
            case "justify":
                await InvokeScriptSafeAsync("editor.execCommand('justifyfull')");
                break;
        }
    }

    private async Task<string> InvokeScriptSafeAsync(string function)
    {
        if (Chromium == null) return string.Empty;

        try
        {
            return await Chromium.ExecuteScriptAsync(function);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }

        return string.Empty;
    }

    /// <summary>
    /// Places the cursor in the composer.
    /// </summary>
    /// <param name="focusControlAsWell">Whether control itself should be focused as well or not.</param>
    private async Task FocusEditorAsync(bool focusControlAsWell)
    {
        await InvokeScriptSafeAsync("editor.selection.setCursorIn(editor.editor.firstChild, true)");

        if (focusControlAsWell)
        {
            Chromium.Focus(FocusState.Keyboard);
            Chromium.Focus(FocusState.Programmatic);
        }
    }

    private async void EmojiButtonClicked(object sender, RoutedEventArgs e)
    {
        CoreInputView.GetForCurrentView().TryShow(CoreInputViewKind.Emoji);

        await FocusEditorAsync(focusControlAsWell: true);
    }

    private void DisposeDisposables()
    {
        if (_disposables.Count != 0)
            _disposables.ForEach(a => a.Dispose());
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        FocusManager.GotFocus += GlobalFocusManagerGotFocus;

        var anim = ConnectedAnimationService.GetForCurrentView().GetAnimation("WebViewConnectedAnimation");
        anim?.TryStart(Chromium);

        DisposeDisposables();

        _disposables.Add(GetSuggestionBoxDisposable(ToBox));
        _disposables.Add(GetSuggestionBoxDisposable(CCBox));
        _disposables.Add(GetSuggestionBoxDisposable(BccBox));
        _disposables.Add(WebViewEditor);

        Chromium.CoreWebView2Initialized -= ChromiumInitialized;
        Chromium.CoreWebView2Initialized += ChromiumInitialized;

        await Chromium.EnsureCoreWebView2Async();

        ViewModel.GetHTMLBodyFunction = WebViewEditor.GetHtmlBodyAsync;
    }

    private async void ChromiumInitialized(Microsoft.UI.Xaml.Controls.WebView2 sender, Microsoft.UI.Xaml.Controls.CoreWebView2InitializedEventArgs args)
    {
        var editorBundlePath = (await ViewModel.NativeAppService.GetEditorBundlePathAsync()).Replace("editor.html", string.Empty);

        Chromium.CoreWebView2.SetVirtualHostNameToFolderMapping("app.editor", editorBundlePath, CoreWebView2HostResourceAccessKind.Allow);
        Chromium.Source = new Uri("https://app.editor/editor.html");

        Chromium.CoreWebView2.DOMContentLoaded -= DOMLoaded;
        Chromium.CoreWebView2.DOMContentLoaded += DOMLoaded;

        Chromium.CoreWebView2.WebMessageReceived -= ScriptMessageReceived;
        Chromium.CoreWebView2.WebMessageReceived += ScriptMessageReceived;
    }

    private void ScriptMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        var change = JsonSerializer.Deserialize(args.WebMessageAsJson, DomainModelsJsonContext.Default.WebViewMessage);

        if (change.Type == "alignment")
        {
            var parsedValue = change.Value switch
            {
                "jodit-icon_left" => 0,
                "jodit-icon_center" => 1,
                "jodit-icon_right" => 2,
                "jodit-icon_justify" => 3,
                _ => 0
            };
            AlignmentListView.SelectionChanged -= AlignmentChanged;
            AlignmentListView.SelectedIndex = parsedValue;
            AlignmentListView.SelectionChanged += AlignmentChanged;
        }
    }

    private void DOMLoaded(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args) => _domLoadedTask.TrySetResult(true);

    async void IRecipient<CreateNewComposeMailRequested>.Receive(CreateNewComposeMailRequested message)
    {
        await WebViewEditor.RenderHtmlAsync(message.RenderModel.RenderHtml);
    }

    private void ShowCCBCCClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.IsCCBCCVisible = true;
    }

    private async void TokenItemAdding(TokenizingTextBox sender, TokenItemAddingEventArgs args)
    {
        // Check is valid email.

        if (!EmailValidator.Validate(args.TokenText))
        {
            args.Cancel = true;
            ViewModel.NotifyInvalidEmail(args.TokenText);

            return;
        }

        var deferral = args.GetDeferral();

        AccountContact addedItem = null;

        var boxTag = sender.Tag?.ToString();

        if (boxTag == "ToBox")
            addedItem = await ViewModel.GetAddressInformationAsync(args.TokenText, ViewModel.ToItems);
        else if (boxTag == "CCBox")
            addedItem = await ViewModel.GetAddressInformationAsync(args.TokenText, ViewModel.CCItems);
        else if (boxTag == "BCCBox")
            addedItem = await ViewModel.GetAddressInformationAsync(args.TokenText, ViewModel.BCCItems);

        if (addedItem == null)
        {
            args.Cancel = true;
            ViewModel.NotifyAddressExists();
        }
        else
        {
            args.Item = addedItem;
        }

        deferral.Complete();
    }

    void IRecipient<ApplicationThemeChanged>.Receive(ApplicationThemeChanged message)
    {
        WebViewEditor.IsEditorDarkMode = message.IsUnderlyingThemeDark;
    }

    private void ImportanceClicked(object sender, RoutedEventArgs e)
    {
        ImportanceFlyout.Hide();
        ImportanceSplitButton.IsChecked = true;

        if (sender is Button senderButton)
        {
            ViewModel.SelectedMessageImportance = (MessageImportance)senderButton.Tag;
            ((ImportanceSplitButton.Content as Viewbox).Child as SymbolIcon).Symbol = (senderButton.Content as SymbolIcon).Symbol;
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
                var boxTag = tokenizingTextBox.Tag?.ToString();

                AccountContact addedItem = null;
                ObservableCollection<AccountContact> addressCollection = null;

                if (boxTag == "ToBox")
                    addressCollection = ViewModel.ToItems;
                else if (boxTag == "CCBox")
                    addressCollection = ViewModel.CCItems;
                else if (boxTag == "BCCBox")
                    addressCollection = ViewModel.BCCItems;

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

    // Hack: Tokenizing text box losing focus somehow on page Loaded and shifting focus to this element.
    // For once we'll switch back to it once CCBBCGotFocus element got focus.

    private bool isInitialFocusHandled = false;

    private void ComposerLoaded(object sender, RoutedEventArgs e)
    {
        ToBox.Focus(FocusState.Programmatic);
    }

    private void CCBBCGotFocus(object sender, RoutedEventArgs e)
    {
        if (!isInitialFocusHandled)
        {
            isInitialFocusHandled = true;
            ToBox.Focus(FocusState.Programmatic);
        }
    }

    protected override async void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);

        FocusManager.GotFocus -= GlobalFocusManagerGotFocus;
        _navManagerPreview.CloseRequested -= OnClose;
        await ViewModel.UpdateMimeChangesAsync();

        DisposeDisposables();
    }
    private async void OnClose(object sender, SystemNavigationCloseRequestedPreviewEventArgs e)
    {
        var deferral = e.GetDeferral();

        try
        {
            await ViewModel.UpdateMimeChangesAsync();
        }
        finally { deferral.Complete(); }
    }
}
