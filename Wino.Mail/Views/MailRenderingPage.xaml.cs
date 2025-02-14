using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.UWP.Extensions;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Client.Shell;
using Wino.Views.Abstract;

namespace Wino.Views
{
    public sealed partial class MailRenderingPage : MailRenderingPageAbstract,
        IRecipient<HtmlRenderingRequested>,
        IRecipient<CancelRenderingContentRequested>,
        IRecipient<ApplicationThemeChanged>
    {
        private readonly IPreferencesService _preferencesService = App.Current.Services.GetService<IPreferencesService>();
        private readonly IMailDialogService _dialogService = App.Current.Services.GetService<IMailDialogService>();

        private bool isRenderingInProgress = false;
        private TaskCompletionSource<bool> DOMLoadedTask = new TaskCompletionSource<bool>();

        private bool isChromiumDisposed = false;

        public WebView2 GetWebView() => Chromium;

        public MailRenderingPage()
        {
            InitializeComponent();

            Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00FFFFFF");
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--enable-features=OverlayScrollbar,msOverlayScrollbarWinStyle,msOverlayScrollbarWinStyleAnimation,msWebView2CodeCache");

            ViewModel.SaveHTMLasPDFFunc = new Func<string, Task<bool>>((path) =>
            {
                return Chromium.CoreWebView2.PrintToPdfAsync(path, null).AsTask();
            });
        }

        public override async void OnEditorThemeChanged()
        {
            base.OnEditorThemeChanged();

            await UpdateEditorThemeAsync();
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

        private async Task RenderInternalAsync(string htmlBody)
        {
            isRenderingInProgress = true;

            await DOMLoadedTask.Task;

            await UpdateEditorThemeAsync();
            await UpdateReaderFontPropertiesAsync();

            if (string.IsNullOrEmpty(htmlBody))
            {
                await Chromium.ExecuteScriptFunctionAsync("RenderHTML", isChromiumDisposed, JsonSerializer.Serialize(" ", BasicTypesJsonContext.Default.String));
            }
            else
            {
                var shouldLinkifyText = ViewModel.CurrentRenderModel?.MailRenderingOptions?.RenderPlaintextLinks ?? true;
                await Chromium.ExecuteScriptFunctionAsync("RenderHTML", isChromiumDisposed,
                    JsonSerializer.Serialize(htmlBody, BasicTypesJsonContext.Default.String),
                    JsonSerializer.Serialize(shouldLinkifyText, BasicTypesJsonContext.Default.Boolean));
            }

            isRenderingInProgress = false;
        }

        private async void WindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
        {
            args.Handled = true;

            try
            {
                await Launcher.LaunchUriAsync(new Uri(args.Uri));
            }
            catch (Exception) { }
        }

        private void DOMContentLoaded(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args) => DOMLoadedTask.TrySetResult(true);

        async void IRecipient<HtmlRenderingRequested>.Receive(HtmlRenderingRequested message)
        {
            if (message == null || string.IsNullOrEmpty(message.HtmlBody))
            {
                await RenderInternalAsync(string.Empty);
                return;
            }

            await Chromium.EnsureCoreWebView2Async();

            await RenderInternalAsync(message.HtmlBody);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            // Disposing the page.
            // Make sure the WebView2 is disposed properly.

            DisposeWebView2();
        }

        private void DisposeWebView2()
        {
            if (Chromium == null) return;

            Chromium.CoreWebView2Initialized -= CoreWebViewInitialized;
            Chromium.NavigationStarting -= WebViewNavigationStarting;

            if (Chromium.CoreWebView2 != null)
            {
                Chromium.CoreWebView2.DOMContentLoaded -= DOMContentLoaded;
                Chromium.CoreWebView2.NewWindowRequested -= WindowRequested;
            }

            isChromiumDisposed = true;

            Chromium.Close();
            GC.Collect();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var anim = ConnectedAnimationService.GetForCurrentView().GetAnimation("WebViewConnectedAnimation");
            anim?.TryStart(Chromium);

            Chromium.CoreWebView2Initialized -= CoreWebViewInitialized;
            Chromium.CoreWebView2Initialized += CoreWebViewInitialized;

            _ = Chromium.EnsureCoreWebView2Async();

            // We don't have shell initialized here. It's only standalone EML viewing.
            // Shift command bar from top to adjust the design.

            if (ViewModel.StatePersistenceService.ShouldShiftMailRenderingDesign)
                RendererGridFrame.Margin = new Thickness(0, 24, 0, 0);
            else
                RendererGridFrame.Margin = new Thickness(0, 0, 0, 0);
        }

        private async void CoreWebViewInitialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
        {
            if (Chromium.CoreWebView2 == null) return;

            var editorBundlePath = (await ViewModel.NativeAppService.GetEditorBundlePathAsync()).Replace("editor.html", string.Empty);

            Chromium.CoreWebView2.SetVirtualHostNameToFolderMapping("app.reader", editorBundlePath, CoreWebView2HostResourceAccessKind.Allow);

            Chromium.CoreWebView2.DOMContentLoaded -= DOMContentLoaded;
            Chromium.CoreWebView2.DOMContentLoaded += DOMContentLoaded;

            Chromium.CoreWebView2.NewWindowRequested -= WindowRequested;
            Chromium.CoreWebView2.NewWindowRequested += WindowRequested;

            Chromium.Source = new Uri("https://app.reader/reader.html");
        }


        async void IRecipient<CancelRenderingContentRequested>.Receive(CancelRenderingContentRequested message)
        {
            await Chromium.EnsureCoreWebView2Async();

            if (!isRenderingInProgress)
            {
                await RenderInternalAsync(string.Empty);
            }
        }

        private async void WebViewNavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            // This is our reader.
            if (args.Uri == "https://app.reader/reader.html")
                return;

            // Cancel all external navigations since it's navigating to different address inside the WebView2.
            args.Cancel = !args.Uri.StartsWith("data:text/html");

            // TODO: Check external link navigation setting is enabled.
            // Open all external urls in launcher.

            if (args.Cancel && Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri newUri))
            {
                await Launcher.LaunchUriAsync(newUri);
            }
        }

