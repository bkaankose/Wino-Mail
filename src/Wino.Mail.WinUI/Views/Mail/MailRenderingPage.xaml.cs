using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Ai;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Printing;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.ContentProcessing;
using Wino.Mail.Controls.Core.IntelligenceHeader;
using Wino.Mail.Controls.Core.IntelligenceTileBar;
using Wino.Mail.Controls.Core.ContextFlyout;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Intelligence;
using Wino.Mail.ViewModels.Models;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Extensions;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Mail.Rendering;
using Wino.Mail.WinUI.Rendering;
using Wino.Mail.WinUI.Services;
using Wino.Core.Domain.Messaging.Client.Mails;
using Wino.Core.Domain.Messaging.Client.Shell;
using Wino.Mail.WinUI.Helpers;
using Wino.Mail.WinUI.Views.Abstract;
using Wino.Renderer;
using Wino.Renderer.Accessibility;
using Wino.Renderer.Policy;
using Wino.Renderer.Primitives;
using Wino.Renderer.Print;
using Wino.Renderer.Theme;
using Wino.Renderer.WinUI;
using Wino.Services.Models.Reader;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using WinRT;

namespace Wino.Mail.WinUI.Views.Mail;

public sealed partial class MailRenderingPage : MailRenderingPageAbstract,
    IPopoutClient,
    IReentryTarget,
    IMailMessageReader,
    IRecipient<ApplicationThemeChanged>
{
    private readonly IPreferencesService _preferencesService = App.Current.Services.GetService<IPreferencesService>()!;
    private readonly MailView _mailView;
    private readonly EngineReaderPresentation _readerPresentation;
    private readonly EngineMailMessageReader _messageReader;

    // Selections can overlap: one message's render may still be awaiting its intelligence context
    // when the next arrives. Only the newest render is allowed to touch reader state.
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
            _messageReader.IsReaderView = value;
        }
    }
    public event EventHandler<PopOutRequestedEventArgs>? PopOutRequested;
    public event EventHandler<PopoutHostActionRequestedEventArgs>? HostActionRequested;

    public MailRenderingPage()
    {
        InitializeComponent();

        _readerPresentation = new EngineReaderPresentation();
        _mailView = CreateMailView();
        MailRendererHost.Children.Add(_mailView);
        _messageReader = new EngineMailMessageReader(
            _mailView,
            App.Current.Services.GetRequiredService<MailEngineHost>(),
            _readerPresentation,
            ViewModel.SecureMimeContextFactory,
            () => ViewModel.ShouldStripLinkTrackingParameters);

        InitializeWinoIntelligenceHeader();

        ViewModel.PrintPresenter = CreatePrintPresenter();
        ViewModel.MessageReader = this;
        ViewModel.CloseRequested += ViewModel_CloseRequested;
        ViewModel.ComposeRequested += ViewModel_ComposeRequested;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    // The view is created here instead of in XAML: its public surface is engine types the XAML type table cannot describe.
    private MailView CreateMailView()
    {
        var view = new MailView();

        AutomationProperties.SetAutomationId(view, "MailMessageRenderer");
        ApplyMailViewAutomationText(view);

        view.LinkActivated += MailView_LinkActivated;
        view.LinkHoverChanged += MailView_LinkHoverChanged;
        view.ContextMenuRequested += MailView_ContextMenuRequested;

        return view;
    }

    private static void ApplyMailViewAutomationText(MailView view)
    {
        AutomationProperties.SetName(view, Translator.Reader_MessageBodyAutomationName);
        AutomationProperties.SetHelpText(view, Translator.Reader_MessageBodyAutomationHelpText);
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

        return new EngineMailPrintPresenter(
            App.Current.Services.GetRequiredService<MailEngineHost>(),
            () =>
            {
                var owner = windowManager.GetWindows()
                    .FirstOrDefault(window => XamlRoot != null && ReferenceEquals(window.Content?.XamlRoot, XamlRoot));
                return owner == null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(owner);
            },
            () => _messageReader.HasMessage ? _mailView.Reader : null,
            CreatePrintHeader);
    }

    private PrintHeader CreatePrintHeader()
    {
        var sender = string.IsNullOrWhiteSpace(ViewModel.FromName)
            ? ViewModel.FromAddress
            : string.IsNullOrWhiteSpace(ViewModel.FromAddress)
                ? ViewModel.FromName
                : $"{ViewModel.FromName} <{ViewModel.FromAddress}>";

        return new PrintHeader(
            string.IsNullOrWhiteSpace(ViewModel.Subject) ? Translator.MailItemNoSubject : ViewModel.Subject,
            sender,
            FormatPrintRecipients(ViewModel.ToItems),
            FormatPrintRecipients(ViewModel.CcItems),
            XamlHelpers.GetCreationDateString(ViewModel.CreationDate, _preferencesService.MailTimeFormatPreference));
    }

    private static string FormatPrintRecipients(IEnumerable<AccountContactViewModel> recipients)
        => string.Join("; ", recipients.Select(recipient =>
            string.IsNullOrWhiteSpace(recipient.Name) || string.Equals(recipient.Name, recipient.Address, StringComparison.OrdinalIgnoreCase)
                ? recipient.Address
                : $"{recipient.Name} <{recipient.Address}>"));

    async Task<MailRenderModel> IMailMessageReader.RenderAsync(MailMessageRenderRequest request, CancellationToken cancellationToken)
    {
        var renderVersion = Interlocked.Increment(ref _renderVersion);

        Interlocked.Increment(ref _activeRenderCount);

        Task? intelligenceLoadingTask = null;

        try
        {
            ApplyReaderPresentation();
            HideLinkTargetBar();
            UpdateAccessibleMailContext(string.Empty);

            // Read before the load starts: the engine's worker owns the message until the load ends.
            _currentRenderedHtml = EngineMailMessageReader.GetBodyHtml(request.Message);
            _intelligence.SetRenderedContent(_currentRenderedHtml);

            // The header becomes visible before cloud/local metadata finishes loading.
            intelligenceLoadingTask = LoadIntelligenceContextAsync();

            _messageReader.IsReaderView = IsReaderViewEnabled;
            var renderModel = await _messageReader.RenderAsync(request, cancellationToken);

            if (!IsStaleRender(renderVersion))
                UpdateAccessibleMailContext(renderModel.AccessibleText);

            await intelligenceLoadingTask;

            // An encrypted message has no readable body until the engine decrypted it, so the header gets it now.
            if (_currentRenderedHtml.Length == 0 && !IsStaleRender(renderVersion) &&
                _messageReader.GetLoadedBodyHtml() is { Length: > 0 } resolvedHtml)
            {
                _currentRenderedHtml = resolvedHtml;
                await LoadIntelligenceContextAsync();
            }

            return renderModel;
        }
        catch
        {
            if (intelligenceLoadingTask is not null)
                _ = ObserveIntelligenceLoadAsync(intelligenceLoadingTask);

            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _activeRenderCount);
        }
    }

    Task IMailMessageReader.ClearAsync() => ClearRenderedContentAsync();

    private static async Task ObserveIntelligenceLoadAsync(Task intelligenceLoadingTask)
    {
        try
        {
            await intelligenceLoadingTask;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Intelligence context loading ended with its abandoned render.");
        }
    }

    private bool IsStaleRender(int renderVersion) => renderVersion != Volatile.Read(ref _renderVersion);

    // The translation presenter asks for this when a translation is shown or removed.
    private Task RenderActiveContentAsync()
    {
        var html = _intelligence.ResolveActiveHtml(_currentRenderedHtml);

        return ReferenceEquals(html, _currentRenderedHtml)
            ? _messageReader.ShowOriginalAsync()
            : _messageReader.ShowHtmlVariantAsync(html);
    }

    private void UpdateAccessibleMailContext(string accessibleText)
    {
        var subject = string.IsNullOrWhiteSpace(ViewModel.Subject) ? Translator.MailItemNoSubject : ViewModel.Subject;
        var sender = string.IsNullOrWhiteSpace(ViewModel.FromName)
            ? ViewModel.FromAddress
            : string.IsNullOrWhiteSpace(ViewModel.FromAddress)
                ? ViewModel.FromName
                : $"{ViewModel.FromName} <{ViewModel.FromAddress}>";
        var creationDate = XamlHelpers.GetCreationDateString(ViewModel.CreationDate, _preferencesService.MailTimeFormatPreference);

        _mailView.AccessibilityContext = new AccessibilityContext
        {
            Subject = subject,
            Sender = sender,
            Date = creationDate,
            BodyName = Translator.Reader_MessageBodyAutomationName,
            PlainTextFallbackName = Translator.Reader_PlainTextFallbackAutomationName,
            FallbackText = accessibleText
        };
    }

    private async void MailView_LinkActivated(object? sender, LinkEventArgs e)
    {
        try
        {
            if (CreateResolvedLink(e.Link, e.Text) is { } link)
                (await ViewModel.OpenLinkAsync(link)).ThrowIfNotSucceeded();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open a link from the mail renderer.");
        }
    }

    private static ResolvedMailLink? CreateResolvedLink(ResolvedLink link, string? text)
    {
        switch (link.Kind)
        {
            case LinkKind.Web:
                if (!Uri.TryCreate(link.Effective, UriKind.Absolute, out var address))
                    return null;

                var original = !string.Equals(link.Original, link.Effective, StringComparison.Ordinal)
                    && Uri.TryCreate(link.Original, UriKind.Absolute, out var originalAddress)
                        ? originalAddress
                        : null;

                return new ResolvedMailLink(address, text, link.RemovedTrackingParameters, original);

            // The operating system routes these to the registered mail and phone handlers, as before.
            case LinkKind.Mailto:
            case LinkKind.Tel:
                return Uri.TryCreate(link.Original, UriKind.Absolute, out var protocolAddress)
                    ? new ResolvedMailLink(protocolAddress, text)
                    : null;

            default:
                return null;
        }
    }

    private void MailView_LinkHoverChanged(object? sender, LinkHoverEventArgs e)
    {
        var text = GetLinkTargetText(e.Link);

        ReaderLinkTargetText.Text = text;
        ReaderLinkTargetBar.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string GetLinkTargetText(ResolvedLink? link)
    {
        if (link is null || link.Kind == LinkKind.Blocked)
            return string.Empty;

        if (link.Protection == LinkProtection.OutlookSafeLinks && link.RedirectTarget is { Length: > 0 } redirectTarget)
        {
            return string.Format(Translator.Reader_ProtectedLinkFormat, redirectTarget);
        }

        var target = link.Kind == LinkKind.Web ? link.Effective : link.Original;
        return target ?? string.Empty;
    }

    private void MailView_ContextMenuRequested(object? sender, ContextMenuRequest request)
    {
        List<ContextFlyoutMenuEntry> entries = [];

        if (request.HasSelection)
        {
            entries.Add(new ContextFlyoutCommandEntry
            {
                Text = Translator.Buttons_Copy,
                Icon = new ContextFlyoutIcon(WinoIconGlyphs.GetGlyph(WinoIconGlyph.Copy)),
                Command = new RelayCommand(() => _mailView.CopySelection()),
                Shortcut = new ContextFlyoutShortcut("Ctrl+C", "C", Control: true),
                AutomationId = "MailRenderingCopy"
            });
        }

        if (!string.IsNullOrEmpty(request.LinkUrl))
        {
            entries.Add(new ContextFlyoutCommandEntry
            {
                Text = Translator.Reader_ContextMenu_CopyLink,
                Icon = new ContextFlyoutIcon(WinoIconGlyphs.GetGlyph(WinoIconGlyph.Link)),
                Command = ViewModel.CopyClipboardCommand,
                CommandParameter = request.LinkUrl,
                AutomationId = "MailRenderingCopyLink"
            });
        }

        if (request.OnImage && _mailView.Reader is { } reader && reader.TryGetImageAt(request.ViewPoint, out var image) && !image.Bytes.IsEmpty)
        {
            entries.Add(new ContextFlyoutCommandEntry
            {
                Text = Translator.Reader_ContextMenu_CopyImage,
                Icon = new ContextFlyoutIcon(WinoIconGlyphs.GetGlyph(WinoIconGlyph.Image)),
                Command = new AsyncRelayCommand(() => CopyImageToClipboardAsync(image)),
                AutomationId = "MailRenderingCopyImage"
            });
        }

        entries.Add(ContextFlyoutSeparatorEntry.Instance);
        entries.Add(new ContextFlyoutCommandEntry
        {
            Text = Translator.Reader_ContextMenu_SelectAll,
            Icon = new ContextFlyoutIcon(WinoIconGlyphs.GetGlyph(WinoIconGlyph.SelectAll)),
            Command = new RelayCommand(_mailView.SelectAll),
            Shortcut = new ContextFlyoutShortcut("Ctrl+A", "A", Control: true),
            AutomationId = "MailRenderingSelectAll"
        });

        WinoContextFlyoutHelper.Show(_mailView, new Windows.Foundation.Point(request.ViewPoint.X, request.ViewPoint.Y), entries);
    }

    private static async Task CopyImageToClipboardAsync(MailResource image)
    {
        try
        {
            // The clipboard reads the stream later, so it is handed over open.
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(image.Bytes.ToArray());
                await writer.StoreAsync();
                writer.DetachStream();
            }

            stream.Seek(0);

            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to copy an image from the mail renderer.");
        }
    }

    private async void PrivacyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PrivacyMenuItem item })
            return;

        PrivacyFlyout.Hide();

        try
        {
            await ViewModel.InvokePrivacyMenuItemCommand.ExecuteAsync(item);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "A privacy action from the reader failed.");
        }
    }

    private void PrivacyButton_Click(object sender, RoutedEventArgs e)
        => PrivacyFlyout.ShowAt(PrivacyButton);

    private void EditSenderRule_Click(object sender, RoutedEventArgs e)
        => PrivacyFlyout.ShowAt(PrivacyButton);

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private void LinkedItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Wino.Mail.ViewModels.Links.LinkedItemViewModel item)
            ViewModel.OpenLinkedItemCommand.Execute(item);
    }

    public Visibility GetPrivacyIconVisibility(PrivacyButtonState state, PrivacyButtonState visibleState)
        => state == visibleState ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GetPlainPrivacyIconVisibility(PrivacyButtonState state)
        => state is PrivacyButtonState.Plain or PrivacyButtonState.Count ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GetRemoteContentBarVisibility(bool isJunkBlocked, bool isSenderBlockedByRule)
        => isJunkBlocked || isSenderBlockedByRule ? Visibility.Visible : Visibility.Collapsed;

    public async Task ClearRenderedContentAsync()
    {
        if (Volatile.Read(ref _activeRenderCount) == 0)
        {
            ResetReaderContentState();
            HideLinkTargetBar();

            await _messageReader.ClearAsync();
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

        if (Volatile.Read(ref _activeRenderCount) != 0)
            return;

        ResetReaderContentState();
        HideLinkTargetBar();

        await _messageReader.EnterIdleAsync();
    }

    private void HideLinkTargetBar()
    {
        ReaderLinkTargetText.Text = string.Empty;
        ReaderLinkTargetBar.Visibility = Visibility.Collapsed;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // This page is moved, rather than discarded, when the reader is popped
        // out. Retain its renderer and render delegates for the new window.
        if (_isPoppedOut)
            return;

        base.OnNavigatedFrom(e);

        ViewModel.PrintPresenter = null;
        ViewModel.MessageReader = null;
        _intelligence.ClearContext();
        _intelligence.ForgetMailItem();
        _currentMailItem = null;
        _currentRenderedHtml = string.Empty;
        RendererCommandBar.PopOutClicked -= RendererCommandBar_PopOutClicked;
        HideLinkTargetBar();
        _messageReader.Detach();
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
    /// Shows another message without rebuilding the page, keeping its renderer alive.
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

            // Deliberately no clear of the renderer. The previous body stays loaded behind the
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
        _currentMailItem = e.Parameter as MailItemViewModel;
        _intelligence.BeginMailItem(_currentMailItem);
        _messageReader.Attach();
        ViewModel.PrintPresenter = CreatePrintPresenter();
        ViewModel.MessageReader = this;
        RendererCommandBar.PopOutClicked -= RendererCommandBar_PopOutClicked;
        RendererCommandBar.PopOutClicked += RendererCommandBar_PopOutClicked;

        base.OnNavigatedTo(e);

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

    public ElementTheme GetReaderElementTheme(bool isDark) => isDark ? ElementTheme.Dark : ElementTheme.Light;

    /// <summary>
    /// Pushes the reader theme, the surface behind the message, high contrast and the reader font to the view.
    /// A message that is on screen is laid out again only when one of them changed.
    /// </summary>
    [DynamicWindowsRuntimeCast(typeof(SolidColorBrush))]
    private void ApplyReaderPresentation()
    {
        _readerPresentation.IsDark = ViewModel.IsDarkWebviewRenderer;
        _readerPresentation.Method = ViewModel.ReaderDarkModeMethod == MailDarkModeMethod.Inverted
            ? DarkModeMethod.Inverted
            : DarkModeMethod.Adaptive;

        _readerPresentation.SetTypography(_preferencesService.ReaderFont, _preferencesService.ReaderFontSize);
        _readerPresentation.ApplyTo(_mailView);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ViewModel.IsDarkWebviewRenderer) or nameof(ViewModel.ReaderDarkModeMethod)))
            return;

        if (DispatcherQueue.HasThreadAccess)
            ApplyReaderPresentation();
        else
            DispatcherQueue.TryEnqueue(ApplyReaderPresentation);
    }

    private void MailRendererHost_ActualThemeChanged(FrameworkElement sender, object args) => ApplyReaderPresentation();

    private void MailRendererHost_Loaded(object sender, RoutedEventArgs e)
    {
        _readerPresentation.HighContrastChanged -= ReaderPresentation_HighContrastChanged;
        _readerPresentation.HighContrastChanged += ReaderPresentation_HighContrastChanged;
        _readerPresentation.StartWatching(MailRendererHost.XamlRoot);
        ApplyReaderPresentation();
    }

    private void MailRendererHost_Unloaded(object sender, RoutedEventArgs e)
    {
        _readerPresentation.StopWatching();
        _readerPresentation.HighContrastChanged -= ReaderPresentation_HighContrastChanged;
    }

    private void ReaderPresentation_HighContrastChanged(object? sender, EventArgs e) => ApplyReaderPresentation();

    void IRecipient<ApplicationThemeChanged>.Receive(ApplicationThemeChanged message)
    {
        DispatcherQueue.TryEnqueue(() => ViewModel.ApplyApplicationTheme(message.IsUnderlyingThemeDark));
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

    private void RendererCommandBar_EditorThemeToggled(object? sender, bool isDark)
        => ViewModel.RememberReaderTheme(isDark);

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
        ApplyMailViewAutomationText(_mailView);
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
