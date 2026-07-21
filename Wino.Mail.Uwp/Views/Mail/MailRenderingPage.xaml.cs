using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using Serilog;
using Windows.System;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Printing;
using Wino.Helpers;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Models;
using Wino.Mail.Editor;
using Wino.Mail.Uwp;
using Wino.Mail.Uwp.Controls;
using Wino.Mail.Uwp.Extensions;
using Wino.Mail.Uwp.Interfaces;
using Wino.Mail.Uwp.Models;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Client.Shell;
using Wino.Views.Abstract;
using WebView2 = Microsoft.UI.Xaml.Controls.WebView2;

namespace Wino.Views.Mail;

public sealed partial class MailRenderingPage : MailRenderingPageAbstract,
    IAiHtmlActionHost,
    IRecipient<ApplicationThemeChanged>
{
    private readonly IPreferencesService _preferencesService = App.Current.Services.GetService<IPreferencesService>()!;
    private readonly IMailDialogService _dialogService = App.Current.Services.GetService<IMailDialogService>()!;
    private readonly IMimeFileService _mimeFileService = App.Current.Services.GetRequiredService<IMimeFileService>();

    private bool isRenderingInProgress = false;
    private string _currentRenderedHtml = string.Empty;

    public WebView2 GetWebView() => Chromium.GetUnderlyingWebView();
    public bool GetAiActionsToggleVisible(bool isHidden) => !isHidden;
    public Visibility GetAiActionsPanelVisibility(bool isEnabled, bool isHidden)
        => !isHidden && isEnabled ? Visibility.Visible : Visibility.Collapsed;

    public MailRenderingPage()
    {
        InitializeComponent();

        WebViewExtensions.EnsureWebView2Environment();

        ViewModel.RenderPdfStreamFuncAsync = RenderPdfStreamAsync;

        ViewModel.SaveHTMLasPDFFunc = new Func<string, Task<bool>>((path) =>
        {
            return Chromium.GetUnderlyingWebView().CoreWebView2.PrintToPdfAsync(path, null).AsTask();
        });
        ViewModel.RenderHtmlAsyncFunc = RenderInternalAsync;
        ViewModel.ClearRenderedHtmlAsyncFunc = ClearRenderedContentAsync;
        ViewModel.CloseRequested += ViewModel_CloseRequested;
        ViewModel.ComposeRequested += ViewModel_ComposeRequested;

    }

    private async Task<Stream> RenderPdfStreamAsync(WebView2PrintSettingsModel settings)
    {
        var webView = Chromium.GetUnderlyingWebView();
        if (webView.CoreWebView2 == null)
            throw new InvalidOperationException("WebView2 is not initialized for printing.");

        var nativeSettings = settings.ToCoreWebView2PdfRenderSettings(webView.CoreWebView2.Environment);
        var pdfStream = await webView.CoreWebView2.PrintToPdfStreamAsync(nativeSettings);
        return pdfStream.AsStreamForRead();
    }

    public override async void OnEditorThemeChanged()
    {
        base.OnEditorThemeChanged();

        await UpdateEditorThemeAsync();
    }

    private async Task RenderInternalAsync(string htmlBody)
    {
        isRenderingInProgress = true;
        _currentRenderedHtml = htmlBody ?? string.Empty;

        try
        {
            await Chromium.InitializeAsync();
            await UpdateEditorThemeAsync();
            await UpdateReaderFontPropertiesAsync();

            var shouldLinkifyText = ViewModel.CurrentRenderModel?.MailRenderingOptions?.RenderPlaintextLinks ?? true;
            await Chromium.RenderHtmlAsync(string.IsNullOrEmpty(htmlBody) ? " " : htmlBody, shouldLinkifyText);

            await UpdateAccessibleMailContextAsync();
        }
        finally
        {
            isRenderingInProgress = false;
        }
    }

    private async Task UpdateAccessibleMailContextAsync()
    {
        var subject = string.IsNullOrWhiteSpace(ViewModel.Subject) ? Translator.MailItemNoSubject : ViewModel.Subject;
        var sender = string.IsNullOrWhiteSpace(ViewModel.FromName)
            ? ViewModel.FromAddress
            : string.IsNullOrWhiteSpace(ViewModel.FromAddress)
                ? ViewModel.FromName
                : $"{ViewModel.FromName} <{ViewModel.FromAddress}>";
        var creationDate = XamlHelpers.GetCreationDateString(ViewModel.CreationDate, _preferencesService.MailTimeFormatPreference);
        await Chromium.SetAccessibilityContextAsync(
            subject,
            sender,
            creationDate,
            ViewModel.CurrentRenderModel?.AccessibleText);
    }

    public async Task ClearRenderedContentAsync()
    {
        await Chromium.InitializeAsync();

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
        ViewModel.RenderPdfStreamFuncAsync = null;
        ViewModel.RenderHtmlAsyncFunc = null;
        ViewModel.ClearRenderedHtmlAsyncFunc = null;
        _currentRenderedHtml = string.Empty;
        RendererCommandBar.AIActionsEnabledChanged -= RendererCommandBar_AIActionsEnabledChanged;
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

        Chromium.NavigationRequested -= Chromium_NavigationRequested;
        Chromium.Dispose();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.RenderHtmlAsyncFunc = RenderInternalAsync;
        ViewModel.ClearRenderedHtmlAsyncFunc = ClearRenderedContentAsync;
        RendererCommandBar.AIActionsEnabledChanged -= RendererCommandBar_AIActionsEnabledChanged;
        RendererCommandBar.AIActionsEnabledChanged += RendererCommandBar_AIActionsEnabledChanged;
        RendererCommandBar.IsAIActionsEnabled = false;
        Chromium.NavigationRequested -= Chromium_NavigationRequested;
        Chromium.NavigationRequested += Chromium_NavigationRequested;
        _ = Chromium.InitializeAsync();

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

    private async void Chromium_NavigationRequested(object? sender, RendererNavigationRequestedEventArgs args)
    {
        try { await Launcher.LaunchUriAsync(args.Uri); } catch (Exception) { }
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
        var isDark = ViewModel.IsDarkWebviewRenderer;
        Chromium.IsDarkMode = isDark;
        await Chromium.InitializeAsync();
    }

    private async Task UpdateReaderFontPropertiesAsync()
    {
        await Chromium.SetReaderTypographyAsync($"{_preferencesService.ReaderFont}, sans-serif", _preferencesService.ReaderFontSize);
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

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton button && button.CommandParameter is string address)
        {
            ViewModel.CopyClipboardCommand.Execute(address);
        }
    }

    private void ViewModel_CloseRequested(object? sender, EventArgs e)
    {
        WeakReferenceMessenger.Default.Send(new DisposeRenderingFrameRequested());
    }

    private void ViewModel_ComposeRequested(object? sender, ComposeDraftRequestedEventArgs e)
    {
        App.Current.Services.GetRequiredService<INavigationService>()
            .Navigate(WinoPage.ComposePage, e.DraftUniqueId, NavigationReferenceFrame.RenderingFrame);
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

    private void EscapeInvoked(Windows.UI.Xaml.Input.KeyboardAccelerator sender, Windows.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
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