        private void AttachmentClicked(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is MailAttachmentViewModel attachmentViewModel)
            {
                ViewModel.OpenAttachmentCommand.Execute(attachmentViewModel);
            }
        }

        private void BarDynamicOverflowChanging(CommandBar sender, DynamicOverflowItemsChangingEventArgs args)
        {
            if (args.Action == CommandBarDynamicOverflowAction.AddingToOverflow || sender.SecondaryCommands.Any())
                sender.OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Visible;
            else
                sender.OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Collapsed;
        }

        private async Task UpdateEditorThemeAsync()
        {
            await DOMLoadedTask.Task;

            if (ViewModel.IsDarkWebviewRenderer)
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

        private async Task UpdateReaderFontPropertiesAsync()
        {
            await Chromium.ExecuteScriptFunctionAsync("ChangeFontSize", isChromiumDisposed, JsonSerializer.Serialize(_preferencesService.ReaderFontSize, BasicTypesJsonContext.Default.Int32));

            // Prepare font family name with fallback to sans-serif by default.
            var fontName = _preferencesService.ReaderFont;

            // If font family name is not supported by the browser, fallback to sans-serif.
            fontName += ", sans-serif";

            await Chromium.ExecuteScriptFunctionAsync("ChangeFontFamily", isChromiumDisposed, JsonSerializer.Serialize(fontName, BasicTypesJsonContext.Default.String));
        }

        void IRecipient<ApplicationThemeChanged>.Receive(ApplicationThemeChanged message)
        {
            ViewModel.IsDarkWebviewRenderer = message.IsUnderlyingThemeDark;
        }

        private void InternetAddressClicked(object sender, RoutedEventArgs e)
        {
            if (sender is HyperlinkButton hyperlinkButton)
            {
                hyperlinkButton.ContextFlyout.ShowAt(hyperlinkButton);
            }
        }
    }
}
