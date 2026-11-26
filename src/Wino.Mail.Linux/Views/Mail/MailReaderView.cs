using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Editor;
using Wino.Editor.WebKitGtk;
using Wino.Mail.Linux.Infrastructure;
#if DEBUG
using Wino.Mail.Linux.Infrastructure.Diagnostics;
#endif
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Mail;

internal enum ReaderHostKind { Pane, PopOut, File }

internal enum ReaderWidthState { Wide, Narrow, Compact }

/// <summary>
/// The reading pane (macOS layout of the Windows <c>MailRenderingPage</c>): the subject row with the commands at its end,
/// the sender block, the slots of the later reader packages, the body in one WebKitGTK session reused for every message,
/// and the attachment slot. Each feature lives in a <c>MailReaderView.&lt;Part&gt;.cs</c> and joins through the
/// <c>partial void Build…/Initialize…</c> hooks below; this file owns layout, render, theme, width and lifetime.
/// </summary>
internal sealed partial class MailReaderView : WinoView<MailRenderingPageViewModel>, IRenavigableView
{
    private const int NarrowWidth = 560;
    private const int CompactWidth = 430;

    private readonly IServiceProvider _provider;
    private readonly LinuxSenderPictureProvider _pictures;
    private readonly Gtk.Overlay _root = Gtk.Overlay.New();
    private readonly Gtk.DrawingArea _probe = Gtk.DrawingArea.New();
    private readonly Gtk.Box _page = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
    private readonly Gtk.Box _toolbar = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    private readonly Gtk.Label _subject = Gtk.Label.New(string.Empty);
    private readonly MailReaderHeader _header = new();
    private readonly ContactCardPopover _card;
    private readonly MailLoadingView _loading = new();
    private readonly Adw.Bin _commandSlot = Adw.Bin.New();
    private readonly Adw.Bin _linksSlot = Adw.Bin.New();
    private readonly Adw.Bin _intelligenceSlot = Adw.Bin.New();
    private readonly Adw.Bin _privacySlot = Adw.Bin.New();
    private readonly Adw.Bin _invitationSlot = Adw.Bin.New();
    private readonly Adw.Bin _attachmentSlot = Adw.Bin.New();
    private readonly Gtk.Overlay _bodyOverlay = Gtk.Overlay.New();
    private readonly Adw.Bin _bodyFrame = Adw.Bin.New();
    private readonly MainLoop.Coalescer _width;
    private readonly List<Action> _renavigating = [];
    private readonly List<Action> _rendering = [];
    private readonly List<Func<string, Task>> _rendered = [];
    private readonly List<Action> _cleared = [];
    private readonly List<Action<int, ReaderWidthState>> _widthChanged = [];
    private WebKitGtkHtmlMailReaderSession? _reader;
    private MailItemViewModel? _currentItem;
    private string _currentHtml = string.Empty;
    private int _probedWidth;
    private int _paneWidth;
    private ReaderWidthState _widthState;
    private bool _widthKnown;
    private int _crashRenders;
    private Func<string, Task<string>>? ResolveActiveHtml { get; set; }
    private EventHandler? _terminated;
    private EventHandler? _closeRequested;

