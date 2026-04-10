using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using Windows.System;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Printing;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Models;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Controls;
using Wino.Mail.WinUI.Extensions;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Client.Shell;
using Wino.Views.Abstract;

namespace Wino.Views.Mail;

public sealed partial class MailRenderingPage : MailRenderingPageAbstract,
    IAiHtmlActionHost,
    IPopoutClient,
    IRecipient<ApplicationThemeChanged>
{
    private readonly IPreferencesService _preferencesService = App.Current.Services.GetService<IPreferencesService>()!;
    private readonly IMailDialogService _dialogService = App.Current.Services.GetService<IMailDialogService>()!;
    private readonly IMimeFileService _mimeFileService = App.Current.Services.GetRequiredService<IMimeFileService>();

    private bool isRenderingInProgress = false;
    private bool? _lastAppliedDarkTheme;
    private TaskCompletionSource<bool> DOMLoadedTask = new TaskCompletionSource<bool>();
    private string _currentRenderedHtml = string.Empty;
    private bool _isPoppedOut;

    public bool SupportsPopOut => !_isPoppedOut;
    public event EventHandler<PopOutRequestedEventArgs>? PopOutRequested;
    public event EventHandler<PopoutHostActionRequestedEventArgs>? HostActionRequested;

    public WebView2 GetWebView() => Chromium;
    public bool GetAiActionsToggleVisible(bool isHidden) => !isHidden;
    public Visibility GetAiActionsPanelVisibility(bool isEnabled, bool isHidden)
        => !isHidden && isEnabled ? Visibility.Visible : Visibility.Collapsed;

    public MailRenderingPage()
    {
        InitializeComponent();

        WebViewExtensions.EnsureWebView2Environment();

        ViewModel.DirectPrintFuncAsync = DirectPrintAsync;

        ViewModel.SaveHTMLasPDFFunc = new Func<string, Task<bool>>((path) =>
        {
            return Chromium.CoreWebView2.PrintToPdfAsync(path, null).AsTask();
        });
        ViewModel.RenderHtmlAsyncFunc = RenderInternalAsync;
        ViewModel.ClearRenderedHtmlAsyncFunc = ClearRenderedContentAsync;
        ViewModel.CloseRequested += ViewModel_CloseRequested;
        ViewModel.ComposeRequested += ViewModel_ComposeRequested;

    }

    public HostedPopoutDescriptor GetPopoutDescriptor()
    {
        var title = string.IsNullOrWhiteSpace(ViewModel.Subject) ? Translator.MailItemNoSubject : ViewModel.Subject;
        var uniquePart = ViewModel.CurrentMailFileId?.ToString("N") ?? title;
        return new HostedPopoutDescriptor(
            $"mail-rendering-{uniquePart}",
            title,
            1080,
            780,
            640,
            480,
            nameof(MailRenderingPage));
    }

    public void OnPopoutStateChanged(bool isPoppedOut)
    {
        _isPoppedOut = isPoppedOut;
        Bindings.Update();
        RendererCommandBar.InvalidateCommands();
    }

    private async Task<PrintingResult> DirectPrintAsync(WebView2PrintSettingsModel settings)
    {
        if (Chromium.CoreWebView2 == null) return PrintingResult.Failed;

        try
        {
            var nativeSettings = settings.ToCoreWebView2PrintSettings(Chromium.CoreWebView2.Environment);
            var res = await Chromium.CoreWebView2.PrintAsync(nativeSettings);

            return res switch
            {
                CoreWebView2PrintStatus.Succeeded => PrintingResult.Submitted,
                _ => PrintingResult.Failed,
            };
        }
        catch (Exception)
        {
            return PrintingResult.Failed;
        }
    }

    public override async void OnEditorThemeChanged()
    {
        base.OnEditorThemeChanged();

        await UpdateEditorThemeAsync();
    }

    private async Task EnsureChromiumInitializedAsync()
    {
        var sharedEnvironment = await WebViewExtensions.GetSharedEnvironmentAsync();
        await Chromium.EnsureCoreWebView2Async(sharedEnvironment);
    }

    private async Task RenderInternalAsync(string htmlBody)
    {
        isRenderingInProgress = true;
        _currentRenderedHtml = htmlBody ?? string.Empty;

        await DOMLoadedTask.Task;

        await UpdateEditorThemeAsync();
        await UpdateReaderFontPropertiesAsync();

        if (string.IsNullOrEmpty(htmlBody))
        {
            await Chromium.ExecuteScriptFunctionAsync("RenderHTML", JsonSerializer.Serialize(" ", BasicTypesJsonContext.Default.String));
        }
        else
        {
            var shouldLinkifyText = ViewModel.CurrentRenderModel?.MailRenderingOptions?.RenderPlaintextLinks ?? true;
            await Chromium.ExecuteScriptFunctionAsync("RenderHTML",
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

    public async Task ClearRenderedContentAsync()
    {
        await EnsureChromiumInitializedAsync();

        if (!isRenderingInProgress)
        {
            await RenderInternalAsync(string.Empty);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        // Disposing the page.
        // Make sure the WebView2 is disposed properly.

        ViewModel.SaveHTMLasPDFFunc = null;
        ViewModel.DirectPrintFuncAsync = null;
        ViewModel.RenderHtmlAsyncFunc = null;
        ViewModel.ClearRenderedHtmlAsyncFunc = null;
        _currentRenderedHtml = string.Empty;
        RendererCommandBar.AIActionsEnabledChanged -= RendererCommandBar_AIActionsEnabledChanged;
        RendererCommandBar.PopOutClicked -= RendererCommandBar_PopOutClicked;
        RendererCommandBar.IsAIActionsEnabled = false;
        ReaderAiActionsPanel.CancelPendingOperation();

        DisposeWebView2();
    }

    public Task<string?> GetCurrentHtmlAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(_currentRenderedHtml);
    }

    public async Task ApplyHtmlResultAsync(string html, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RenderInternalAsync(html);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public Task RefreshMailItemAsync(MailItemViewModel mailItemViewModel)
    {
        return ViewModel.RefreshMailItemAsync(mailItemViewModel);
    }

    private async void RendererCommandBar_AIActionsEnabledChanged(object? sender, bool isEnabled)
    {
        if (isEnabled)
        {
            await ReaderAiActionsPanel.RefreshAvailabilityAsync();
        }
    }

    public async Task<string?> TryGetCachedTranslationHtmlAsync(string languageCode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ViewModel.CurrentMailAccountId.HasValue || !ViewModel.CurrentMailFileId.HasValue || string.IsNullOrWhiteSpace(languageCode))
        {
            return null;
        }

        return await _mimeFileService.GetTranslatedHtmlAsync(
            ViewModel.CurrentMailAccountId.Value,
            ViewModel.CurrentMailFileId.Value,
            languageCode,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveCachedTranslationHtmlAsync(string languageCode, string html, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ViewModel.CurrentMailAccountId.HasValue || !ViewModel.CurrentMailFileId.HasValue || string.IsNullOrWhiteSpace(languageCode))
        {
            return;
        }

        await _mimeFileService.SaveTranslatedHtmlAsync(
            ViewModel.CurrentMailAccountId.Value,
            ViewModel.CurrentMailFileId.Value,
            languageCode,
            html,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> TryGetCachedSummaryTextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ViewModel.CurrentMailAccountId.HasValue || !ViewModel.CurrentMailFileId.HasValue)
        {
            return null;
        }

        return await _mimeFileService.GetSummaryTextAsync(
            ViewModel.CurrentMailAccountId.Value,
            ViewModel.CurrentMailFileId.Value,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveCachedSummaryTextAsync(string summary, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ViewModel.CurrentMailAccountId.HasValue || !ViewModel.CurrentMailFileId.HasValue)
        {
            return;
        }

        await _mimeFileService.SaveSummaryTextAsync(
            ViewModel.CurrentMailAccountId.Value,
            ViewModel.CurrentMailFileId.Value,
            summary,
            cancellationToken).ConfigureAwait(false);
    }

    public string GetSuggestedSummaryFileName()
    {
        var subject = string.IsNullOrWhiteSpace(ViewModel.Subject) ? "email-summary" : ViewModel.Subject;
        return $"{SanitizeFileNamePart(subject)}.txt";
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

        Chromium.Close();
        GC.Collect();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        DOMLoadedTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ViewModel.RenderHtmlAsyncFunc = RenderInternalAsync;
        ViewModel.ClearRenderedHtmlAsyncFunc = ClearRenderedContentAsync;
        RendererCommandBar.AIActionsEnabledChanged -= RendererCommandBar_AIActionsEnabledChanged;
        RendererCommandBar.AIActionsEnabledChanged += RendererCommandBar_AIActionsEnabledChanged;
        RendererCommandBar.PopOutClicked -= RendererCommandBar_PopOutClicked;
        RendererCommandBar.PopOutClicked += RendererCommandBar_PopOutClicked;
        RendererCommandBar.IsAIActionsEnabled = false;
        Chromium.CoreWebView2Initialized -= CoreWebViewInitialized;
        Chromium.CoreWebView2Initialized += CoreWebViewInitialized;
        _ = EnsureChromiumInitializedAsync();

        base.OnNavigatedTo(e);

        var anim = ConnectedAnimationService.GetForCurrentView().GetAnimation("WebViewConnectedAnimation");
        anim?.TryStart(Chromium);

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

        Chromium.CoreWebView2.SetVirtualHostNameToFolderMapping("wino.mail", editorBundlePath, CoreWebView2HostResourceAccessKind.Allow);

        Chromium.CoreWebView2.DOMContentLoaded -= DOMContentLoaded;
        Chromium.CoreWebView2.DOMContentLoaded += DOMContentLoaded;

        Chromium.CoreWebView2.NewWindowRequested -= WindowRequested;
        Chromium.CoreWebView2.NewWindowRequested += WindowRequested;

        Chromium.Source = new Uri("https://wino.mail/reader.html");
    }

    private async void WebViewNavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        // This is our reader.
        if (args.Uri == "https://wino.mail/reader.html")
            return;

        // Cancel all external navigations since it's navigating to different address inside the WebView2.
        args.Cancel = !args.Uri.StartsWith("data:text/html");

        // TODO: Check external link navigation setting is enabled.
        // Open all external urls in launcher.

        if (args.Cancel && Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? newUri) && newUri != null)
        {
            await Launcher.LaunchUriAsync(newUri);
        }
    }

    private void AttachmentClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MailAttachmentViewModel attachmentViewModel)
        {
            ViewModel?.OpenAttachmentCommand.Execute(attachmentViewModel);
        }
    }

    private async Task UpdateEditorThemeAsync()
    {
        await DOMLoadedTask.Task;

        var isDark = ViewModel.IsDarkWebviewRenderer;

        if (_lastAppliedDarkTheme == isDark) return;

        _lastAppliedDarkTheme = isDark;

        if (isDark)
        {
            Chromium.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;

            await Chromium.ExecuteScriptSafeAsync("SetDarkEditor();");
        }
        else
        {
            Chromium.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;

            await Chromium.ExecuteScriptSafeAsync("SetLightEditor();");
        }
    }

    private async Task UpdateReaderFontPropertiesAsync()
    {
        await Chromium.ExecuteScriptFunctionAsync("ChangeFontSize", JsonSerializer.Serialize(_preferencesService.ReaderFontSize, BasicTypesJsonContext.Default.Int32));

        // Prepare font family name with fallback to sans-serif by default.
        var fontName = _preferencesService.ReaderFont;

        // If font family name is not supported by the browser, fallback to sans-serif.
        fontName += ", sans-serif";

        await Chromium.ExecuteScriptFunctionAsync("ChangeFontFamily", JsonSerializer.Serialize(fontName, BasicTypesJsonContext.Default.String));
    }

    void IRecipient<ApplicationThemeChanged>.Receive(ApplicationThemeChanged message)
    {
        ViewModel.IsDarkWebviewRenderer = message.IsUnderlyingThemeDark;
    }

    private void InternetAddressClicked(object sender, RoutedEventArgs e)
    {
        // TODO: Popped out windows don't have xaml root assigned properly, therefore ShowAt will fail.
        if (sender is HyperlinkButton hyperlinkButton && !_isPoppedOut)
        {
            hyperlinkButton.ContextFlyout.ShowAt(hyperlinkButton);
        }
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton button && button.CommandParameter is string address)
        {
            ViewModel.CopyClipboardCommand.Execute(address);
        }
    }

    private void RendererCommandBar_PopOutClicked(object? sender, EventArgs e)
    {
        PopOutRequested?.Invoke(this, PopOutRequestedEventArgs.Default);
    }

    private void ViewModel_CloseRequested(object? sender, EventArgs e)
    {
        HostActionRequested?.Invoke(this, new PopoutHostActionRequestedEventArgs(PopoutHostActionKind.CloseHostedInstance));
    }

    private void ViewModel_ComposeRequested(object? sender, ComposeDraftRequestedEventArgs e)
    {
        HostActionRequested?.Invoke(this, new PopoutHostActionRequestedEventArgs(PopoutHostActionKind.PopOutNextNavigation, typeof(ComposePage), e.DraftUniqueId));
    }

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

    private void EscapeInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        WeakReferenceMessenger.Default.Send(new ClearMailSelectionsRequested());
    }

    private static string SanitizeFileNamePart(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitizedChars = value.Trim().ToCharArray();

        for (var i = 0; i < sanitizedChars.Length; i++)
        {
            if (Array.IndexOf(invalidCharacters, sanitizedChars[i]) >= 0)
            {
                sanitizedChars[i] = '_';
            }
        }

        var sanitized = new string(sanitizedChars).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "email-summary" : sanitized;
    }
}
