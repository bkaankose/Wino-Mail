using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Windows.UI.ViewManagement.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Messages.Mails;
using Wino.Views.Abstract;

namespace Wino.Views.Settings
{
    public sealed partial class SignatureManagementPage : SignatureManagementPageAbstract, IRecipient<HtmlRenderingRequested>
    {
        private TaskCompletionSource<bool> DOMLoadedTask = new TaskCompletionSource<bool>();

        public bool IsComposerDarkMode
        {
            get { return (bool)GetValue(IsComposerDarkModeProperty); }
            set { SetValue(IsComposerDarkModeProperty, value); }
        }

        public static readonly DependencyProperty IsComposerDarkModeProperty = DependencyProperty.Register(nameof(IsComposerDarkMode), typeof(bool), typeof(SignatureManagementPage), new PropertyMetadata(false, OnIsComposerDarkModeChanged));

        public SignatureManagementPage()
        {
            this.InitializeComponent();

            // Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00FFFFFF");
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            Chromium.CoreWebView2Initialized -= ChromiumInitialized;

            if (Chromium.CoreWebView2 != null)
            {
                Chromium.CoreWebView2.DOMContentLoaded -= DOMLoaded;
                Chromium.CoreWebView2.WebMessageReceived -= ScriptMessageRecieved;
            }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            Chromium.CoreWebView2Initialized -= ChromiumInitialized;
            Chromium.CoreWebView2Initialized += ChromiumInitialized;

            await Chromium.EnsureCoreWebView2Async();

            ViewModel.GetHTMLBodyFunction = new Func<Task<string>>(async () =>
            {
                var quillContent = await InvokeScriptSafeAsync("GetHTMLContent();");

                return JsonConvert.DeserializeObject<string>(quillContent);
            });

            ViewModel.GetTextBodyFunction = new Func<Task<string>>(() => InvokeScriptSafeAsync("GetTextContent();"));

            var underlyingThemeService = App.Current.Services.GetService<IUnderlyingThemeService>();

            IsComposerDarkMode = underlyingThemeService.IsUnderlyingThemeDark();
        }

        private async void HyperlinkAddClicked(object sender, RoutedEventArgs e)
        {
            await InvokeScriptSafeAsync($"addHyperlink('{LinkUrlTextBox.Text}')");

            LinkUrlTextBox.Text = string.Empty;
            HyperlinkFlyout.Hide();
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

        private async Task UpdateEditorThemeAsync()
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

                await FocusEditorAsync();
            }
        }

        private async void ChromiumInitialized(Microsoft.UI.Xaml.Controls.WebView2 sender, Microsoft.UI.Xaml.Controls.CoreWebView2InitializedEventArgs args)
        {
            var editorBundlePath = (await ViewModel.NativeAppService.GetQuillEditorBundlePathAsync()).Replace("full.html", string.Empty);

            Chromium.CoreWebView2.SetVirtualHostNameToFolderMapping("app.reader", editorBundlePath, CoreWebView2HostResourceAccessKind.Allow);
            Chromium.Source = new Uri("https://app.reader/full.html");

            Chromium.CoreWebView2.DOMContentLoaded -= DOMLoaded;
            Chromium.CoreWebView2.DOMContentLoaded += DOMLoaded;

            Chromium.CoreWebView2.WebMessageReceived -= ScriptMessageRecieved;
            Chromium.CoreWebView2.WebMessageReceived += ScriptMessageRecieved;
        }

        private static async void OnIsComposerDarkModeChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is ComposePage page)
            {
                await page.UpdateEditorThemeAsync();
            }
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

        public async void Receive(HtmlRenderingRequested message)
        {
            await RenderInternalAsync(message.HtmlBody);
        }
    }
}
