using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI.Controls;
using EmailValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using MimeKit;
using Newtonsoft.Json;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.UI.ViewManagement.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
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

            if (files == null) return;

            // Convert files to MailAttachmentViewModel.

            if (files.Any())
            {
                foreach (var file in files)
                {
                    if (!ViewModel.IncludedAttachments.Any(a => a.FileName == file.Path))
                    {
                        var attachmentViewModel = await file.ToAttachmentViewModelAsync();

                        ViewModel.IncludedAttachments.Add(attachmentViewModel);
                    }
                }
            }
        }

        private async void BoldButtonClicked(object sender, RoutedEventArgs e)
        {
            await InvokeScriptSafeAsync("document.getElementById('boldButton').click();");
        }

        private async void ItalicButtonClicked(object sender, RoutedEventArgs e)
        {
            await InvokeScriptSafeAsync("document.getElementById('italicButton').click();");
        }

        private async void UnderlineButtonClicked(object sender, RoutedEventArgs e)
        {
            await InvokeScriptSafeAsync("document.getElementById('underlineButton').click();");
        }

        private async void StrokeButtonClicked(object sender, RoutedEventArgs e)
        {
            await InvokeScriptSafeAsync("document.getElementById('strikeButton').click();");
        }

        private async void BulletListButtonClicked(object sender, RoutedEventArgs e)
        {
            await InvokeScriptSafeAsync("document.getElementById('bulletListButton').click();");
        }

        private async void OrderedListButtonClicked(object sender, RoutedEventArgs e)
        {
            await InvokeScriptSafeAsync("document.getElementById('orderedListButton').click();");
        }

        private async void IncreaseIndentClicked(object sender, RoutedEventArgs e)
        {
            await InvokeScriptSafeAsync("document.getElementById('increaseIndentButton').click();");
        }

        private async void DecreaseIndentClicked(object sender, RoutedEventArgs e)
        {
            await InvokeScriptSafeAsync("document.getElementById('decreaseIndentButton').click();");
        }

        private async void DirectionButtonClicked(object sender, RoutedEventArgs e)
        {
            await InvokeScriptSafeAsync("document.getElementById('directionButton').click();");
        }

        private async void AlignmentChanged(object sender, SelectionChangedEventArgs e)
        {
            var selectedItem = AlignmentListView.SelectedItem as ComboBoxItem;
            var alignment = selectedItem.Tag.ToString();

            switch (alignment)
            {
                case "left":
                    await InvokeScriptSafeAsync("document.getElementById('ql-align-left').click();");
                    break;
                case "center":
                    await InvokeScriptSafeAsync("document.getElementById('ql-align-center').click();");
                    break;
                case "right":
                    await InvokeScriptSafeAsync("document.getElementById('ql-align-right').click();");
                    break;
                case "justify":
                    await InvokeScriptSafeAsync("document.getElementById('ql-align-justify').click();");
                    break;
            }
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
            try
            {
                return await Chromium.ExecuteScriptAsync(function);
            }
            catch (Exception) { }

            return string.Empty;
        }

        private async void AddImageClicked(object sender, RoutedEventArgs e)
        {
            await InvokeScriptSafeAsync("document.getElementById('addImageButton').click();");
        }

        private async Task FocusEditorAsync()
        {
            await InvokeScriptSafeAsync("quill.focus();");

            Chromium.Focus(FocusState.Keyboard);
            Chromium.Focus(FocusState.Programmatic);
        }

        private async void EmojiButtonClicked(object sender, RoutedEventArgs e)
        {
            CoreInputView.GetForCurrentView().TryShow(CoreInputViewKind.Emoji);

            await FocusEditorAsync();
        }

        private async Task<string> TryGetSelectedTextAsync()
        {
            try
            {
                return await Chromium.ExecuteScriptAsync("getSelectedText();");
            }
            catch (Exception) { }

            return string.Empty;
        }

        private async void LinkButtonClicked(object sender, RoutedEventArgs e)
        {
            // Get selected text from Quill.

            HyperlinkTextBox.Text = await TryGetSelectedTextAsync();
        }

        public async Task UpdateEditorThemeAsync()
        {
            await DOMLoadedTask.Task;

            if (IsComposerDarkMode)
            {
                await InvokeScriptSafeAsync("DarkReader.enable();");
            }
            else
            {
                await InvokeScriptSafeAsync("DarkReader.disable();");
            }
        }

        private async Task RenderInternalAsync(string htmlBody)
        {
            await DOMLoadedTask.Task;

            await UpdateEditorThemeAsync();

            if (string.IsNullOrEmpty(htmlBody))
            {
                await ExecuteScriptFunctionAsync("RenderHTML", " ");
            }
            else
            {
                await ExecuteScriptFunctionAsync("RenderHTML", htmlBody);
            }
        }

        protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            base.OnNavigatingFrom(e);

            DisposeDisposables();

            Chromium.CoreWebView2Initialized -= ChromiumInitialized;

            if (Chromium.CoreWebView2 != null)
            {
                Chromium.CoreWebView2.DOMContentLoaded -= DOMLoaded;
                Chromium.CoreWebView2.WebMessageReceived -= ScriptMessageRecieved;
            }
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
                var quillContent = await InvokeScriptSafeAsync("GetHTMLContent();");

                return JsonConvert.DeserializeObject<string>(quillContent);
            });

            var underlyingThemeService = App.Current.Services.GetService<IUnderlyingThemeService>();

            IsComposerDarkMode = underlyingThemeService.IsUnderlyingThemeDark();
        }

        private async void ChromiumInitialized(Microsoft.UI.Xaml.Controls.WebView2 sender, Microsoft.UI.Xaml.Controls.CoreWebView2InitializedEventArgs args)
        {
            var editorBundlePath = (await ViewModel.NativeAppService.GetQuillEditorBundlePathAsync()).Replace("full.html", string.Empty);

            Chromium.CoreWebView2.SetVirtualHostNameToFolderMapping("app.example", editorBundlePath, CoreWebView2HostResourceAccessKind.Allow);
            Chromium.Source = new Uri("https://app.example/full.html");

            Chromium.CoreWebView2.DOMContentLoaded -= DOMLoaded;
            Chromium.CoreWebView2.DOMContentLoaded += DOMLoaded;

            Chromium.CoreWebView2.WebMessageReceived -= ScriptMessageRecieved;
            Chromium.CoreWebView2.WebMessageReceived += ScriptMessageRecieved;
        }

        private void ScriptMessageRecieved(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            var change = JsonConvert.DeserializeObject<string>(args.WebMessageAsJson);

            bool isEnabled = change.EndsWith("ql-active");

            if (change.StartsWith("ql-bold"))
                BoldButton.IsChecked = isEnabled;
            else if (change.StartsWith("ql-italic"))
                ItalicButton.IsChecked = isEnabled;
            else if (change.StartsWith("ql-underline"))
                UnderlineButton.IsChecked = isEnabled;
            else if (change.StartsWith("ql-strike"))
                StrokeButton.IsChecked = isEnabled;
            else if (change.StartsWith("orderedListButton"))
                OrderedListButton.IsChecked = isEnabled;
            else if (change.StartsWith("bulletListButton"))
                BulletListButton.IsChecked = isEnabled;
            else if (change.StartsWith("ql-direction"))
                DirectionButton.IsChecked = isEnabled;
            else if (change.StartsWith("ql-align-left"))
            {
                AlignmentListView.SelectionChanged -= AlignmentChanged;
                AlignmentListView.SelectedIndex = 0;
                AlignmentListView.SelectionChanged += AlignmentChanged;
            }
            else if (change.StartsWith("ql-align-center"))
            {
                AlignmentListView.SelectionChanged -= AlignmentChanged;
                AlignmentListView.SelectedIndex = 1;
                AlignmentListView.SelectionChanged += AlignmentChanged;
            }
            else if (change.StartsWith("ql-align-right"))
            {
                AlignmentListView.SelectionChanged -= AlignmentChanged;
                AlignmentListView.SelectedIndex = 2;
                AlignmentListView.SelectionChanged += AlignmentChanged;
            }
            else if (change.StartsWith("ql-align-justify"))
            {
                AlignmentListView.SelectionChanged -= AlignmentChanged;
                AlignmentListView.SelectedIndex = 3;
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

        private async void HyperlinkAddClicked(object sender, RoutedEventArgs e)
        {
            await InvokeScriptSafeAsync($"addHyperlink('{LinkUrlTextBox.Text}')");

            LinkUrlTextBox.Text = string.Empty;
            HyperlinkFlyout.Hide();
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
