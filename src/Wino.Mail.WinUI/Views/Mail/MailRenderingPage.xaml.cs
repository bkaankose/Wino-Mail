using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using WinRT;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Ai;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Printing;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Editor;
using Wino.Helpers;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.ContentProcessing;
using Wino.Mail.Controls.Core.IntelligenceHeader;
using Wino.Mail.Controls.Core.IntelligenceTileBar;
using Wino.Mail.Controls.Core.ContextFlyout;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Intelligence;
using Wino.Mail.ViewModels.Models;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Extensions;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Mail.WinUI.Services;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;
using Wino.Views.Abstract;

namespace Wino.Views.Mail;

[GeneratedBindableCustomProperty(
    new string[] { nameof(ViewModel) },
    new Type[] { })]
public sealed partial class MailRenderingPage : MailRenderingPageAbstract,
    IPopoutClient,
    IReentryTarget,
    IRecipient<ApplicationThemeChanged>
{
    private readonly IPreferencesService _preferencesService = App.Current.Services.GetService<IPreferencesService>()!;

    // Selections can overlap: one message's render may still be awaiting its intelligence context
    // when the next arrives. Only the newest render is allowed to touch reader state.
    private IHtmlMailReaderSession _readerSession = null!;
    private int _renderVersion;
    private int _activeRenderCount;
    private string _currentRenderedHtml = string.Empty;
    private bool _isReaderViewEnabled;
    private bool _isPoppedOut;

    public bool SupportsPopOut => ViewModel.PlatformCapabilities.AdditionalWindows && !_isPoppedOut;
    public bool IsReaderViewEnabled
    {
        get => _isReaderViewEnabled;
        set
        {
            if (_isReaderViewEnabled == value)
                return;

            _isReaderViewEnabled = value;
            if (!string.IsNullOrWhiteSpace(_currentRenderedHtml))
                DispatcherQueue.TryEnqueue(async () => await RenderActiveContentAsync());
        }
    }
    public event EventHandler<PopOutRequestedEventArgs>? PopOutRequested;
    public event EventHandler<PopoutHostActionRequestedEventArgs>? HostActionRequested;

    public WebView2 GetWebView() => MailRenderer.GetUnderlyingWebView();
    public MailRenderingPage()
    {
        InitializeComponent();
        _readerSession = new WindowsHtmlMailReaderSession(MailRenderer);

        InitializeWinoIntelligenceHeader();

        WebViewExtensions.EnsureWebView2Environment();

        ViewModel.PrintPresenter = CreatePrintPresenter();
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

    private IMailPrintPresenter CreatePrintPresenter()
    {
        var windowManager = App.Current.Services.GetRequiredService<IWinoWindowManager>();
        var printService = App.Current.Services.GetRequiredService<IWindowsPrintService>();

        return new WindowsMailPrintPresenter(printService, () =>
        {
            var owner = windowManager.GetWindows()
                .FirstOrDefault(window => XamlRoot != null && ReferenceEquals(window.Content?.XamlRoot, XamlRoot));
            return owner == null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(owner);
        }, RenderPdfStreamAsync, path => GetWebView().CoreWebView2.PrintToPdfAsync(path, null).AsTask());
    }

    private async Task<Stream> RenderPdfStreamAsync(MailPrintOptions settings)
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
        var renderVersion = Interlocked.Increment(ref _renderVersion);

        Interlocked.Increment(ref _activeRenderCount);

        _currentRenderedHtml = htmlBody ?? string.Empty;
        _intelligence.SetRenderedContent(_currentRenderedHtml);

        try
        {
            await UpdateEditorThemeAsync();
            if (IsStaleRender(renderVersion)) return;

            await UpdateReaderFontPropertiesAsync();
            if (IsStaleRender(renderVersion)) return;

            // The header becomes visible before cloud/local metadata finishes loading.
            var intelligenceLoadingTask = LoadIntelligenceContextAsync();
            await RenderActiveContentAsync();
            if (IsStaleRender(renderVersion)) return;

            await UpdateAccessibleMailContextAsync();
            await intelligenceLoadingTask;
        }
        finally
        {
            Interlocked.Decrement(ref _activeRenderCount);
        }
    }

    private bool IsStaleRender(int renderVersion) => renderVersion != Volatile.Read(ref _renderVersion);

    private async Task RenderActiveContentAsync()
    {
        var html = _intelligence.ResolveActiveHtml(_currentRenderedHtml);
        var renderMode = IsReaderViewEnabled
            ? HtmlMailRenderMode.Readability
            : HtmlMailRenderMode.Original;
        var shouldLinkifyText = ViewModel.CurrentRenderModel?.MailRenderingOptions?.RenderPlaintextLinks ?? true;

        // Image stripping cannot reach CSS backgrounds or web fonts, so blocked remote
        // content is also enforced at the renderer's network layer.
        var resourcePolicy = (ViewModel.CurrentRenderModel?.MailRenderingOptions?.LoadImages ?? true)
            ? RemoteContentPolicy.ImagesAndFontsAllowed : RemoteContentPolicy.Blocked;
        await _readerSession.RenderAsync(new HtmlMailReaderRequest(
            string.IsNullOrEmpty(html) ? " " : html,
            resourcePolicy,
            renderMode,
            shouldLinkifyText));
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

        await _readerSession.SetAccessibilityContextAsync(new ReaderAccessibilityContext(
            subject,
            sender,
            creationDate,
            Translator.Reader_MessageBodyAutomationName,
            Translator.Reader_PlainTextFallbackAutomationName,
            accessibleText));
    }

    private async void MailRenderer_NavigationRequested(object? sender, RendererNavigationRequestedEventArgs args)
    {
        try
        {
            (await ViewModel.ExternalLauncher.LaunchUriAsync(args.Uri)).ThrowIfNotSucceeded();
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
        if (Volatile.Read(ref _activeRenderCount) == 0)
        {
            ResetReaderContentState();
            await _readerSession.ClearAsync();
        }
    }

    private void ResetReaderContentState()
    {
        _currentRenderedHtml = string.Empty;
        _intelligence.ResetContent();
    }

    public async Task PrepareForIdleAsync()
    {
        // Clearing the renderer means this item is no longer being shown. The page stays alive for
        // a short grace period, so a re-selection must not be suppressed as an already-loaded item.
        _currentMailItem = null;
        _intelligence.ForgetMailItem();

        await ClearRenderedContentAsync();
        await _readerSession.EnterIdleAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // This page is moved, rather than discarded, when the reader is popped
        // out. Retain its WebView2 and render delegates for the new window.
        if (_isPoppedOut)
            return;

        base.OnNavigatedFrom(e);

        // Disposing the page.
        // Make sure the WebView2 is disposed properly.

        ViewModel.PrintPresenter = null;
        ViewModel.RenderHtmlAsyncFunc = null;
        ViewModel.ClearRenderedHtmlAsyncFunc = null;
        _intelligence.ClearContext();
        _intelligence.ForgetMailItem();
        _currentMailItem = null;
        _currentRenderedHtml = string.Empty;
        RendererCommandBar.PopOutClicked -= RendererCommandBar_PopOutClicked;
        MailRenderer.Dispose();
    }

    bool IReentryTarget.CanReenter(object parameter)
    {
        // A popped-out reader owns its own window and must never absorb the shell's selection.
        if (_isPoppedOut) return false;

        // Standalone EML viewing arrives as MimeMessageInformation and takes the navigation path.
        if (parameter is not MailItemViewModel candidate) return false;

        // Drafts belong to the composer, which the mail list routes to directly.
        if (candidate.IsDraft) return false;

        return candidate.MailCopy?.AssignedAccount is not null;
    }

    Task IReentryTarget.ReenterAsync(object parameter) => RefreshMailItemAsync(parameter as MailItemViewModel);

    /// <summary>
    /// Shows another message without rebuilding the page, keeping its WebView2 alive.
    /// </summary>
    /// <remarks>
    /// The navigation service drops the returned task, so nothing may escape here: an unobserved
    /// failure would leave the reader on its loading skeleton for good.
    /// </remarks>
    public async Task RefreshMailItemAsync(MailItemViewModel mailItemViewModel)
    {
        try
        {
            if (mailItemViewModel is null || IsAlreadyShowing(mailItemViewModel)) return;

            // Loading state first: everything after this point belongs to the new message, and no
            // part of the previous one may be read as if it belonged to it.
            ViewModel.BeginMailContentLoad();

            // Abandons a render that is still in flight for the previous message.
            Interlocked.Increment(ref _renderVersion);

            _intelligence.ClearContext();
            ResetReaderContentState();

            _currentMailItem = mailItemViewModel;
            _intelligence.BeginMailItem(_currentMailItem);

            // Deliberately no _readerSession.ClearAsync(). The previous body stays loaded behind the
            // opaque loading overlay instead of blanking the pane, which is what removes the flash.
            await ViewModel.LoadContentAsync(mailItemViewModel);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reading pane re-entry failed.");
        }
    }

    // Compared by identifier rather than by reference: a synchronization can hand back a rebuilt
    // view model for the same message.
    private bool IsAlreadyShowing(MailItemViewModel candidate)
        => _currentMailItem?.MailCopy.UniqueId == candidate.MailCopy.UniqueId
           && ViewModel.IsMailContentReady;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.PrintPresenter = CreatePrintPresenter();
        _currentMailItem = e.Parameter as MailItemViewModel;
        _intelligence.BeginMailItem(_currentMailItem);
        ViewModel.RenderHtmlAsyncFunc = RenderInternalAsync;
        ViewModel.ClearRenderedHtmlAsyncFunc = ClearRenderedContentAsync;
        RendererCommandBar.PopOutClicked -= RendererCommandBar_PopOutClicked;
        RendererCommandBar.PopOutClicked += RendererCommandBar_PopOutClicked;
        _ = ObserveChromiumInitializationAsync(InitializeMailRendererAsync());

        base.OnNavigatedTo(e);

        var anim = ConnectedAnimationService.GetForCurrentView().GetAnimation("WebViewConnectedAnimation");
        anim?.TryStart(GetWebView());

        ApplyRenderingDesignShift();
    }

    private void ApplyRenderingDesignShift()
    {
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
        await _readerSession.SetThemeAsync(ViewModel.IsDarkWebviewRenderer);
    }

    private async Task InitializeMailRendererAsync()
    {
        try
        {
            await _readerSession.InitializeAsync();
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
        await _readerSession.SetReaderTypographyAsync(fontName, _preferencesService.ReaderFontSize);
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

    private void AttachmentContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: MailAttachmentViewModel attachment } target)
            return;

        WinoContextFlyoutHelper.Show(target, args, (ContextFlyoutMenuEntry[])
        [
            new ContextFlyoutCommandEntry
            {
                Text = Translator.Buttons_Open,
                Icon = new ContextFlyoutIcon(WinoIconGlyphs.GetGlyph(WinoIconGlyph.Open)),
                Command = ViewModel.OpenAttachmentCommand,
                CommandParameter = attachment,
                AutomationId = "MailRenderingAttachmentOpen"
            },
            new ContextFlyoutCommandEntry
            {
                Text = Translator.Buttons_Save,
                Icon = new ContextFlyoutIcon(WinoIconGlyphs.GetGlyph(WinoIconGlyph.Save)),
                Command = ViewModel.SaveAttachmentCommand,
                CommandParameter = attachment,
                Shortcut = new ContextFlyoutShortcut("Ctrl+S", "S", Control: true),
                AutomationId = "MailRenderingAttachmentSave"
            }
        ], FlyoutPlacementMode.Right);
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        WeakReferenceMessenger.Default.Register<ApplicationThemeChanged>(this);
        _intelligence.Attach();
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        WeakReferenceMessenger.Default.Unregister<ApplicationThemeChanged>(this);
        _intelligence.Detach();
    }

    private void EscapeInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        WeakReferenceMessenger.Default.Send(new ClearMailSelectionsRequested());
    }

    #region Wino Intelligence

    // The shared presenter owns the intelligence context, snapshot and requests. This page copies
    // its state onto the header control and routes the header's events back to it.
    private WinoIntelligenceHeaderPresenter _intelligence = null!;
    private MailItemViewModel? _currentMailItem;

    private void InitializeWinoIntelligenceHeader()
    {
        _intelligence = ActivatorUtilities.CreateInstance<WinoIntelligenceHeaderPresenter>(
            App.Current.Services, new PageQueueDispatcher(this));

        IntelligenceHeader.TranslationLanguages = _intelligence.TranslationLanguages;
        IntelligenceHeader.SelectedSourceLanguage = _intelligence.SelectedSourceLanguage;
        IntelligenceHeader.SelectedTargetLanguage = _intelligence.SelectedTargetLanguage;

        // Suggested replies, find-similar, deadlines and reply status are no longer produced.
        IntelligenceHeader.IsSuggestedRepliesAvailable = false;
        IntelligenceHeader.IsFindSimilarMailAvailable = false;
        IntelligenceHeader.NeedsReply = false;
        IntelligenceHeader.NeedsReplyDetailText = string.Empty;
        IntelligenceHeader.IsAddToCalendarAvailable = false;

        _intelligence.PropertyChanged += Intelligence_PropertyChanged;
        _intelligence.RerenderRequested += RenderActiveContentAsync;
        _intelligence.SummaryCompleted += (requestId, summary) => IntelligenceHeader.CompleteSummary(requestId, summary);
        _intelligence.SummaryFailed += requestId => IntelligenceHeader.FailRequest(requestId);
    }

    private Task LoadIntelligenceContextAsync()
        => _intelligence.OnHtmlRendered(_currentRenderedHtml, ViewModel.Subject, ViewModel.FromAddress, ViewModel.CreationDate);

    private void Intelligence_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WinoIntelligenceHeaderPresenter.IsVisible):
                IntelligenceHeader.Visibility = _intelligence.IsVisible ? Visibility.Visible : Visibility.Collapsed;
                break;
            case nameof(WinoIntelligenceHeaderPresenter.ContentKey):
                IntelligenceHeader.ContentKey = _intelligence.ContentKey;
                break;
            case nameof(WinoIntelligenceHeaderPresenter.ProcessingState):
                IntelligenceHeader.ProcessingState = _intelligence.ProcessingState;
                break;
            case nameof(WinoIntelligenceHeaderPresenter.IsSummaryAvailable):
                IntelligenceHeader.IsSummaryAvailable = _intelligence.IsSummaryAvailable;
                break;
            case nameof(WinoIntelligenceHeaderPresenter.IsTranslateAvailable):
                IntelligenceHeader.IsTranslateAvailable = _intelligence.IsTranslateAvailable;
                break;
            case nameof(WinoIntelligenceHeaderPresenter.IsProcessingAvailable):
                IntelligenceHeader.IsProcessingAvailable = _intelligence.IsProcessingAvailable;
                break;
            case nameof(WinoIntelligenceHeaderPresenter.BriefingFactText):
                IntelligenceHeader.BriefingFactText = _intelligence.BriefingFactText;
                break;
            case nameof(WinoIntelligenceHeaderPresenter.DeadlineText):
                IntelligenceHeader.DeadlineText = _intelligence.DeadlineText;
                break;
            case nameof(WinoIntelligenceHeaderPresenter.DeadlineDetailText):
                IntelligenceHeader.DeadlineDetailText = _intelligence.DeadlineDetailText;
                break;
            case nameof(WinoIntelligenceHeaderPresenter.VerificationCode):
                IntelligenceHeader.VerificationCode = _intelligence.VerificationCode;
                break;
            case nameof(WinoIntelligenceHeaderPresenter.SummaryText):
                IntelligenceHeader.SummaryText = _intelligence.SummaryText;
                break;
            case nameof(WinoIntelligenceHeaderPresenter.IntelligenceTiles):
                IntelligenceHeader.IntelligenceTiles = _intelligence.IntelligenceTiles;
                break;
            case nameof(WinoIntelligenceHeaderPresenter.SelectedSourceLanguage):
                IntelligenceHeader.SelectedSourceLanguage = _intelligence.SelectedSourceLanguage;
                break;
            case nameof(WinoIntelligenceHeaderPresenter.SelectedTargetLanguage):
                IntelligenceHeader.SelectedTargetLanguage = _intelligence.SelectedTargetLanguage;
                break;
            case nameof(WinoIntelligenceHeaderPresenter.IsTranslationBusy):
                IntelligenceHeader.IsTranslationBusy = _intelligence.IsTranslationBusy;
                break;
            case nameof(WinoIntelligenceHeaderPresenter.HasTranslationResult):
                IntelligenceHeader.HasTranslationResult = _intelligence.HasTranslationResult;
                break;
            case nameof(WinoIntelligenceHeaderPresenter.IsTranslationApplied):
                IntelligenceHeader.IsTranslationApplied = _intelligence.IsTranslationApplied;
                break;
            case nameof(WinoIntelligenceHeaderPresenter.TranslationStatusText):
                IntelligenceHeader.TranslationStatusText = _intelligence.TranslationStatusText;
                break;
        }
    }

    private async void IntelligenceHeader_CopyCodeRequested(object? sender, EventArgs e)
        => await _intelligence.CopyVerificationCodeAsync();

    private async void IntelligenceHeader_FeatureRequested(object? sender, WinoIntelligenceRequestEventArgs e)
    {
        // Suggested replies and find-similar are no longer offered; the header never
        // raises them, and an unexpected request fails rather than hanging.
        if (e.Feature != WinoIntelligenceFeature.Summary)
        {
            IntelligenceHeader.FailRequest(e.RequestId);
            return;
        }

        await _intelligence.RunSummaryAsync(e.RequestId);
    }

    private void IntelligenceHeader_FeatureCancelRequested(object? sender, WinoIntelligenceCancelRequestedEventArgs e)
        => _intelligence.CancelSummaryRequest(e.RequestId);

    private async void IntelligenceHeader_ProcessRequested(object? sender, EventArgs e)
        => await _intelligence.RequestProcessingAsync();

    private async void IntelligenceHeader_ActionInvoked(object? sender, WinoIntelligenceActionEventArgs e)
    {
        switch (e.Action)
        {
            case WinoIntelligenceAction.Translate:
                // The header owns the language pickers.
                _intelligence.SelectedSourceLanguage = IntelligenceHeader.SelectedSourceLanguage;
                _intelligence.SelectedTargetLanguage = IntelligenceHeader.SelectedTargetLanguage;
                await _intelligence.TranslateAsync();
                break;
            case WinoIntelligenceAction.CancelTranslation:
                _intelligence.CancelTranslation();
                break;
            case WinoIntelligenceAction.AddDeadlineToCalendar:
                // Deadlines are no longer produced, so the header never offers this.
                break;
        }
    }

    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();

        _intelligence.OnLanguageChanged();
    }

    /// <summary>Queues presenter callbacks on this page's thread, like DispatcherQueue.TryEnqueue.</summary>
    private sealed class PageQueueDispatcher(MailRenderingPage page) : IDispatcher
    {
        public Task ExecuteOnUIThread(Action action)
        {
            page.DispatcherQueue.TryEnqueue(() => action());
            return Task.CompletedTask;
        }
    }

    #endregion

}
