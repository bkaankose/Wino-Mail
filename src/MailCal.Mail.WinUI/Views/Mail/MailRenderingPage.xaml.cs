using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
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
using MailCal.Core.Domain;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Calendar;
using MailCal.Core.Domain.Models.Navigation;
using MailCal.Core.Domain.Models.Printing;
using MailCal.Editor;
using MailCal.Helpers;
using MailCal.Mail.Controls.Core.ContextFlyout;
using MailCal.Mail.ViewModels.Data;
using MailCal.Mail.ViewModels.Models;
using MailCal.Mail.WinUI;
using MailCal.Mail.WinUI.Extensions;
using MailCal.Mail.WinUI.Interfaces;
using MailCal.Mail.WinUI.Models;
using MailCal.Mail.WinUI.Services;
using MailCal.Messaging.Client.Mails;
using MailCal.Messaging.Client.Shell;
using MailCal.Messaging.UI;
using MailCal.Views.Abstract;
using WinRT;

namespace MailCal.Views.Mail;

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
        var windowManager = App.Current.Services.GetRequiredService<IMailCalWindowManager>();
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

        try
        {
            await UpdateEditorThemeAsync();
            if (IsStaleRender(renderVersion)) return;

            await UpdateReaderFontPropertiesAsync();
            if (IsStaleRender(renderVersion)) return;

            await RenderActiveContentAsync();
            if (IsStaleRender(renderVersion)) return;

            await UpdateAccessibleMailContextAsync();
        }
        finally
        {
            Interlocked.Decrement(ref _activeRenderCount);
        }
    }

    private bool IsStaleRender(int renderVersion) => renderVersion != Volatile.Read(ref _renderVersion);

    private async Task RenderActiveContentAsync()
    {
        var html = _currentRenderedHtml;
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

            try
            {
                await _readerSession.ClearAsync();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or COMException)
            {
                // A competing navigation already disposed this page's renderer (or its WebView2
                // closed). Clearing content that no longer exists needs no further work.
                Log.Debug(ex, "Skipped clearing the mail renderer because it was already disposed.");
            }
        }
    }

    private void ResetReaderContentState()
    {
        _currentRenderedHtml = string.Empty;
    }

    public async Task PrepareForIdleAsync()
    {
        // Clearing the renderer means this item is no longer being shown. The page stays alive for
        // a short grace period, so a re-selection must not be suppressed as an already-loaded item.
        _currentMailItem = null;

        await ClearRenderedContentAsync();

        try
        {
            await _readerSession.EnterIdleAsync();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or COMException)
        {
            // The renderer was torn down while the idle grace period elapsed.
            Log.Debug(ex, "Skipped idling the mail renderer because it was already disposed.");
        }
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

            ResetReaderContentState();

            _currentMailItem = mailItemViewModel;

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

    [DynamicWindowsRuntimeCast(typeof(HyperlinkButton))]
    private void InternetAddressClicked(object sender, RoutedEventArgs e)
    {
        // TODO: Popped out windows don't have xaml root assigned properly, therefore ShowAt will fail.
        if (sender is HyperlinkButton hyperlinkButton && !_isPoppedOut)
        {
            hyperlinkButton.ContextFlyout.ShowAt(hyperlinkButton);
        }
    }

    [DynamicWindowsRuntimeCast(typeof(HyperlinkButton))]
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

        MailCalContextFlyoutHelper.Show(target, args, (ContextFlyoutMenuEntry[])
        [
            new ContextFlyoutCommandEntry
            {
                Text = Translator.Buttons_Open,
                Icon = new ContextFlyoutIcon(MailCalIconGlyphs.GetGlyph(MailCalIconGlyph.Open)),
                Command = ViewModel.OpenAttachmentCommand,
                CommandParameter = attachment,
                AutomationId = "MailRenderingAttachmentOpen"
            },
            new ContextFlyoutCommandEntry
            {
                Text = Translator.Buttons_Save,
                Icon = new ContextFlyoutIcon(MailCalIconGlyphs.GetGlyph(MailCalIconGlyph.Save)),
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

    private MailItemViewModel? _currentMailItem;

}