    public MailReaderView(MailRenderingPageViewModel viewModel, WinoViewServices services, LinuxSenderPictureProvider pictures, IServiceProvider provider)
        : base(viewModel, services)
    {
        _provider = provider;
        _pictures = pictures;
        _card = new ContactCardPopover(pictures.Get);
        _width = new MainLoop.Coalescer(ApplyWidth);
        Bindings.Own(_width);
        Bindings.Own(_card);
        Bindings.Own(_header);
        Bindings.Own(_loading);

        _subject.AddCssClass(WinoCssClass.ReaderSubject);
        _subject.SetHexpand(true);
        _subject.SetXalign(0);
        _subject.SetSelectable(true);
        // A focusable selectable label selects all its text when a closing dialog returns the focus to it.
        _subject.SetFocusable(false);
        _subject.SetWrap(true);
        _subject.SetWrapMode(Pango.WrapMode.WordChar);
        _subject.SetLines(2);
        _subject.SetEllipsize(Pango.EllipsizeMode.End);
        _subject.SetWidthChars(8);
        _subject.SetMaxWidthChars(20);
        _commandSlot.SetValign(Gtk.Align.Center);
        _toolbar.AddCssClass(WinoCssClass.ReaderToolbar);
        _toolbar.Append(_subject);
        _toolbar.Append(_commandSlot);

        _bodyFrame.AddCssClass(WinoCssClass.ReaderBody);
        _bodyFrame.SetOverflow(Gtk.Overflow.Hidden);
        _bodyFrame.SetVexpand(true);
        _bodyOverlay.SetChild(_bodyFrame);
        _bodyOverlay.SetVexpand(true);

        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        content.Append(_header.Root);
        foreach (var slot in new[] { _linksSlot, _intelligenceSlot, _privacySlot, _invitationSlot })
        {
            slot.SetVisible(false);
            slot.SetMarginBottom(10);
            content.Append(slot);
        }
        content.Append(_bodyOverlay);
        content.Append(_attachmentSlot);
        var contentOverlay = Gtk.Overlay.New();
        contentOverlay.SetVexpand(true);
        contentOverlay.SetChild(content);
        contentOverlay.AddOverlay(_loading.Root);

        _page.AddCssClass(WinoCssClass.Reader);
        _page.Append(_toolbar);
        _page.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
        _page.Append(contentOverlay);

        _probe.SetHalign(Gtk.Align.Fill);
        _probe.SetValign(Gtk.Align.Start);
        _probe.SetContentHeight(1);
        _probe.SetCanTarget(false);
        _probe.SetCanFocus(false);
        _root.SetChild(_page);
        _root.AddOverlay(_probe);
        // resize runs during allocation: only the width is stored there, the layout changes on the next turn.
        Bindings.Own(GtkSignals.Connect<Gtk.DrawingArea, Gtk.DrawingArea.ResizeSignalArgs>(h => _probe.OnResize += h, h => _probe.OnResize -= h, (_, args) =>
        {
            _probedWidth = args.Width;
            _width.Request();
        }));
        ApplySlotMargins(ReaderWidthState.Wide);

        BuildCommandBar();
        BuildLinks();
        BuildIntelligence();
        BuildPrivacy();
        BuildInvitation();
        BuildAttachments();
    }

    public override Gtk.Widget Root => _root;

    public ReaderHostKind HostKind { get; set; }

    public async Task RenavigateAsync(object? parameter)
    {
        if (IsReleased) return;
#if DEBUG
        ReaderDiagnostics.Current = this;
        ReaderRenderTiming.Navigated(this);
#endif
        _currentItem = parameter as MailItemViewModel;
        foreach (var reset in _renavigating) reset();
        UpdateChips();
        _card.Close();
        _header.Recipients.ResetExpansion();
        _crashRenders = 0;
        await ViewModel.InitializeNavigationAsync(NavigationMode.New, parameter);
    }

    // OnNavigatedTo is not called: on this ViewModel it wraps InitializeNavigationAsync and would navigate a second time.
    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
#if DEBUG
        ReaderDiagnostics.Current = this;
#endif
        _currentItem = parameter as MailItemViewModel;
        var viewModel = ViewModel;
        var reader = _reader = new WebKitGtkHtmlMailReaderSession(Dispatcher);
        reader.Configure(viewModel.ExternalLauncher);
        reader.OperationFailed += (_, exception) => ReportError(exception);
        // Links go through the ViewModel: tracking parameters are stripped and the link is confirmed when asked.
        reader.LinkOpener = (uri, text) => viewModel.OpenLinkAsync(uri, text);
        _terminated = (_, _) => OnWebProcessEnded();
        reader.ContentProcessTerminated += _terminated;
        reader.View.SetVexpand(true);
        _bodyFrame.SetChild(reader.View);
        WinoA11y.Label(reader.View, Translator.Reader_MessageBodyAutomationName);
        WinoA11y.Description(reader.View, Translator.Reader_MessageBodyAutomationHelpText);

