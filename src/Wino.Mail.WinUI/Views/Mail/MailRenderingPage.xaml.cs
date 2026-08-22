using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
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
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
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
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Models;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Extensions;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;
using Wino.Views.Abstract;

namespace Wino.Views.Mail;

public sealed partial class MailRenderingPage : MailRenderingPageAbstract,
    IPopoutClient,
    IRecipient<ApplicationThemeChanged>,
    IRecipient<SemanticIndexJobChanged>,
    IRecipient<WinoIntelligenceAccessChanged>,
    IRecipient<IntelligenceMetadataChanged>,
    IRecipient<IntelligenceVisibilityChanged>
{
    private static readonly Regex ExcessiveReaderBreaks = new(
        @"(?is)<pre\b.*?</pre>|(?:<br\s*/?>\s*){3,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly IPreferencesService _preferencesService = App.Current.Services.GetService<IPreferencesService>()!;
    private readonly IMailDialogService _dialogService = App.Current.Services.GetService<IMailDialogService>()!;
    private readonly IMailContentProjector _contentProjector = App.Current.Services.GetRequiredService<IMailContentProjector>();

    private bool isRenderingInProgress = false;
    private string _currentRenderedHtml = string.Empty;
    private MailContentProjectionResult? _readerProjection;
    private bool _isPoppedOut;

    public bool SupportsPopOut => !_isPoppedOut;
    public event EventHandler<PopOutRequestedEventArgs>? PopOutRequested;
    public event EventHandler<PopoutHostActionRequestedEventArgs>? HostActionRequested;

    public WebView2 GetWebView() => MailRenderer.GetUnderlyingWebView();
    public MailRenderingPage()
    {
        InitializeComponent();

        InitializeWinoIntelligenceHeader();

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
        _readerProjection = _contentProjector.Project(_currentRenderedHtml, MailContentProjectionProfile.Reader);
        _translationProjection = _contentProjector.Project(_currentRenderedHtml, MailContentProjectionProfile.Translation);
        _inferenceProjection = _contentProjector.Project(_currentRenderedHtml, MailContentProjectionProfile.Inference).Projection;
        _translationMap = null;
        _isShowingTranslation = false;

        try
        {
            await UpdateEditorThemeAsync();
            await UpdateReaderFontPropertiesAsync();

            // The header becomes visible before cloud/local metadata finishes loading.
            var intelligenceLoadingTask = LoadIntelligenceContextAsync();
            await RenderActiveContentAsync();

            await UpdateAccessibleMailContextAsync();
            await intelligenceLoadingTask;
        }
        finally
        {
            isRenderingInProgress = false;
        }
    }

    private async Task RenderActiveContentAsync()
    {
        var html = _preferencesService.IsReaderViewEnabled && _readerProjection is not null
            ? NormalizeReaderBreaks(_readerProjection.RenderReaderHtml(_isShowingTranslation ? _translationMap : null))
            : _isShowingTranslation && _translationProjection is not null && _translationMap is not null
                ? _translationProjection.ApplyTranslations(_translationMap)
                : _currentRenderedHtml;
        var shouldLinkifyText = ViewModel.CurrentRenderModel?.MailRenderingOptions?.RenderPlaintextLinks ?? true;
        await MailRenderer.RenderHtmlAsync(string.IsNullOrEmpty(html) ? " " : html, shouldLinkifyText);
    }

    private static string NormalizeReaderBreaks(string readerHtml)
        => ExcessiveReaderBreaks.Replace(readerHtml, match =>
            match.Value.StartsWith("<pre", StringComparison.OrdinalIgnoreCase) ? match.Value : "<br>");

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
            _readerProjection = null;
            _translationProjection = null;
            _inferenceProjection = null;
            _translationMap = null;
            _isShowingTranslation = false;
            await MailRenderer.ClearAsync();
        }
    }

    public async Task PrepareForIdleAsync()
    {
        await ClearRenderedContentAsync();
        await MailRenderer.EnterIdleAsync();
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

        ViewModel.SaveHTMLasPDFFunc = null;
        ViewModel.RenderPdfStreamFuncAsync = null;
        ViewModel.RenderHtmlAsyncFunc = null;
        ViewModel.ClearRenderedHtmlAsyncFunc = null;
        ClearIntelligenceContext();
        _currentMailItem = null;
        _currentRenderedHtml = string.Empty;
        RendererCommandBar.PopOutClicked -= RendererCommandBar_PopOutClicked;
        _preferencesService.PreferenceChanged -= PreferencesService_PreferenceChanged;

        MailRenderer.Dispose();
    }

    public Task RefreshMailItemAsync(MailItemViewModel mailItemViewModel)
    {
        ClearIntelligenceContext();
        _currentMailItem = mailItemViewModel;
        return ViewModel.RefreshMailItemAsync(mailItemViewModel);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ClearIntelligenceContext();
        _currentMailItem = e.Parameter as MailItemViewModel;
        ShowIntelligenceHeaderImmediately(_currentMailItem);
        ViewModel.RenderHtmlAsyncFunc = RenderInternalAsync;
        ViewModel.ClearRenderedHtmlAsyncFunc = ClearRenderedContentAsync;
        RendererCommandBar.PopOutClicked -= RendererCommandBar_PopOutClicked;
        RendererCommandBar.PopOutClicked += RendererCommandBar_PopOutClicked;
        _preferencesService.PreferenceChanged -= PreferencesService_PreferenceChanged;
        _preferencesService.PreferenceChanged += PreferencesService_PreferenceChanged;
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

    private void PreferencesService_PreferenceChanged(object? sender, string propertyName)
    {
        if (propertyName != nameof(IPreferencesService.IsReaderViewEnabled) || string.IsNullOrWhiteSpace(_currentRenderedHtml))
            return;
        DispatcherQueue.TryEnqueue(async () => await RenderActiveContentAsync());
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
        RegisterWinoIntelligenceRecipients();
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        WeakReferenceMessenger.Default.Unregister<ApplicationThemeChanged>(this);
        UnregisterWinoIntelligenceRecipients();
    }

    private void EscapeInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        WeakReferenceMessenger.Default.Send(new ClearMailSelectionsRequested());
    }

    #region Wino Intelligence

    private readonly INavigationService _navigationService = App.Current.Services.GetRequiredService<INavigationService>();
    private readonly IWinoIntelligenceCoordinator _intelligenceCoordinator = App.Current.Services.GetRequiredService<IWinoIntelligenceCoordinator>();
    private readonly IAiActionOptionsService _aiActionOptionsService = App.Current.Services.GetRequiredService<IAiActionOptionsService>();
    private readonly IMailService _mailService = App.Current.Services.GetRequiredService<IMailService>();
    private readonly IClipboardService _clipboardService = App.Current.Services.GetRequiredService<IClipboardService>();
    private readonly HashSet<Guid> _liveFeatureRequestIds = [];

    private MailContentProjectionResult? _translationProjection;
    private MailContentProjection? _inferenceProjection;
    private IReadOnlyDictionary<string, string>? _translationMap;
    private bool _isShowingTranslation;
    private MailItemViewModel? _currentMailItem;
    private WinoIntelligenceContext? _intelligenceContext;
    private WinoIntelligenceSnapshot? _intelligenceSnapshot;
    private CancellationTokenSource? _intelligenceContextCancellation;
    private Guid? _translationRequestId;

    private void InitializeWinoIntelligenceHeader()
    {
        IntelligenceHeader.TranslationLanguages = new[]
        {
            new WinoIntelligenceLanguageOption(string.Empty, Translator.WinoIntelligence_DetectLanguage),
        }.Concat(_aiActionOptionsService.GetTranslateLanguageOptions()
            .Select(x => new WinoIntelligenceLanguageOption(x.Code, x.Label))).ToArray();
        IntelligenceHeader.SelectedSourceLanguage = string.Empty;
        IntelligenceHeader.SelectedTargetLanguage = _preferencesService.AiDefaultTranslationLanguageCode;
    }

    private void RegisterWinoIntelligenceRecipients()
    {
        WeakReferenceMessenger.Default.Register<SemanticIndexJobChanged>(this);
        WeakReferenceMessenger.Default.Register<WinoIntelligenceAccessChanged>(this);
        WeakReferenceMessenger.Default.Register<IntelligenceMetadataChanged>(this);
        WeakReferenceMessenger.Default.Register<IntelligenceVisibilityChanged>(this);
    }

    private void UnregisterWinoIntelligenceRecipients()
    {
        WeakReferenceMessenger.Default.Unregister<SemanticIndexJobChanged>(this);
        WeakReferenceMessenger.Default.Unregister<WinoIntelligenceAccessChanged>(this);
        WeakReferenceMessenger.Default.Unregister<IntelligenceMetadataChanged>(this);
        WeakReferenceMessenger.Default.Unregister<IntelligenceVisibilityChanged>(this);
    }

    private async Task LoadIntelligenceContextAsync()
    {
        var mailCopy = _currentMailItem?.MailCopy;
        var account = mailCopy?.AssignedAccount;
        if (mailCopy is null || account is null || mailCopy.IsDraft || string.IsNullOrWhiteSpace(_currentRenderedHtml))
        {
            IntelligenceHeader.Visibility = Visibility.Collapsed;
            return;
        }

        ShowIntelligenceHeaderImmediately(_currentMailItem);

        var contentKey = $"{account.Id:N}:{mailCopy.UniqueId:N}";
        if (_intelligenceContext is null || !string.Equals(_intelligenceContext.ContentKey, contentKey, StringComparison.Ordinal))
        {
            ClearIntelligenceContext();
            IntelligenceHeader.ContentKey = contentKey;
            _intelligenceContextCancellation = new CancellationTokenSource();
            _intelligenceContext = new WinoIntelligenceContext(
                contentKey,
                account.Id,
                mailCopy.UniqueId,
                mailCopy.FileId,
                mailCopy.Id,
                account.Address,
                account.ProviderType,
                account.Preferences?.IsSemanticIndexingEnabled == true,
                ViewModel.Subject ?? string.Empty,
                ViewModel.FromAddress ?? string.Empty,
                ToUtc(ViewModel.CreationDate),
                _currentRenderedHtml,
                _inferenceProjection,
                _translationProjection?.Projection,
                mailCopy.IntelligenceMetadata);
        }
        else
        {
            // Metadata can arrive after the reader context was created. Keep the same cancellation
            // scope, but refresh the immutable context so the snapshot sees imported artifacts.
            _intelligenceContext = _intelligenceContext with
            {
                IsSemanticIndexingEnabled = account.Preferences?.IsSemanticIndexingEnabled == true,
                Html = _currentRenderedHtml,
                InferenceProjection = _inferenceProjection,
                TranslationProjection = _translationProjection?.Projection,
                IntelligenceMetadata = mailCopy.IntelligenceMetadata,
            };
        }

        await RefreshIntelligenceSnapshotAsync();
    }

    private void ShowIntelligenceHeaderImmediately(MailItemViewModel mailItem)
    {
        // Eligibility is resolved asynchronously. Start collapsed so ineligible accounts never
        // see an Intelligence header flash while the shared access snapshot is loading.
        var canLoad = mailItem?.MailCopy is { IsDraft: false };
        IntelligenceHeader.Visibility = Visibility.Collapsed;
        IntelligenceHeader.IntelligenceTiles = mailItem?.IntelligenceTiles;
        if (!canLoad)
            return;

        if (mailItem.MailCopy.IntelligenceMetadata is { } metadata)
            ApplyPassiveIntelligenceMetadata(metadata);
        else
            ClearPassiveIntelligenceMetadata();

        IntelligenceHeader.IsSummaryAvailable = false;
        IntelligenceHeader.IsTranslateAvailable = false;
        IntelligenceHeader.IsProcessingAvailable = false;
        IntelligenceHeader.IsSuggestedRepliesAvailable = false;
        IntelligenceHeader.IsFindSimilarMailAvailable = false;
        IntelligenceHeader.ProcessingState = WinoIntelligenceProcessingState.NotProcessed;
    }

    private async Task RefreshIntelligenceSnapshotAsync()
    {
        var context = _intelligenceContext;
        var cancellation = _intelligenceContextCancellation;
        if (context is null || cancellation is null || cancellation.IsCancellationRequested)
            return;
        try
        {
            var snapshot = await _intelligenceCoordinator.GetSnapshotAsync(context, cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(context, _intelligenceContext))
                return;
            _intelligenceSnapshot = snapshot;
            ApplyIntelligenceSnapshot(snapshot);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ApplyIntelligenceSnapshot(WinoIntelligenceSnapshot snapshot)
    {
        IntelligenceHeader.IsSummaryAvailable = snapshot.IsSummaryAvailable;
        IntelligenceHeader.IsTranslateAvailable = snapshot.IsTranslateAvailable;
        IntelligenceHeader.IsProcessingAvailable = snapshot.IsProcessingAvailable;
        IntelligenceHeader.IsSuggestedRepliesAvailable = snapshot.IsSuggestedRepliesAvailable;
        IntelligenceHeader.IsFindSimilarMailAvailable = snapshot.IsFindSimilarAvailable;
        IntelligenceHeader.ProcessingState = MapProcessingState(snapshot.ProcessingState);
        if (_currentMailItem?.MailCopy.IntelligenceMetadata is { } metadata)
            ApplyPassiveIntelligenceMetadata(metadata);
        else
        {
            var excludedIndicators = _currentMailItem?.MailCopy?.AssignedAccount?.Preferences?
                .ExcludedIntelligenceIndicatorIds;
            var showNeedsReply = IntelligenceVisibilityPolicy.IsVisible(excludedIndicators, IntelligenceFactKind.NeedsReply);
            var showDeadline = IntelligenceVisibilityPolicy.IsVisible(excludedIndicators, IntelligenceFactKind.Deadline);

            IntelligenceHeader.NeedsReply = showNeedsReply && snapshot.NeedsReply;
            IntelligenceHeader.NeedsReplyDetailText = string.IsNullOrWhiteSpace(snapshot.NeedsReplyDetail)
                ? Translator.WinoIntelligence_NeedsReplyDetail
                : snapshot.NeedsReplyDetail;
            IntelligenceHeader.BriefingFactText = string.Empty;
            IntelligenceHeader.DeadlineText = showDeadline ? FormatDeadline(snapshot.Deadline) : string.Empty;
            IntelligenceHeader.DeadlineDetailText = snapshot.Deadline?.ActionText ?? string.Empty;
            IntelligenceHeader.IsAddToCalendarAvailable = showDeadline && snapshot.Deadline is not null;
        }
        if (!string.IsNullOrWhiteSpace(snapshot.CachedSummary))
            IntelligenceHeader.SummaryText = snapshot.CachedSummary;
        IntelligenceHeader.IntelligenceTiles = _currentMailItem?.IntelligenceTiles;
        IntelligenceHeader.Visibility = snapshot.IsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyPassiveIntelligenceMetadata(MailIntelligenceMetadata metadata)
    {
        var visibleTiles = _currentMailItem?.IntelligenceTiles ?? [];
        var hasDeadline = visibleTiles.Any(static tile => tile.Kind == WinoIntelligenceTileKind.Deadline);
        var hasNeedsReply = visibleTiles.Any(static tile => tile.Kind == WinoIntelligenceTileKind.NeedsReply);
        var hasBriefingFact = visibleTiles.Any(static tile => tile.Kind == WinoIntelligenceTileKind.BriefingFact);

        IntelligenceHeader.NeedsReply = hasNeedsReply && metadata.NeedsReply?.Value == true;
        IntelligenceHeader.NeedsReplyDetailText = Translator.WinoIntelligence_NeedsReplyDetail;
        IntelligenceHeader.BriefingFactText = hasBriefingFact ? metadata.Headline : string.Empty;
        IntelligenceHeader.DeadlineText = hasDeadline && metadata.Deadline?.HasDeadline == true
            ? MailIntelligenceTileFactory.FormatDeadline(metadata.Deadline, CultureInfo.CurrentCulture)
            : string.Empty;
        IntelligenceHeader.DeadlineDetailText = string.Empty;
        IntelligenceHeader.IsAddToCalendarAvailable = hasDeadline && metadata.Deadline?.HasDeadline == true;
        IntelligenceHeader.VerificationCode = metadata.VerificationCode ?? string.Empty;
    }

    private void ClearPassiveIntelligenceMetadata()
    {
        IntelligenceHeader.NeedsReply = false;
        IntelligenceHeader.NeedsReplyDetailText = string.Empty;
        IntelligenceHeader.BriefingFactText = string.Empty;
        IntelligenceHeader.DeadlineText = string.Empty;
        IntelligenceHeader.DeadlineDetailText = string.Empty;
        IntelligenceHeader.IsAddToCalendarAvailable = false;
        IntelligenceHeader.VerificationCode = string.Empty;
    }

    private async void IntelligenceHeader_CopyCodeRequested(object? sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(IntelligenceHeader.VerificationCode))
            await _clipboardService.CopyClipboardAsync(IntelligenceHeader.VerificationCode);
    }

    private void ClearIntelligenceContext()
    {
        if (_translationRequestId is { } translationRequestId)
            _intelligenceCoordinator.CancelRequest(translationRequestId);
        _translationRequestId = null;
        IntelligenceHeader.IsTranslationBusy = false;
        IntelligenceHeader.HasTranslationResult = false;
        IntelligenceHeader.IsTranslationApplied = false;
        IntelligenceHeader.TranslationStatusText = string.Empty;
        _intelligenceContextCancellation?.Cancel();
        _intelligenceContextCancellation?.Dispose();
        _intelligenceContextCancellation = null;
        if (_intelligenceContext is { } context)
            _intelligenceCoordinator.CancelContext(context.ContentKey);
        foreach (var requestId in _liveFeatureRequestIds.ToArray())
        {
            _intelligenceCoordinator.CancelRequest(requestId);
            IntelligenceHeader.FailRequest(requestId);
        }
        _liveFeatureRequestIds.Clear();
        _intelligenceContext = null;
        _intelligenceSnapshot = null;
        IntelligenceHeader.Visibility = Visibility.Collapsed;
        IntelligenceHeader.VerificationCode = string.Empty;
    }

    private async void IntelligenceHeader_FeatureRequested(object? sender, WinoIntelligenceRequestEventArgs e)
    {
        var context = _intelligenceContext;
        if (context is null)
        {
            IntelligenceHeader.FailRequest(e.RequestId);
            return;
        }
        _liveFeatureRequestIds.Add(e.RequestId);
        var result = e.Feature == WinoIntelligenceFeature.Summary
            ? await _intelligenceCoordinator.SummarizeAsync(context, e.RequestId)
            : null;
        if (e.Feature == WinoIntelligenceFeature.SuggestedReplies)
        {
            var replies = await _intelligenceCoordinator.GetSuggestedRepliesAsync(context, e.RequestId);
            _liveFeatureRequestIds.Remove(e.RequestId);
            if (!IsCurrent(replies.ContentKey) || replies.IsCanceled)
                return;
            if (!replies.IsSuccess)
            {
                ReportFeatureFailure(e.RequestId, replies.Error);
                return;
            }
            IntelligenceHeader.CompleteSuggestedReplies(
                e.RequestId,
                replies.Value?.Select(x => new WinoIntelligenceReply(x.Tone, x.Text)) ?? []);
            return;
        }

        if (e.Feature == WinoIntelligenceFeature.FindSimilarMail)
        {
            var similar = await _intelligenceCoordinator.FindSimilarAsync(context, e.RequestId);
            _liveFeatureRequestIds.Remove(e.RequestId);
            if (!IsCurrent(similar.ContentKey) || similar.IsCanceled)
                return;
            if (!similar.IsSuccess)
            {
                ReportFeatureFailure(e.RequestId, similar.Error);
                return;
            }

            IntelligenceHeader.CompleteSimilarMail(
                e.RequestId,
                similar.Value?.Select(item => new WinoIntelligenceSimilarMailItem
                {
                    DisplayName = item.Sender,
                    Initials = FormatInitials(item.Sender),
                    Subject = item.Subject,
                    Meta = $"{item.Sender} · {item.OccurredAtUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)}",
                    ScoreText = item.Similarity.ToString("P0", CultureInfo.CurrentCulture),
                    Tag = item.MailUniqueId,
                }) ?? []);
            return;
        }

        _liveFeatureRequestIds.Remove(e.RequestId);
        if (result is null || !IsCurrent(result.ContentKey) || result.IsCanceled)
            return;
        if (!result.IsSuccess)
        {
            ReportFeatureFailure(e.RequestId, result.Error);
            return;
        }
        IntelligenceHeader.CompleteSummary(e.RequestId, result.Value ?? string.Empty);
    }

    private void IntelligenceHeader_FeatureCancelRequested(object? sender, WinoIntelligenceCancelRequestedEventArgs e)
    {
        _liveFeatureRequestIds.Remove(e.RequestId);
        _intelligenceCoordinator.CancelRequest(e.RequestId);
    }

    private async void IntelligenceHeader_ProcessRequested(object? sender, EventArgs e)
    {
        var context = _intelligenceContext;
        if (context is null)
            return;
        try
        {
            IntelligenceHeader.ProcessingState = WinoIntelligenceProcessingState.Processing;
            await _intelligenceCoordinator.RequestProcessingAsync(context);
            if (!IsCurrent(context.ContentKey))
                return;
            IntelligenceHeader.ProcessingState = WinoIntelligenceProcessingState.Processed;
            await RefreshIntelligenceSnapshotAsync();
        }
        catch (Exception exception)
        {
            if (!IsCurrent(context.ContentKey))
                return;
            IntelligenceHeader.ProcessingState = WinoIntelligenceProcessingState.Failed;
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, WinoAccountApiErrorTranslator.Translate(exception.Message), InfoBarMessageType.Error);
        }
    }

    private async void IntelligenceHeader_ActionInvoked(object? sender, WinoIntelligenceActionEventArgs e)
    {
        var context = _intelligenceContext;
        if (context is null)
            return;
        try
        {
            switch (e.Action)
            {
                case WinoIntelligenceAction.Translate:
                    await TranslateCurrentMessageAsync(context);
                    break;
                case WinoIntelligenceAction.CancelTranslation:
                    CancelTranslation();
                    break;
                case WinoIntelligenceAction.AddDeadlineToCalendar:
                    OpenDeadlineInCalendar(context);
                    break;
            }
        }
        catch (Exception exception)
        {
            if (IsCurrent(context.ContentKey))
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, WinoAccountApiErrorTranslator.Translate(exception.Message), InfoBarMessageType.Error);
        }
    }

    private async void IntelligenceHeader_SuggestedReplyChosen(object? sender, WinoIntelligenceReplyChosenEventArgs e)
    {
        var context = _intelligenceContext;
        var cancellation = _intelligenceContextCancellation;
        if (context is null || cancellation is null)
            return;
        try
        {
            var draftId = await _intelligenceCoordinator.CreateSuggestedReplyDraftAsync(context, e.Reply.Text, cancellation.Token);
            if (IsCurrent(context.ContentKey))
                HostActionRequested?.Invoke(this, new PopoutHostActionRequestedEventArgs(PopoutHostActionKind.PopOutNextNavigation, typeof(ComposePage), draftId));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrent(context.ContentKey))
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, WinoAccountApiErrorTranslator.Translate(exception.Message), InfoBarMessageType.Error);
        }
    }

    private void IntelligenceHeader_SimilarMailChosen(object? sender, WinoIntelligenceSimilarMailChosenEventArgs e)
    {
        if (e.Item.Tag is Guid mailUniqueId)
            WeakReferenceMessenger.Default.Send(new MailItemNavigationRequested(mailUniqueId, ScrollToItem: true));
    }

    private async Task TranslateCurrentMessageAsync(WinoIntelligenceContext context)
    {
        if (_isShowingTranslation)
        {
            _isShowingTranslation = false;
            IntelligenceHeader.IsTranslationApplied = false;
            await RenderActiveContentAsync();
            return;
        }

        if (_translationMap is not null)
        {
            _isShowingTranslation = true;
            IntelligenceHeader.IsTranslationApplied = true;
            await RenderActiveContentAsync();
            return;
        }

        var requestId = Guid.NewGuid();
        _translationRequestId = requestId;
        IntelligenceHeader.IsTranslationBusy = true;
        IntelligenceHeader.TranslationStatusText = Translator.WinoIntelligence_Translating;
        var sourceLanguage = string.IsNullOrWhiteSpace(IntelligenceHeader.SelectedSourceLanguage)
            ? null
            : IntelligenceHeader.SelectedSourceLanguage;
        var targetLanguage = IntelligenceHeader.SelectedTargetLanguage;
        _preferencesService.AiDefaultTranslationLanguageCode = targetLanguage;
        WinoIntelligenceOperationResult<MailTranslationResult> result;
        try
        {
            result = await _intelligenceCoordinator.TranslateAsync(
                context,
                requestId,
                sourceLanguage,
                targetLanguage);
        }
        finally
        {
            if (_translationRequestId == requestId)
            {
                _translationRequestId = null;
                IntelligenceHeader.IsTranslationBusy = false;
            }
        }
        if (!IsCurrent(result.ContentKey) || result.IsCanceled)
            return;
        if (!result.IsSuccess)
            throw new InvalidOperationException(result.Error);
        if (result.Value is null)
            throw new InvalidOperationException("Translation response was empty.");
        _translationMap = result.Value.Translations.ToDictionary(x => x.Id, x => x.Text, StringComparer.Ordinal);
        _isShowingTranslation = true;
        IntelligenceHeader.HasTranslationResult = true;
        IntelligenceHeader.IsTranslationApplied = true;
        IntelligenceHeader.TranslationStatusText = $"{result.Value.DetectedSourceLanguage} → {targetLanguage}";
        await RenderActiveContentAsync();
    }

    private void CancelTranslation()
    {
        if (_translationRequestId is not { } requestId)
            return;
        _intelligenceCoordinator.CancelRequest(requestId);
        _translationRequestId = null;
        IntelligenceHeader.IsTranslationBusy = false;
        IntelligenceHeader.TranslationStatusText = Translator.WinoIntelligence_TranslationCanceled;
    }

    private void OpenDeadlineInCalendar(WinoIntelligenceContext context)
    {
        var deadline = _intelligenceSnapshot?.Deadline;
        if (deadline is null)
            return;
        var isAllDay = deadline.DueAtUtc is null && deadline.LocalDate is not null;
        var start = deadline.DueAtUtc?.ToLocalTime().DateTime
                    ?? deadline.LocalDate?.ToDateTime(TimeOnly.MinValue)
                    ?? DateTime.Now;
        var end = isAllDay
            ? deadline.LocalDateEnd?.AddDays(1).ToDateTime(TimeOnly.MinValue) ?? start.AddDays(1)
            : start.AddMinutes(30);
        var title = string.IsNullOrWhiteSpace(deadline.ActionText) ? context.Subject : deadline.ActionText;
        var notes = $"<p><strong>{WebUtility.HtmlEncode(context.Subject)}</strong><br>{WebUtility.HtmlEncode(context.Sender)}</p>";
        _navigationService.ChangeApplicationMode(
            WinoApplicationMode.Calendar,
            new ShellModeActivationContext
            {
                Parameter = new CalendarEventComposeNavigationArgs
                {
                    Title = title,
                    StartDate = start,
                    EndDate = end,
                    IsAllDay = isAllDay,
                    NotesHtml = notes,
                }
            });
    }

    private void ReportFeatureFailure(Guid requestId, string? error)
    {
        if (IntelligenceHeader.FailRequest(requestId))
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, error ?? Translator.WinoIntelligence_ActionFailed, InfoBarMessageType.Error);
    }

    private bool IsCurrent(string contentKey)
        => _intelligenceContext is { } context && string.Equals(context.ContentKey, contentKey, StringComparison.Ordinal);

    private static WinoIntelligenceProcessingState MapProcessingState(SemanticMessageIndexState state) => state switch
    {
        SemanticMessageIndexState.NotIndexed => WinoIntelligenceProcessingState.NotProcessed,
        SemanticMessageIndexState.Queued => WinoIntelligenceProcessingState.Queued,
        SemanticMessageIndexState.Indexing => WinoIntelligenceProcessingState.Processing,
        SemanticMessageIndexState.Indexed => WinoIntelligenceProcessingState.Processed,
        SemanticMessageIndexState.Failed => WinoIntelligenceProcessingState.Failed,
        _ => WinoIntelligenceProcessingState.Unavailable,
    };

    private static string FormatDeadline(WinoIntelligenceDeadline? deadline)
    {
        if (deadline?.DueAtUtc is { } dueAt)
            return dueAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        if (deadline?.LocalDate is not { } localDate)
            return string.Empty;
        var startText = localDate.ToString("d", CultureInfo.CurrentCulture);
        return deadline.LocalDateEnd is { } localDateEnd
            ? $"{startText} – {localDateEnd.ToString("d", CultureInfo.CurrentCulture)}"
            : startText;
    }

    private static string FormatInitials(string displayName)
        => string.Concat((displayName ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(2)
            .Select(part => char.ToUpper(part[0], CultureInfo.CurrentCulture)));

    private static DateTimeOffset ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(value),
        DateTimeKind.Local => new DateTimeOffset(value.ToUniversalTime()),
        _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
    };

    void IRecipient<SemanticIndexJobChanged>.Receive(SemanticIndexJobChanged message)
    {
        if (_intelligenceContext?.LocalAccountId != message.AccountId)
            return;

        DispatcherQueue.TryEnqueue(async () => await RefreshIntelligenceSnapshotAsync());
    }

    void IRecipient<WinoIntelligenceAccessChanged>.Receive(WinoIntelligenceAccessChanged message)
    {
        _intelligenceCoordinator.InvalidateAccess();
        DispatcherQueue.TryEnqueue(async () => await RefreshIntelligenceSnapshotAsync());
    }

    void IRecipient<IntelligenceMetadataChanged>.Receive(IntelligenceMetadataChanged message)
    {
        var mail = _currentMailItem?.MailCopy;
        if (mail?.AssignedAccount is null)
            return;

        var remoteId = RemoteMessageIdentity.TryCreate(mail);
        var matches = message.Scope == IntelligenceMetadataChangeScope.DatabaseReset ||
            (message.LocalAccountId == mail.AssignedAccount.Id &&
             (message.Scope == IntelligenceMetadataChangeScope.MailboxReset ||
              (remoteId is not null && message.RemoteMessageIds.Contains(remoteId))));
        if (matches)
            DispatcherQueue.TryEnqueue(async () => await RefreshCurrentIntelligenceMetadataAsync(message.Scope));
    }

    void IRecipient<IntelligenceVisibilityChanged>.Receive(IntelligenceVisibilityChanged message)
    {
        var account = _currentMailItem?.MailCopy?.AssignedAccount;
        if (account?.Id != message.LocalAccountId)
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            _currentMailItem?.RefreshIntelligenceTiles();
            IntelligenceHeader.IntelligenceTiles = _currentMailItem?.IntelligenceTiles;
            if (_currentMailItem?.MailCopy.IntelligenceMetadata is { } metadata)
                ApplyPassiveIntelligenceMetadata(metadata);
            else
                IntelligenceHeader.IntelligenceTiles = _currentMailItem?.IntelligenceTiles;
        });
    }


    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();

        DispatcherQueue.TryEnqueue(async () =>
        {
            _currentMailItem?.RefreshIntelligenceTiles();
            IntelligenceHeader.IntelligenceTiles = _currentMailItem?.IntelligenceTiles;
            await RefreshIntelligenceSnapshotAsync();
        });
    }

    private async Task RefreshCurrentIntelligenceMetadataAsync(IntelligenceMetadataChangeScope scope)
    {
        var mailItem = _currentMailItem;
        if (mailItem?.MailCopy is null)
            return;

        if (scope == IntelligenceMetadataChangeScope.Messages)
            await _mailService.HydrateIntelligenceMetadataAsync(new[] { mailItem.MailCopy });
        else
            mailItem.MailCopy.IntelligenceMetadata = null;

        mailItem.UpdateFrom(mailItem.MailCopy, MailCopyChangeFlags.IntelligenceMetadata);
        ShowIntelligenceHeaderImmediately(mailItem);
        await LoadIntelligenceContextAsync();
    }

    #endregion

}
