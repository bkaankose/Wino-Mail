using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI.Controls;
using EmailValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Toolkit.Uwp.Helpers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using MimeKit;
using Newtonsoft.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.ViewManagement.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Messages.Mails;
using Wino.Core.Messages.Shell;
using Wino.Extensions;
using Wino.Mail.ViewModels.Data;
using Wino.Views.Abstract;

namespace Wino.Views
{
    public sealed partial class ComposePage : ComposePageAbstract,
        IRecipient<NavigationPaneModeChanged>,
        IRecipient<CreateNewComposeMailRequested>,
        IRecipient<ApplicationThemeChanged>
    {
        public bool IsComposerDarkMode
        {
            get { return (bool)GetValue(IsComposerDarkModeProperty); }
            set { SetValue(IsComposerDarkModeProperty, value); }
        }

        public static readonly DependencyProperty IsComposerDarkModeProperty = DependencyProperty.Register(nameof(IsComposerDarkMode), typeof(bool), typeof(ComposePage), new PropertyMetadata(false, OnIsComposerDarkModeChanged));
        public WebView2 GetWebView() => Chromium;

        private TaskCompletionSource<bool> DOMLoadedTask = new TaskCompletionSource<bool>();

        private List<IDisposable> Disposables = new List<IDisposable>();

        public ComposePage()
        {
            InitializeComponent();

            Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00FFFFFF");
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--enable-features=OverlayScrollbar,msOverlayScrollbarWinStyle,msOverlayScrollbarWinStyleAnimation");
        }

        private static async void OnIsComposerDarkModeChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is ComposePage page)
            {
                await page.UpdateEditorThemeAsync();
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
                            if (t.Sender is AutoSuggestBox senderBox && senderBox.Text.Length >= 3)
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

        private async void AddFilesClicked(object sender, RoutedEventArgs e)
        {
            // TODO: Pick files
            var picker = new FileOpenPicker()
            {
                SuggestedStartLocation = PickerLocationId.Desktop
            };

            picker.FileTypeFilter.Add("*");
            var files = await picker.PickMultipleFilesAsync();

            await AttachFiles(files);
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
                    if (ValidateImageFile(file))
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

                    var imageDataURLs = new List<string>();

                    foreach (var file in files)
                    {
                        if (ValidateImageFile(file))
                            imageDataURLs.Add(await GetDataURL(file));
                    }

                    await InvokeScriptSafeAsync($"insertImages({JsonConvert.SerializeObject(imageDataURLs)});");
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
                return $"data:image/{file.FileType.Replace(".", "")};base64,{Convert.ToBase64String(await file.ReadBytesAsync())}";
            }
        }

        private async Task AttachFiles(IEnumerable<StorageFile> files)
        {
            if (files?.Any() != true) return;

            // Convert files to MailAttachmentViewModel.
            foreach (var file in files)
            {
                if (!ViewModel.IncludedAttachments.Any(a => a.FileName == file.Path))
                {
                    var attachmentViewModel = await file.ToAttachmentViewModelAsync();

                    ViewModel.IncludedAttachments.Add(attachmentViewModel);
                }
            }
        }

        private bool ValidateImageFile(StorageFile file)
        {
            string[] allowedTypes = new string[] { ".jpg", ".jpeg", ".png" };
            var fileType = file.FileType.ToLower();

            return allowedTypes.Contains(fileType);
        }

        private async void BoldButtonClicked(object sender, RoutedEventArgs e)
        {
            await InvokeScriptSafeAsync("editor.execCommand('bold')");
        }

        private async void ItalicButtonClicked(object sender, RoutedEventArgs e)
        {
            await InvokeScriptSafeAsync("editor.execCommand('italic')");
        }

        private async void UnderlineButtonClicked(object sender, RoutedEventArgs e)
        {
            await InvokeScriptSafeAsync("editor.execCommand('underline')");
        }

        private async void StrokeButtonClicked(object sender, RoutedEventArgs e)
        {
            await InvokeScriptSafeAsync("editor.execCommand('strikethrough')");
        }

        private async void BulletListButtonClicked(object sender, RoutedEventArgs e)
        {
            await InvokeScriptSafeAsync("editor.execCommand('insertunorderedlist')");
        }

        private async void OrderedListButtonClicked(object sender, RoutedEventArgs e)
        {
            await InvokeScriptSafeAsync("editor.execCommand('insertorderedlist')");
        }

        private async void IncreaseIndentClicked(object sender, RoutedEventArgs e)
        {
            await InvokeScriptSafeAsync("editor.execCommand('indent')");
        }