        viewModel.RenderHtmlAsyncFunc = RenderAsync;
        viewModel.ClearRenderedHtmlAsyncFunc = ClearAsync;
        _closeRequested = (_, _) => OnCloseRequested();
        viewModel.CloseRequested += _closeRequested;
        Bindings.Own(new ActionDisposable(() =>
        {
            if (_closeRequested is { } handler) viewModel.CloseRequested -= handler;
        }));

        Bind.Bind(nameof(MailRenderingPageViewModel.Subject), static vm => vm.Subject, subject =>
        {
            var text = string.IsNullOrWhiteSpace(subject) ? Translator.MailItemNoSubject : subject;
            _subject.SetText(text);
            _subject.SetTooltipText(text);
        });
        Bind.BindAny(static vm => (vm.FromName, vm.FromAddress), _ => UpdateSender());
        EventHandler<string?> pictureChanged = (_, address) =>
        {
            if (IsReleased) return;
            if (address is null || address == LinuxSenderPictureProvider.Normalize(ViewModel.FromAddress)) _header.SetSenderPicture(_pictures.Get(ViewModel.FromAddress));
        };
        _pictures.PictureChanged += pictureChanged;
        Bindings.Own(new ActionDisposable(() => _pictures.PictureChanged -= pictureChanged));
        Bind.BindAny(static vm => (vm.CreationDate, vm.PreferencesService.MailTimeFormatPreference),
            value => _header.SetDate(MailRowPresentation.FormatReaderDate(value.CreationDate, value.MailTimeFormatPreference)));
        Bind.Bind(nameof(MailRenderingPageViewModel.IsMailContentReady), static vm => vm.IsMailContentReady, ready =>
        {
            _loading.SetActive(!ready);
            // The loading treatment covers the reader below the subject row; the previous subject hides with it.
            _subject.SetOpacity(ready ? 1 : 0);
        });
        Bind.BindAny(static vm => (vm.IsIndetermineProgress, vm.CurrentDownloadPercentage), value => _loading.SetProgress(value.IsIndetermineProgress, value.CurrentDownloadPercentage));
        Bind.Bind(nameof(MailRenderingPageViewModel.CurrentRenderModel), static vm => vm.CurrentRenderModel, _ =>
        {
            _header.SetStatus(ViewModel.CanUnsubscribe, ViewModel.IsSmimeSigned, ViewModel.SmimeSignaturesValid, ViewModel.IsSmimeEncrypted);
            UpdateRecipients();
        });
        Bind.BindCollection(viewModel.ToItems, UpdateRecipients);
        Bind.BindCollection(viewModel.CcItems, UpdateRecipients);
        Bind.BindCollection(viewModel.BccItems, UpdateRecipients);
        Bind.BindAny(static vm => (vm.IsDarkWebviewRenderer, vm.ReaderDarkModeMethod), value => ApplyReaderTheme(value.IsDarkWebviewRenderer));
        // The reader theme comes from the ViewModel (it remembers a per-reader choice); the style manager only reports the app theme.
        Bindings.Own(GtkSignals.OnNotify(Adw.StyleManager.GetDefault(), "dark", () =>
        {
            if (!IsReleased) ViewModel.ApplyApplicationTheme(Adw.StyleManager.GetDefault().Dark);
        }));
        var preferences = viewModel.PreferencesService;
        EventHandler<string> preferenceChanged = (_, name) => RunOnUI(() => ApplyPreference(name));
        preferences.PreferenceChanged += preferenceChanged;
        Bindings.Own(new ActionDisposable(() => preferences.PreferenceChanged -= preferenceChanged));

