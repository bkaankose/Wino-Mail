using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Printing;
using Wino.Editor;
using Wino.Helpers;
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
    private string _currentRenderedHtml = string.Empty;
    private bool _isPoppedOut;

    public bool SupportsPopOut => !_isPoppedOut;
    public event EventHandler<PopOutRequestedEventArgs>? PopOutRequested;
    public event EventHandler<PopoutHostActionRequestedEventArgs>? HostActionRequested;

    public WebView2 GetWebView() => MailRenderer.GetUnderlyingWebView();
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
            return GetWebView().CoreWebView2.PrintToPdfAsync(path, null).AsTask();
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

    private async Task<Stream> RenderPdfStreamAsync(WebView2PrintSettingsModel settings)
    {
        var webView = GetWebView();
        if (webView.CoreWebView2 == null)
            throw new InvalidOperationException("WebView2 is not initialized for printing.");

        var nativeSettings = settings.ToCoreWebView2PdfRenderSettings(webView.CoreWebView2.Environment);
        var pdfStream = await webView.CoreWebView2.PrintToPdfStreamAsync(nativeSettings);
        return pdfStream.AsStreamForRead();
    }

    private async Task RenderInternalAsync(string htmlBody)
    {
        isRenderingInProgress = true;
        _currentRenderedHtml = htmlBody ?? string.Empty;

        try
        {
            await UpdateEditorThemeAsync();
            await UpdateReaderFontPropertiesAsync();

            var shouldLinkifyText = ViewModel.CurrentRenderModel?.MailRenderingOptions?.RenderPlaintextLinks ?? true;
            await MailRenderer.RenderHtmlAsync(string.IsNullOrEmpty(htmlBody) ? " " : htmlBody, shouldLinkifyText);

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
        var accessibleText = ViewModel.CurrentRenderModel?.AccessibleText ?? string.Empty;

        await MailRenderer.SetAccessibilityContextAsync(
            subject,
            sender,
            creationDate,
            Translator.Reader_MessageBodyAutomationName,
            Translator.Reader_PlainTextFallbackAutomationName,
            accessibleText);
    }

    private async void MailRenderer_NavigationRequested(object? sender, RendererNavigationRequestedEventArgs args)
    {
        try
        {
            await ViewModel.NativeAppService.LaunchUriAsync(args.Uri);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open a link from the mail renderer.");
        }
    }

    private void MailRenderer_InitializationFailed(object? sender, Exception exception)
        => Log.Error(exception, "Mail rendering WebView2 initialization failed.");

    private async Task ObserveChromiumInitializationAsync(Task initializationTask)
    {
        try
        {
            await initializationTask;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Mail rendering WebView2 initialization failed.");
        }
    }

    public async Task ClearRenderedContentAsync()
    {
        if (!isRenderingInProgress)
        {
            _currentRenderedHtml = string.Empty;
            await MailRenderer.ClearAsync();
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
        RendererCommandBar.PopOutClicked -= RendererCommandBar_PopOutClicked;
        RendererCommandBar.IsAIActionsEnabled = false;
        ReaderAiActionsPanel.CancelPendingOperation();

        MailRenderer.Dispose();
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

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.RenderHtmlAsyncFunc = RenderInternalAsync;
        ViewModel.ClearRenderedHtmlAsyncFunc = ClearRenderedContentAsync;
        RendererCommandBar.AIActionsEnabledChanged -= RendererCommandBar_AIActionsEnabledChanged;
        RendererCommandBar.AIActionsEnabledChanged += RendererCommandBar_AIActionsEnabledChanged;
        RendererCommandBar.PopOutClicked -= RendererCommandBar_PopOutClicked;
        RendererCommandBar.PopOutClicked += RendererCommandBar_PopOutClicked;
        RendererCommandBar.IsAIActionsEnabled = false;
        _ = ObserveChromiumInitializationAsync(InitializeMailRendererAsync());

        base.OnNavigatedTo(e);

        var anim = ConnectedAnimationService.GetForCurrentView().GetAnimation("WebViewConnectedAnimation");
        anim?.TryStart(GetWebView());

        // We don't have shell initialized here. It's only standalone EML viewing.
        // Shift command bar from top to adjust the design.

        if (ViewModel.StatePersistenceService.ShouldShiftMailRenderingDesign)
            RendererGridFrame.Margin = new Thickness(0, 24, 0, 0);
        else
            RendererGridFrame.Margin = new Thickness(0, 0, 0, 0);
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
        await MailRenderer.SetThemeAsync(ViewModel.IsDarkWebviewRenderer);
    }

    private async Task InitializeMailRendererAsync()
    {
        try
        {
            await MailRenderer.InitializeAsync();
        }
        catch (Exception)
        {
            // TODO: Debug object disposal.
            // throw new InvalidOperationException(Translator.Exception_WebView2RuntimeMissing_Message, ex);
        }
    }

    private async Task UpdateReaderFontPropertiesAsync()
    {
        var fontName = $"{_preferencesService.ReaderFont}, sans-serif";
        await MailRenderer.SetReaderTypographyAsync(fontName, _preferencesService.ReaderFontSize);
    }

    void IRecipient<ApplicationThemeChanged>.Receive(ApplicationThemeChanged message)
    {
        DispatcherQueue.TryEnqueue(() =>
            ViewModel.IsDarkWebviewRenderer = message.IsUnderlyingThemeDark);
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