        private async void DecreaseIndentClicked(object sender, RoutedEventArgs e)
        {
            await InvokeScriptSafeAsync("editor.execCommand('outdent')");
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

        private async void WebViewToggleButtonClicked(object sender, RoutedEventArgs e)
        {
            var enable = WebviewToolBarButton.IsChecked == true ? "true" : "false";
            await InvokeScriptSafeAsync($"toggleToolbar('{enable}');");
        }

        public async Task<string> ExecuteScriptFunctionAsync(string functionName, params object[] parameters)
        {
            string script = functionName + "(";
            for (int i = 0; i < parameters.Length; i++)
            {
                script += JsonConvert.SerializeObject(parameters[i]);
                if (i < parameters.Length - 1)
                {
                    script += ", ";
                }
            }
            script += ");";

            return await Chromium.ExecuteScriptAsync(script);
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

        private async void AddImageClicked(object sender, RoutedEventArgs e)
        {
            await InvokeScriptSafeAsync("imageInput.click();");
        }

        private async Task FocusEditorAsync()
        {
            await InvokeScriptSafeAsync("editor.selection.focus();");

            Chromium.Focus(FocusState.Keyboard);
            Chromium.Focus(FocusState.Programmatic);
        }

        private async void EmojiButtonClicked(object sender, RoutedEventArgs e)
        {
            CoreInputView.GetForCurrentView().TryShow(CoreInputViewKind.Emoji);

            await FocusEditorAsync();
        }

        public async Task UpdateEditorThemeAsync()
        {
            await DOMLoadedTask.Task;

            if (IsComposerDarkMode)
            {
                Chromium.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
                await InvokeScriptSafeAsync("SetDarkEditor();");
            }
            else
            {
                Chromium.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;
                await InvokeScriptSafeAsync("SetLightEditor();");
            }
        }

        private async Task RenderInternalAsync(string htmlBody)
        {
            await DOMLoadedTask.Task;

            await UpdateEditorThemeAsync();
            await InitializeEditorAsync();

            if (string.IsNullOrEmpty(htmlBody))
            {
                await ExecuteScriptFunctionAsync("RenderHTML", " ");
            }
            else
            {
                await ExecuteScriptFunctionAsync("RenderHTML", htmlBody);
            }
        }

        private async Task<string> InitializeEditorAsync()
        {
            var fonts = ViewModel.FontService.GetFonts();
            var composerFont = ViewModel.PreferencesService.ComposerFont;
            int composerFontSize = ViewModel.PreferencesService.ComposerFontSize;
            var readerFont = ViewModel.PreferencesService.ReaderFont;
            int readerFontSize = ViewModel.PreferencesService.ReaderFontSize;
            return await ExecuteScriptFunctionAsync("initializeJodit", fonts, composerFont, composerFontSize, readerFont, readerFontSize);
        }

        protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            base.OnNavigatingFrom(e);

            DisposeDisposables();
            DisposeWebView2();
        }

        private void DisposeWebView2()
        {
            if (Chromium == null) return;

            Chromium.CoreWebView2Initialized -= ChromiumInitialized;

            if (Chromium.CoreWebView2 != null)
            {
                Chromium.CoreWebView2.DOMContentLoaded -= DOMLoaded;
                Chromium.CoreWebView2.WebMessageReceived -= ScriptMessageReceived;
            }

            Chromium.Close();
            GC.Collect();
        }

        private void DisposeDisposables()
        {
            if (Disposables.Any())
                Disposables.ForEach(a => a.Dispose());
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var anim = ConnectedAnimationService.GetForCurrentView().GetAnimation("WebViewConnectedAnimation");
            anim?.TryStart(Chromium);

            DisposeDisposables();

            Disposables.Add(GetSuggestionBoxDisposable(ToBox));
            Disposables.Add(GetSuggestionBoxDisposable(CCBox));
            Disposables.Add(GetSuggestionBoxDisposable(BccBox));

            Chromium.CoreWebView2Initialized -= ChromiumInitialized;
            Chromium.CoreWebView2Initialized += ChromiumInitialized;

            await Chromium.EnsureCoreWebView2Async();

            ViewModel.GetHTMLBodyFunction = new Func<Task<string>>(async () =>
            {
                var editorContent = await InvokeScriptSafeAsync("GetHTMLContent();");

                return JsonConvert.DeserializeObject<string>(editorContent);
            });

            var underlyingThemeService = App.Current.Services.GetService<IUnderlyingThemeService>();

            IsComposerDarkMode = underlyingThemeService.IsUnderlyingThemeDark();
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
            var change = JsonConvert.DeserializeObject<WebViewMessage>(args.WebMessageAsJson);

            if (change.type == "bold")
            {
                BoldButton.IsChecked = change.value == "true";
            }
            else if (change.type == "italic")
            {
                ItalicButton.IsChecked = change.value == "true";
            }
            else if (change.type == "underline")
            {
                UnderlineButton.IsChecked = change.value == "true";
            }
            else if (change.type == "strikethrough")
            {
                StrokeButton.IsChecked = change.value == "true";
            }
            else if (change.type == "ol")
            {
                OrderedListButton.IsChecked = change.value == "true";
            }
            else if (change.type == "ul")
            {
                BulletListButton.IsChecked = change.value == "true";
            }
            else if (change.type == "indent")
            {
                IncreaseIndentButton.IsEnabled = change.value == "disabled" ? false : true;
            }
            else if (change.type == "outdent")
            {
                DecreaseIndentButton.IsEnabled = change.value == "disabled" ? false : true;
            }
            else if (change.type == "alignment")
            {
                var parsedValue = change.value switch
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

        private void DOMLoaded(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args) => DOMLoadedTask.TrySetResult(true);

        void IRecipient<NavigationPaneModeChanged>.Receive(NavigationPaneModeChanged message)
        {
            if (message.NewMode == MenuPaneMode.Hidden)
                TopPanelGrid.Padding = new Thickness(48, 6, 6, 6);
            else
                TopPanelGrid.Padding = new Thickness(16, 6, 6, 6);
        }

        async void IRecipient<CreateNewComposeMailRequested>.Receive(CreateNewComposeMailRequested message)
        {
            await RenderInternalAsync(message.RenderModel.RenderHtml);
        }

        private void BarDynamicOverflowChanging(CommandBar sender, DynamicOverflowItemsChangingEventArgs args)
        {
            if (args.Action == CommandBarDynamicOverflowAction.AddingToOverflow)
                sender.OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Visible;
            else
                sender.OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Collapsed;
        }

        private void ShowCCBCCClicked(object sender, RoutedEventArgs e)
        {
            CCBCCShowButton.Visibility = Visibility.Collapsed;

            CCTextBlock.Visibility = Visibility.Visible;
            CCBox.Visibility = Visibility.Visible;
            BccTextBlock.Visibility = Visibility.Visible;
            BccBox.Visibility = Visibility.Visible;
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

            var deferal = args.GetDeferral();

            AddressInformation addedItem = null;

            var boxTag = sender.Tag?.ToString();

            if (boxTag == "ToBox")
                addedItem = await ViewModel.GetAddressInformationAsync(args.TokenText, ViewModel.ToItems);
            else if (boxTag == "CCBox")
                addedItem = await ViewModel.GetAddressInformationAsync(args.TokenText, ViewModel.CCItemsItems);
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

            deferal.Complete();
        }

        void IRecipient<ApplicationThemeChanged>.Receive(ApplicationThemeChanged message)
        {
            IsComposerDarkMode = message.IsUnderlyingThemeDark;
        }

        private void InvertComposerThemeClicked(object sender, RoutedEventArgs e)
        {
            IsComposerDarkMode = !IsComposerDarkMode;
        }

        private void ImportanceClicked(object sender, RoutedEventArgs e)
        {
            ImportanceFlyout.Hide();
            ImportanceSplitButton.IsChecked = true;

            if (sender is Button senderButton)
            {
                var selectedImportance = (MessageImportance)senderButton.Tag;

                ViewModel.SelectedMessageImportance = selectedImportance;
                (ImportanceSplitButton.Content as SymbolIcon).Symbol = (senderButton.Content as SymbolIcon).Symbol;
            }
        }

        private void AttachmentClicked(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is MailAttachmentViewModel attachmentViewModel)
            {
                ViewModel.RemoveAttachmentCommand.Execute(attachmentViewModel);
            }
        }

        private async void AddressBoxLostFocus(object sender, RoutedEventArgs e)
        {
            // Automatically add current text as item if it is valid mail address.

            if (sender is TokenizingTextBox tokenizingTextBox)
            {
                if (!(tokenizingTextBox.Items.LastOrDefault() is ITokenStringContainer info)) return;

                var currentText = info.Text;

                if (!string.IsNullOrEmpty(currentText) && EmailValidator.Validate(currentText))
                {
                    var boxTag = tokenizingTextBox.Tag?.ToString();

                    AddressInformation addedItem = null;
                    ObservableCollection<AddressInformation> addressCollection = null;

                    if (boxTag == "ToBox")
                        addressCollection = ViewModel.ToItems;
                    else if (boxTag == "CCBox")
                        addressCollection = ViewModel.CCItemsItems;
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
    }
}