        _header.SenderInvoked += anchor => _card.Show(anchor, ViewModel.FromName, ViewModel.FromAddress ?? string.Empty);
        _header.SenderCopyRequested += () => CopyAddress(ViewModel.FromAddress);
        _header.Recipients.ContactInvoked += (name, address, anchor) => _card.Show(anchor, name, address);
        _header.Recipients.CopyRequested += CopyAddress;
        _card.CopyRequested += CopyAddress;
        _header.UnsubscribeInvoked += () => Observe(ViewModel.UnsubscribeCommand.ExecuteAsync(null));
        _header.SignatureInvoked += () => Observe(ViewModel.ShowSmimeSigningCertificateInfoCommand.ExecuteAsync(null));
        UpdateChips();

        InitializeCommandBar();
        InitializeLinks();
        InitializeIntelligence();
        InitializePrivacy();
        InitializeInvitation();
        InitializeAttachments();
        InitializePrint();
        InitializePopOut();
        InitializeKeyboard();

        await reader.InitializeAsync();
        if (IsReleased) return;
        await ApplyThemeAsync(reader, ViewModel.IsDarkWebviewRenderer);
        if (IsReleased) return;
#if DEBUG
        ReaderRenderTiming.Navigated(this);
#endif
        await viewModel.InitializeNavigationAsync(mode, parameter);
    }

    protected override async Task DeactivateAsync()
    {
        ViewModel.RenderHtmlAsyncFunc = null;
        ViewModel.ClearRenderedHtmlAsyncFunc = null;
        _card.Close();
        ViewModel.OnNavigatedFrom(NavigationMode.Back, null);
        if (_reader is { } reader)
        {
            if (_terminated is { } terminated) reader.ContentProcessTerminated -= terminated;
            await reader.DisposeAsync();
        }
    }

    private void WhenRenavigating(Action reset) => _renavigating.Add(reset);

    private void WhenRendering(Action reset) => _rendering.Add(reset);

    /// <summary>Runs beside each new body render with its HTML (not for a re-render of the same body); the render awaits it last.</summary>
    private void WhenRendered(Func<string, Task> rendered) => _rendered.Add(rendered);

    private void WhenCleared(Action cleared) => _cleared.Add(cleared);

    private void WhenWidthChanged(Action<int, ReaderWidthState> apply)
    {
        _widthChanged.Add(apply);
        if (_widthKnown) apply(_paneWidth, _widthState);
    }

    private async Task RenderAsync(string html)
    {
        if (_reader is not { } reader || IsReleased) return;
#if DEBUG
        ReaderRenderTiming.RenderCalled();
#endif
        foreach (var reset in _rendering) reset();
        _currentHtml = html ?? string.Empty;
        var viewModel = ViewModel;
        var preferences = viewModel.PreferencesService;
        await reader.SetThemeAsync(viewModel.IsDarkWebviewRenderer);
        await reader.SetReaderTypographyAsync($"{preferences.ReaderFont}, sans-serif", preferences.ReaderFontSize);
        var rendered = _rendered.Count == 0 ? Task.CompletedTask : Task.WhenAll(_rendered.Select(apply => apply(_currentHtml)));
        await RenderActiveContentAsync();
#if DEBUG
        Observe(ReaderRenderTiming.WatchPaintAsync(reader.View));
#endif
        await reader.SetAccessibilityContextAsync(new ReaderAccessibilityContext(
            string.IsNullOrWhiteSpace(viewModel.Subject) ? Translator.MailItemNoSubject : viewModel.Subject,
            string.IsNullOrWhiteSpace(viewModel.FromName) ? viewModel.FromAddress : $"{viewModel.FromName} <{viewModel.FromAddress}>",
            MailRowPresentation.FormatReaderDate(viewModel.CreationDate, preferences.MailTimeFormatPreference),
            Translator.Reader_MessageBodyAutomationName,
            Translator.Reader_PlainTextFallbackAutomationName,
            viewModel.CurrentRenderModel?.AccessibleText ?? string.Empty));
        await rendered;
    }

    private async Task RenderActiveContentAsync()
    {
        if (_reader is not { } reader || IsReleased) return;
        var html = ResolveActiveHtml is { } resolve ? await resolve(_currentHtml) : _currentHtml;
        if (IsReleased) return;
        var viewModel = ViewModel;
        var options = viewModel.CurrentRenderModel?.MailRenderingOptions;
        var policy = options?.RemoteContent == RemoteContentMode.BlockAll ? RemoteContentPolicy.Blocked : RemoteContentPolicy.ImagesAndFontsAllowed;
        await reader.RenderAsync(new HtmlMailReaderRequest(string.IsNullOrEmpty(html) ? " " : html, policy,
            viewModel.IsReaderViewEnabled ? HtmlMailRenderMode.Readability : HtmlMailRenderMode.Original, options?.RenderPlaintextLinks ?? true));
    }

    private Task ClearAsync()
    {
        foreach (var reset in _rendering) reset();
        foreach (var cleared in _cleared) cleared();
        _currentHtml = string.Empty;
        return _reader is { } session && !IsReleased ? session.ClearAsync() : Task.CompletedTask;
    }

    private void OnWebProcessEnded()
    {
        if (IsReleased || _currentHtml.Length == 0) return;
        Serilog.Log.Warning("The reader's web process ended; rendering the message again.");
        if (++_crashRenders > 2)
        {
            ReportError(new InvalidOperationException("The web process ended repeatedly for this message."));
            return;
        }
        Observe(RenderActiveContentAsync());
    }

    private void OnCloseRequested()
    {
        if (IsReleased) return;
        var handled = false;
        HandleCloseRequested(ref handled);
        if (!handled && HostKind == ReaderHostKind.Pane)
            _provider.GetRequiredService<INavigationService>().Navigate(WinoPage.IdlePage, null, NavigationReferenceFrame.RenderingFrame);
    }

    // Typography restyles the loaded message; RenderPlaintextLinks is baked into the rendered HTML and
    // the ViewModel has no public re-render, so it applies to the next opened message as on Windows and macOS.
    private void ApplyPreference(string name)
    {
        if (_reader is not { } reader) return;
        if (name is not (nameof(IPreferencesService.ReaderFont) or nameof(IPreferencesService.ReaderFontSize))) return;
        var preferences = ViewModel.PreferencesService;
        Observe(reader.SetReaderTypographyAsync($"{preferences.ReaderFont}, sans-serif", preferences.ReaderFontSize));
    }

    private void ApplyReaderTheme(bool dark)
    {
        if (_reader is not { } reader || IsReleased) return;
        Observe(ApplyThemeAsync(reader, dark));
    }

    private async Task ApplyThemeAsync(WebKitGtkHtmlMailReaderSession reader, bool dark)
    {
        var method = ViewModel.ReaderDarkModeMethod == MailDarkModeMethod.Inverted ? HtmlMailDarkModeMethod.Inverted : HtmlMailDarkModeMethod.Adaptive;
        await ((IHtmlMailReaderSession)reader).SetDarkModeMethodAsync(method);
        await reader.SetThemeAsync(dark);
    }

    private void UpdateSender()
    {
        _header.SetSender(ViewModel.FromName, ViewModel.FromAddress);
        _header.SetSenderPicture(_pictures.Get(ViewModel.FromAddress));
    }

    private void UpdateRecipients()
    {
        var recipients = _header.Recipients;
        var changed = recipients.SetRecipients(MailRecipientRows.To, Translator.ComposerTo.Trim(), [.. ViewModel.ToItems]);
        // "Cc:" and "Bcc:" are the protocol labels; Windows and macOS show them untranslated too.
        changed |= recipients.SetRecipients(MailRecipientRows.Cc, "Cc:", [.. ViewModel.CcItems]);
        changed |= recipients.SetRecipients(MailRecipientRows.Bcc, "Bcc:", [.. ViewModel.BccItems]);
        if (changed) _card.Close();
    }

    private void UpdateChips()
    {
        var chips = _currentItem is { } item
            ? item.Categories.Take(4).Select(static category => new ReaderCategoryChip(category.Name ?? string.Empty, category.BackgroundColorHex, category.TextColorHex)).ToArray()
            : [];
        _header.SetCategories(chips);
    }

    private void CopyAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || IsReleased) return;
        Observe(ViewModel.CopyClipboardCommand.ExecuteAsync(address));
    }

    private void ApplyWidth()
    {
        if (IsReleased || _probedWidth <= 0) return;
        var width = _probedWidth;
        var state = width < CompactWidth ? ReaderWidthState.Compact : width < NarrowWidth ? ReaderWidthState.Narrow : ReaderWidthState.Wide;
        var stateChanged = !_widthKnown || state != _widthState;
        if (_widthKnown && width == _paneWidth && !stateChanged) return;
        _widthKnown = true;
        _paneWidth = width;
        _widthState = state;
        if (stateChanged)
        {
            if (state == ReaderWidthState.Wide) _page.RemoveCssClass(WinoCssClass.ReaderNarrow);
            else _page.AddCssClass(WinoCssClass.ReaderNarrow);
            if (state == ReaderWidthState.Compact) _page.AddCssClass(WinoCssClass.ReaderCompact);
            else _page.RemoveCssClass(WinoCssClass.ReaderCompact);
            _header.SetCompact(state == ReaderWidthState.Compact);
            ApplySlotMargins(state);
        }
        foreach (var apply in _widthChanged) apply(width, state);
    }

    private void ApplySlotMargins(ReaderWidthState state)
    {
        var inset = state == ReaderWidthState.Wide ? 20 : 14;
        foreach (var slot in new[] { _linksSlot, _intelligenceSlot, _privacySlot, _invitationSlot })
        {
            slot.SetMarginStart(inset);
            slot.SetMarginEnd(inset);
        }
    }

    partial void BuildCommandBar();
    partial void InitializeCommandBar();
    partial void BuildAttachments();
    partial void InitializeAttachments();
    partial void BuildPrivacy();
    partial void InitializePrivacy();
    partial void BuildInvitation();
    partial void InitializeInvitation();
    partial void BuildLinks();
    partial void InitializeLinks();
    partial void BuildIntelligence();
    partial void InitializeIntelligence();
    partial void InitializePrint();
    partial void InitializePopOut();
    partial void InitializeKeyboard();
    partial void HandleCloseRequested(ref bool handled);

#if DEBUG
    private readonly List<Func<string>> _describing = [];

    private void WhenDescribing(Func<string> describe) => _describing.Add(describe);

    internal WebKit.WebView? DebugWebView => _reader?.View;
    internal MailReaderHeader DebugHeader => _header;
    internal ContactCardPopover DebugCard => _card;
    internal int DebugSampleWidth => _paneWidth;

    internal void DebugShowSenderCard() => _card.Show(_header.SenderAnchor, ViewModel.FromName, ViewModel.FromAddress ?? string.Empty);

    internal string Describe()
    {
        _root.Measure(Gtk.Orientation.Horizontal, -1, out var minimum, out _, out _, out _);
        var text = new System.Text.StringBuilder();
        text.Append($"host={HostKind} ready={ViewModel.IsMailContentReady} subject=\"{_subject.GetText()}\" width={_paneWidth} state={_widthState} minWidth={minimum} ")
            .Append($"dark={ViewModel.IsDarkWebviewRenderer} readerView={ViewModel.IsReaderViewEnabled} html={_currentHtml.Length} session={_reader?.IsReady == true} loading={_loading.IsActive}")
            .AppendLine();
        text.AppendLine(_header.Describe());
        text.AppendLine(_header.Recipients.Describe());
        text.AppendLine($"card={_card.IsShown}");
        foreach (var describe in _describing) text.AppendLine(describe());
        return text.ToString();
    }
#endif
}
