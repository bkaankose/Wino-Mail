using System.Collections.Specialized;
using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using CoreGraphics;
using Microsoft.Extensions.DependencyInjection;
using WebKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Models.Navigation;
using Wino.Editor;
using Wino.Editor.WebKit;
using AppKitHtmlMailReaderSession = Wino.Editor.WebKit.WebKitHtmlMailReaderSession;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.MailList;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Mails;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;
using Wino.Core.Domain.Presentation;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// Reading pane message view (Windows MailRenderingPage, in the Mac layout): the subject row (the
/// subject, then the icon-only commands Reply, Reply all, Forward | Archive, Delete, Move, Flag, Mark
/// read, Pop out, More at the trailing edge), the sender block (40pt avatar; name, address and the
/// To, Cc and Bcc rows; the date with Unsubscribe and the S/MIME indicators at the top right),
/// category chips, the Wino Intelligence header, the
/// remote-image banner, the HTML body in a card through the shared reader engine, the attachment
/// strip with Save all, and the loading skeleton. Print and Save as PDF go through
/// <see cref="MacMailPrintPresenter"/> while the reader is active.
/// The controller is reused across selections; <see cref="RenavigateAsync"/> loads the next message.
/// Pop out moves it into its own window (MailRenderingPageViewController.PopOut.cs).
/// The Wino Intelligence header lives in MailRenderingPageViewController.Intelligence.cs.
/// </summary>
public sealed partial class MailRenderingPageViewController(MailRenderingPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger, IServiceProvider services)
    : WinoViewController<MailRenderingPageViewModel>(viewModel, dispatcher, logger)
{
    private readonly IServiceProvider _services = services;
    private readonly MacSenderPictureProvider _pictures = services.GetRequiredService<MacSenderPictureProvider>();
    private MailReaderCommandBar _commandBar = null!;
    private NSTextField _subject = null!;
    private WinoContactPicture _avatar = null!;
    private NSTextField _senderName = null!;
    private NSTextField _senderAddress = null!;
    private NSTextField _date = null!;
    private MailReaderStatusRow _statusRow = null!;
    private NSStackView _chips = null!;
    private NSView _intelligenceHost = null!;
    private WinoInfoBar _imageBanner = null!;
    private WKWebView _webView = null!;
    private readonly ReaderLinkTargetBar _linkTarget = new();
    private AppKitHtmlMailReaderSession? _reader;
    private NSStackView _attachmentTiles = null!;
    private NSView _attachmentStrip = null!;
    private NSButton _saveAllButton = null!;
    private MailLoadingView _loading = null!;
    private MailItemViewModel? _currentItem;
    private string _currentRenderedHtml = string.Empty;
    private bool _dark;
    private bool _disposedReader;
    private bool _readerViewEnabled;
    private NSStackView _header = null!;
    private NSStackView _stripRow = null!;
    private readonly List<NSLayoutConstraint> _headerWidths = new();
    private NSLayoutConstraint _subjectLeading = null!;
    private NSLayoutConstraint _bodyLeading = null!;
    private NSLayoutConstraint _bodyTrailing = null!;
    private NSLayoutConstraint _bodyBottom = null!;
    private ReaderWidthState? _widthState;

    /// <summary>Reader widths: Narrow tightens the margins; Compact also drops the avatar.</summary>
    private enum ReaderWidthState { Wide, Narrow, Compact }

    private const double NarrowWidth = 560;
    private const double CompactWidth = 430;
    private const double SubjectMinimumWidth = 160;

    public override void LoadView()
    {
        var root = new NSView();

        // ---- Subject row: the subject, then the commands at the trailing edge ----
        _commandBar = new MailReaderCommandBar { IconOnly = true, HugsCommands = true };
        // IconOnly hands the toolbar role to a host; here the bar is the reader's toolbar itself.
        _commandBar.AccessibilityElement = true;
        _subject = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(16, NSFontWeight.Semibold), WinoStyle.PrimaryText, 2);
        _subject.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _subject.Selectable = true;
        _subject.Cell.TruncatesLastVisibleLine = true;
        var topRow = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        var topSeparator = new WinoSurfaceView { Fill = WinoStyle.ZoneStroke };
        topRow.AddSubview(_subject);
        topRow.AddSubview(_commandBar);
        topRow.AddSubview(topSeparator);
        _subjectLeading = _subject.LeadingAnchor.ConstraintEqualTo(topRow.LeadingAnchor, 24);
        // The subject keeps a readable width; below it the commands fold into More instead.
        var subjectMinimum = _subject.WidthAnchor.ConstraintGreaterThanOrEqualTo((nfloat)SubjectMinimumWidth);
        subjectMinimum.Priority = 760;
        var rowHeight = topRow.HeightAnchor.ConstraintEqualTo(50);
        rowHeight.Priority = 250;
        NSLayoutConstraint.ActivateConstraints(
        [
            _subjectLeading,
            subjectMinimum,
            _subject.CenterYAnchor.ConstraintEqualTo(topRow.CenterYAnchor),
            _subject.TopAnchor.ConstraintGreaterThanOrEqualTo(topRow.TopAnchor, 10),
            _subject.BottomAnchor.ConstraintLessThanOrEqualTo(topRow.BottomAnchor, -10),
            _commandBar.LeadingAnchor.ConstraintGreaterThanOrEqualTo(_subject.TrailingAnchor, 12),
            _commandBar.TrailingAnchor.ConstraintEqualTo(topRow.TrailingAnchor),
            _commandBar.CenterYAnchor.ConstraintEqualTo(topRow.CenterYAnchor),
            _commandBar.TopAnchor.ConstraintGreaterThanOrEqualTo(topRow.TopAnchor),
            _commandBar.BottomAnchor.ConstraintLessThanOrEqualTo(topRow.BottomAnchor),
            topRow.HeightAnchor.ConstraintGreaterThanOrEqualTo(50),
            rowHeight,
            topSeparator.LeadingAnchor.ConstraintEqualTo(topRow.LeadingAnchor),
            topSeparator.TrailingAnchor.ConstraintEqualTo(topRow.TrailingAnchor),
            topSeparator.BottomAnchor.ConstraintEqualTo(topRow.BottomAnchor),
            topSeparator.HeightAnchor.ConstraintEqualTo(1)
        ]);

        // ---- Sender block: avatar | name, address and the To, Cc, Bcc rows | date and status ----
        _avatar = new WinoContactPicture(40);
        // The sender line is one link that opens the sender's contact card (AttachSenderLink).
        _senderName = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong);
        _senderAddress = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText);
        _senderAddress.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        var nameLine = WinoLayout.HStack(6, _senderName, _senderAddress);
        nameLine.Alignment = NSLayoutAttribute.FirstBaseline;
        AttachSenderLink(nameLine);
        var recipientRows = BuildRecipientRows();
        var senderText = WinoLayout.VStack(3, nameLine, recipientRows);
        // NSStackView sizes itself by its own hugging priority: the column takes the row's free width.
        senderText.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        senderText.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        recipientRows.WidthAnchor.ConstraintEqualTo(senderText.WidthAnchor).Active = true;

        // Date, then Unsubscribe and the S/MIME indicators, at the top right like the Windows header.
        _date = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
        _date.SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);
        _statusRow = new MailReaderStatusRow();
        _statusRow.UnsubscribeInvoked += (_, _) => Observe(ViewModel.UnsubscribeCommand.ExecuteAsync(null));
        _statusRow.SignatureInvoked += (_, _) => Observe(ViewModel.ShowSmimeSigningCertificateInfoCommand.ExecuteAsync(null));
        var details = WinoLayout.VStack(4, _date, _statusRow);
        details.Alignment = NSLayoutAttribute.Trailing;
        details.SetHuggingPriority(750, NSLayoutConstraintOrientation.Horizontal);

        var senderRow = WinoLayout.HStack(12, _avatar, senderText, details);
        senderRow.Alignment = NSLayoutAttribute.Top;
        senderRow.DetachesHiddenViews = true;

        _chips = WinoLayout.HStack(8);
        _chips.Distribution = NSStackViewDistribution.Fill;
        _chips.SetHuggingPriority(750, NSLayoutConstraintOrientation.Horizontal);
        _chips.Hidden = true;
        _chips.SetClippingResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);

        _imageBanner = new WinoInfoBar(WinoInfoBarSeverity.Warning, null, Translator.ImageRenderingDisabled)
        {
            ActionTitle = Translator.Buttons_EnableImageRendering,
            // Windows leaves this InfoBar at the default IsClosable="True".
            IsClosable = true,
            Hidden = true
        };
        _imageBanner.ActionInvoked += (_, _) => Observe(ViewModel.ForceImageLoadingCommand.ExecuteAsync(null));

        // Hosts the Wino Intelligence header; hidden until the intelligence partial fills it.
        _intelligenceHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        BuildIntelligenceHeader(_intelligenceHost);

        var header = _header = WinoLayout.VStack(12, senderRow, _chips, _intelligenceHost, _imageBanner);
        header.EdgeInsets = new NSEdgeInsets(14, 24, 10, 24);
        foreach (var view in new NSView[] { senderRow, _intelligenceHost, _imageBanner })
            _headerWidths.Add(view.WidthAnchor.ConstraintEqualTo(header.WidthAnchor, 1, -48));
        // The tile row hugs its chips from the leading edge; it is only capped (and clips) at the header width.
        _headerWidths.Add(_chips.WidthAnchor.ConstraintLessThanOrEqualTo(header.WidthAnchor, 1, -48));
        NSLayoutConstraint.ActivateConstraints(_headerWidths.ToArray());

        // ---- Body card ----
        _webView = new WKWebView(CGRect.Empty, new WKWebViewConfiguration()) { TranslatesAutoresizingMaskIntoConstraints = false };
        _webView.SetValueForKey(Foundation.NSNumber.FromBoolean(false), new Foundation.NSString("drawsBackground"));
        WinoAccessibility.Label(_webView, Translator.Reader_MessageBodyAutomationName);
        WinoAccessibility.Help(_webView, Translator.Reader_MessageBodyAutomationHelpText);
        var card = new WinoSurfaceView
        {
            CornerRadius = WinoStyle.GroupRadius,
            Fill = WinoStyle.Dynamic(NSColor.White, WinoStyle.Hex(0x1F1F22)),
            Stroke = WinoStyle.ZoneStroke
        };
        WinoLayout.Fill(_webView, card, 1, 1, 1, 1);
        _linkTarget.Attach(card);
        var body = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        card.TranslatesAutoresizingMaskIntoConstraints = false;
        body.AddSubview(card);
        _bodyLeading = card.LeadingAnchor.ConstraintEqualTo(body.LeadingAnchor, 24);
        _bodyTrailing = card.TrailingAnchor.ConstraintEqualTo(body.TrailingAnchor, -24);
        _bodyBottom = card.BottomAnchor.ConstraintEqualTo(body.BottomAnchor, -24);
        NSLayoutConstraint.ActivateConstraints([_bodyLeading, _bodyTrailing, _bodyBottom, card.TopAnchor.ConstraintEqualTo(body.TopAnchor, 6)]);

        // ---- Attachments ----
        _attachmentTiles = WinoLayout.HStack(10);
        var tileScroll = new NSScrollView
        {
            DocumentView = _attachmentTiles,
            HasHorizontalScroller = true,
            AutohidesScrollers = true,
            DrawsBackground = false,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        tileScroll.HeightAnchor.ConstraintEqualTo(54).Active = true;
        tileScroll.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _attachmentTiles.TopAnchor.ConstraintEqualTo(tileScroll.ContentView.TopAnchor).Active = true;
        _attachmentTiles.LeadingAnchor.ConstraintEqualTo(tileScroll.ContentView.LeadingAnchor).Active = true;
        _saveAllButton = new NSButton { Title = Translator.Reader_SaveAllAttachmentButtonText, BezelStyle = NSBezelStyle.Rounded, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false };
        _saveAllButton.Image = WinoIcons.Image(WinoIconGlyph.ArrowDownload, 12, null, Translator.Reader_SaveAllAttachmentButtonText);
        _saveAllButton.ImagePosition = NSCellImagePosition.ImageLeading;
        _saveAllButton.Activated += (_, _) => Observe(ViewModel.SaveAllAttachmentsCommand.ExecuteAsync(null));
        var stripRow = _stripRow = WinoLayout.HStack(10, tileScroll, _saveAllButton);
        stripRow.EdgeInsets = new NSEdgeInsets(0, 24, 14, 24);
        _attachmentStrip = stripRow;
        _attachmentStrip.Hidden = true;

        var stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Spacing = 0,
            Distribution = NSStackViewDistribution.Fill,
            Alignment = NSLayoutAttribute.Leading,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        foreach (var view in new NSView[] { topRow, header, body, _attachmentStrip })
        {
            stack.AddArrangedSubview(view);
            view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        }
        body.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        WinoLayout.Fill(stack, root);

        _loading = new MailLoadingView();
        var loadingHost = new WinoSurfaceView { Fill = WinoStyle.ZoneFill, Hidden = true };
        WinoLayout.Fill(_loading, loadingHost);
        root.AddSubview(loadingHost);
        NSLayoutConstraint.ActivateConstraints(
        [
            loadingHost.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            loadingHost.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            loadingHost.TopAnchor.ConstraintEqualTo(topRow.BottomAnchor),
            loadingHost.BottomAnchor.ConstraintEqualTo(root.BottomAnchor)
        ]);

        UpdateCommandBar();
        View = root;
#if DEBUG
        MacDebugBridge.Register("readerbar", _ => Task.FromResult(_commandBar.Dump() + " menu=" + ViewModel.MenuItems.Count));
        RegisterReaderDebugCommands();
        RegisterPopOutDebugCommands();
#endif
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        _currentItem = parameter as MailItemViewModel;
        IntelligenceBeginItem(_currentItem);
        _reader = new AppKitHtmlMailReaderSession(_webView);
        _reader.Configure(ViewModel.ExternalLauncher);
        _reader.OperationFailed += (_, exception) => ReportError(exception);
        _reader.LinkHovered += (_, args) => _linkTarget.Show(args);
        _reader.NavigationRequested += (_, _) => _linkTarget.Clear();
        _commandBar.ShowsLabels = ViewModel.PreferencesService.IsShowActionLabelsEnabled;
        ViewModel.PreferencesService.PreferenceChanged -= PreferenceChanged;
        ViewModel.PreferencesService.PreferenceChanged += PreferenceChanged;
        _dark = IsDarkAppearance();
        ViewModel.IsDarkWebviewRenderer = _dark;
        ViewModel.RenderHtmlAsyncFunc = RenderAsync;
        ViewModel.ClearRenderedHtmlAsyncFunc = ClearAsync;
        ViewModel.PrintPresenter = new MacMailPrintPresenter(() => _disposedReader ? null : _webView, Dispatcher);
        ViewModel.CloseRequested += CloseRequested;
        ViewModel.ComposeRequested += ComposeRequested;

        Bind(nameof(ViewModel.Subject), vm => vm.Subject, subject =>
        {
            _subject.StringValue = string.IsNullOrWhiteSpace(subject) ? Translator.MailItemNoSubject : subject;
            _subject.ToolTip = _subject.StringValue;
            UpdatePopOutTitle(subject);
        });
        Bind(nameof(ViewModel.FromName), vm => vm.FromName, _ => UpdateSender());
        Bind(nameof(ViewModel.FromAddress), vm => vm.FromAddress, _ => UpdateSender());
        EventHandler<string?> pictureChanged = (_, address) =>
        {
            if (address is null || string.Equals(ViewModel.FromAddress?.Trim(), address, StringComparison.OrdinalIgnoreCase)) UpdateSender();
        };
        _pictures.PictureChanged += pictureChanged;
        Bindings.Own(new ActionDisposable(() => _pictures.PictureChanged -= pictureChanged));
        Bind(nameof(ViewModel.CreationDate), vm => vm.CreationDate, _ => UpdateRecipients());
        Bind(nameof(ViewModel.MenuItems), vm => vm.MenuItems, _ => UpdateCommandBar());
        Bind(nameof(ViewModel.IsMailContentReady), vm => vm.IsMailContentReady, ready =>
        {
            if (_loading.Superview is { } host) host.Hidden = ready;
            // The loading treatment covers the reader below the subject row; the previous subject hides with it.
            _subject.AlphaValue = ready ? 1 : 0;
            _loading.IsAnimating = !ready;
        });
        Bind(nameof(ViewModel.CurrentRenderModel), vm => vm.CurrentRenderModel, _ =>
        {
            _imageBanner.Hidden = !ViewModel.IsImageRenderingDisabled;
            _statusRow.Update(ViewModel.CanUnsubscribe, ViewModel.IsSmimeSigned, ViewModel.SmimeSignaturesValid, ViewModel.IsSmimeEncrypted);
            UpdateRecipients();
            UpdateAttachments();
        });
        ObserveCollection(ViewModel.ToItems);
        ObserveCollection(ViewModel.CcItems);
        ObserveCollection(ViewModel.BccItems);
        BindRecipientAccent();
        ObserveCollection(ViewModel.DisplayedAttachments, attachments: true);
        ObserveCollection(ViewModel.Attachments, attachments: true);
        UpdateChips();

        await _reader.InitializeAsync();
        await _reader.SetThemeAsync(_dark);
        await ViewModel.InitializeNavigationAsync(mode, parameter!);
    }

    /// <summary>Loads another message into this reader without recreating the web view.</summary>
    public async Task RenavigateAsync(object? parameter)
    {
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            _currentItem = parameter as MailItemViewModel;
            IntelligenceBeginItem(_currentItem);
            UpdateChips();
            CloseContactCard();
            _recipientRows.ResetExpansion();
        });
        await ViewModel.InitializeNavigationAsync(NavigationMode.New, parameter!);
    }

    private void Bind<TValue>(string property, Func<MailRenderingPageViewModel, TValue> read, Action<TValue> apply)
        => Bindings.Own(new PropertyBinding<MailRenderingPageViewModel, TValue>(ViewModel, property, read, apply, Dispatcher, ReportError));

    private void ObserveCollection(INotifyCollectionChanged collection, bool attachments = false)
    {
        NotifyCollectionChangedEventHandler handler = (_, _) => _ = Dispatcher.ExecuteOnUIThread(() =>
        {
            if (Bindings.IsDisposed) return;
            if (attachments) UpdateAttachments();
            else UpdateRecipients();
        });
        collection.CollectionChanged += handler;
        Bindings.Own(new ActionDisposable(() => collection.CollectionChanged -= handler));
    }

    private void UpdateSender()
    {
        var name = string.IsNullOrWhiteSpace(ViewModel.FromName) ? ViewModel.FromAddress : ViewModel.FromName;
        _senderName.StringValue = name ?? string.Empty;
        _senderAddress.StringValue = string.Equals(name, ViewModel.FromAddress, StringComparison.OrdinalIgnoreCase) ? string.Empty : ViewModel.FromAddress ?? string.Empty;
        _avatar.SetIdentity(name, ViewModel.FromAddress);
        _avatar.Image = _pictures.Get(ViewModel.FromAddress);
    }

    private void UpdateRecipients()
    {
        UpdateRecipientRows();
        _date.StringValue = MailRowMapper.FormatReaderDate(ViewModel.CreationDate, ViewModel.PreferencesService.MailTimeFormatPreference);
    }

    /// <summary>Category chips under the sender row. The intelligence tiles live in the Wino Intelligence header.</summary>
    private void UpdateChips()
    {
        foreach (var view in _chips.ArrangedSubviews)
        {
            _chips.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
            view.Dispose();
        }
        if (_currentItem is { } item)
        {
            foreach (var category in item.Categories.Take(4))
            {
                var background = WinoStyle.FromHexString(category.BackgroundColorHex) ?? WinoStyle.SubtleFill;
                var chip = new WinoChipView(22) { CornerRadius = 4, Text = category.Name ?? string.Empty, Fill = background, MaxTextWidth = 180 };
                chip.TextColor = WinoStyle.FromHexString(category.TextColorHex) ?? (WinoStyle.FromHexString(category.BackgroundColorHex) is { } solid ? WinoMailRowView.ReadableText(solid) : WinoStyle.SecondaryText);
                _chips.AddArrangedSubview(chip);
            }
        }
        _chips.Hidden = _chips.ArrangedSubviews.Length == 0;
    }

    // ---- Command bar ----

    /// <summary>The primary commands and the More menu from the shared <see cref="MailReaderCommandLayout"/>.</summary>
    private void UpdateCommandBar()
    {
        var plan = MailReaderCommandLayout.Build(ViewModel.MenuItems, _currentItem is not null, offerMove: false);
        var commands = new List<MailReaderCommand?>();
        foreach (var slot in plan.Primary)
        {
            if (slot is null) { commands.Add(null); continue; }
            var captured = slot.Item;
            commands.Add(new MailReaderCommand(MailOperationDisplay.GetGlyph(slot.Operation), slot.Title,
                captured is null ? static () => { } : () => Observe(ViewModel.OperationClickedCommand.ExecuteAsync(captured)), slot.IsEnabled));
        }
        // A loaded message gets the pop-out command; a saved message file and the placeholder set do not.
        if (plan.Kind == MailReaderCommandPlanKind.Message)
            AppendPopOutCommand(commands);
        var overflow = plan.Overflow;
        _commandBar.SetCommands(commands, overflow.Count == 0 ? null : () => BuildMoreMenu(overflow));
    }

    private NSMenu BuildMoreMenu(IReadOnlyList<MailOperationMenuItem> items)
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        bool previousSeparator = true;
        foreach (var item in items)
        {
            if (item.Operation == MailOperation.Seperator)
            {
                if (!previousSeparator) menu.AddItem(NSMenuItem.SeparatorItem);
                previousSeparator = true;
                continue;
            }
            var title = MailOperationDisplay.GetTitle(item.Operation);
            if (string.IsNullOrEmpty(title)) continue;
            var captured = item;
            var menuItem = new NSMenuItem(title, (_, _) => Observe(ViewModel.OperationClickedCommand.ExecuteAsync(captured))) { Enabled = item.IsEnabled };
            menuItem.Image = MailOperationPresentation.Image(item.Operation);
            menu.AddItem(menuItem);
            previousSeparator = false;
        }
        if (!previousSeparator) menu.AddItem(NSMenuItem.SeparatorItem);
        // Windows OperationCommandBar: the Reader View toggle (simplified Readability layout) and the editor theme toggle.
        var readerView = new NSMenuItem(Translator.Reader_ReaderView, (_, _) => ToggleReaderView())
        {
            State = _readerViewEnabled ? NSCellStateValue.On : NSCellStateValue.Off,
            Enabled = !string.IsNullOrWhiteSpace(_currentRenderedHtml),
            Image = WinoIcons.Image(WinoIconGlyph.Document, 16)
        };
        menu.AddItem(readerView);
        var theme = new NSMenuItem(_dark ? Translator.Composer_LightTheme : Translator.Composer_DarkTheme, (_, _) => ToggleTheme())
        {
            Image = WinoIcons.Image(_dark ? WinoIconGlyph.LightEditor : WinoIconGlyph.DarkEditor, 16)
        };
        menu.AddItem(theme);
        return menu;
    }

    /// <summary>Re-renders the current message (or its translation) in the other layout; kept for this reader's lifetime.</summary>
    private void ToggleReaderView()
    {
        if (_reader is null || _disposedReader) return;
        _readerViewEnabled = !_readerViewEnabled;
        if (!string.IsNullOrWhiteSpace(_currentRenderedHtml)) Observe(RenderActiveContentAsync());
    }

    private void ToggleTheme()
    {
        if (_reader is null || _disposedReader) return;
        _dark = !_dark;
        ViewModel.IsDarkWebviewRenderer = _dark;
        Observe(_reader.SetThemeAsync(_dark));
    }

    // ---- Attachments ----

    private void UpdateAttachments()
    {
        foreach (var view in _attachmentTiles.ArrangedSubviews)
        {
            _attachmentTiles.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
            view.Dispose();
        }
        var attachments = ViewModel.Attachments.ToArray();
        foreach (var attachment in attachments) _attachmentTiles.AddArrangedSubview(CreateTile(attachment));
        _attachmentStrip.Hidden = attachments.Length == 0;
        _saveAllButton.Hidden = attachments.Length < 2;
    }

    private NSView CreateTile(MailAttachmentViewModel attachment)
    {
        var extension = Path.GetExtension(attachment.FileName ?? string.Empty).TrimStart('.').ToUpperInvariant();
        if (extension.Length > 4) extension = extension[..4];
        var icon = new WinoSurfaceView { Fill = ExtensionColor(extension), CornerRadius = 6 };
        WinoLayout.Size(icon, 34, 34);
        var iconLabel = WinoStyle.Label(extension, NSFont.SystemFontOfSize(10, NSFontWeight.Bold), NSColor.White);
        icon.AddSubview(iconLabel);
        iconLabel.CenterXAnchor.ConstraintEqualTo(icon.CenterXAnchor).Active = true;
        iconLabel.CenterYAnchor.ConstraintEqualTo(icon.CenterYAnchor).Active = true;
        var name = WinoStyle.Label(attachment.FileName, NSFont.SystemFontOfSize(12, NSFontWeight.Semibold));
        name.WidthAnchor.ConstraintLessThanOrEqualTo(180).Active = true;
        var size = WinoStyle.Label(attachment.ReadableSize, WinoStyle.Caption, WinoStyle.SecondaryText);
        var row = WinoLayout.HStack(10, icon, WinoLayout.VStack(1, name, size));
        row.EdgeInsets = new NSEdgeInsets(8, 8, 8, 12);
        var tile = new AttachmentTile { Fill = WinoStyle.Dynamic(WinoStyle.Hex(0xF7F7F8), NSColor.White.ColorWithAlphaComponent((nfloat)0.06)), Stroke = WinoStyle.ZoneStroke, CornerRadius = WinoStyle.ControlRadius };
        WinoLayout.Fill(row, tile);
        tile.ToolTip = attachment.FileName;
        tile.AccessibilityLabel = $"{attachment.FileName}, {attachment.ReadableSize}";
        tile.Opened += (_, _) => Observe(ViewModel.OpenAttachmentCommand.ExecuteAsync(attachment));
        tile.Menu = new NSMenu();
        tile.Menu.AddItem(new NSMenuItem(Translator.Buttons_Open, (_, _) => Observe(ViewModel.OpenAttachmentCommand.ExecuteAsync(attachment))) { Image = WinoIcons.Image(WinoIconGlyph.Open, 16) });
        tile.Menu.AddItem(new NSMenuItem(Translator.Buttons_Save, (_, _) => Observe(ViewModel.SaveAttachmentCommand.ExecuteAsync(attachment))) { Image = WinoIcons.Image(WinoIconGlyph.Save, 16) });
        return tile;
    }

    private static NSColor ExtensionColor(string extension) => extension switch
    {
        "DOC" or "DOCX" => WinoStyle.Hex(0x2B579A),
        "XLS" or "XLSX" or "CSV" => WinoStyle.Hex(0x217346),
        "PPT" or "PPTX" => WinoStyle.Hex(0xB7472A),
        "PDF" => WinoStyle.Hex(0xD13438),
        "ZIP" or "RAR" or "7Z" => WinoStyle.Hex(0x795548),
        _ => WinoStyle.Hex(0x6E6E73)
    };

    // ---- Rendering ----

    private async Task RenderAsync(string html)
    {
        if (_reader is null || _disposedReader) return;
        _linkTarget.Clear();
        _currentRenderedHtml = html ?? string.Empty;
        await _reader.SetThemeAsync(_dark);
        await _reader.SetReaderTypographyAsync($"{ViewModel.PreferencesService.ReaderFont}, sans-serif", ViewModel.PreferencesService.ReaderFontSize);
        // The intelligence context loads while the body renders, like the Windows reader.
        var intelligence = IntelligenceHtmlRendered(_currentRenderedHtml);
        await RenderActiveContentAsync();
        await _reader.SetAccessibilityContextAsync(new ReaderAccessibilityContext(
            string.IsNullOrWhiteSpace(ViewModel.Subject) ? Translator.MailItemNoSubject : ViewModel.Subject,
            string.IsNullOrWhiteSpace(ViewModel.FromName) ? ViewModel.FromAddress : $"{ViewModel.FromName} <{ViewModel.FromAddress}>",
            MailRowMapper.FormatReaderDate(ViewModel.CreationDate, ViewModel.PreferencesService.MailTimeFormatPreference),
            Translator.Reader_MessageBodyAutomationName,
            Translator.Reader_PlainTextFallbackAutomationName,
            ViewModel.CurrentRenderModel?.AccessibleText ?? string.Empty));
        await intelligence;
    }

    /// <summary>Renders the current message body, or its translation while one is shown.</summary>
    private async Task RenderActiveContentAsync()
    {
        if (_reader is null || _disposedReader) return;
        var html = await ResolveActiveHtmlAsync(_currentRenderedHtml);
        var options = ViewModel.CurrentRenderModel?.MailRenderingOptions;
        var policy = (options?.LoadImages ?? true) ? RemoteContentPolicy.ImagesAndFontsAllowed : RemoteContentPolicy.Blocked;
        await _reader.RenderAsync(new HtmlMailReaderRequest(string.IsNullOrEmpty(html) ? " " : html, policy,
            _readerViewEnabled ? HtmlMailRenderMode.Readability : HtmlMailRenderMode.Original, options?.RenderPlaintextLinks ?? true));
    }

    private Task ClearAsync()
    {
        _currentRenderedHtml = string.Empty;
        _linkTarget.Clear();
        IntelligenceContentCleared();
        return _reader is null || _disposedReader ? Task.CompletedTask : _reader.ClearAsync();
    }

    private bool IsDarkAppearance()
        => View.EffectiveAppearance.FindBestMatch([NSAppearance.NameAqua.ToString(), NSAppearance.NameDarkAqua.ToString()]) == NSAppearance.NameDarkAqua.ToString();

    public override void ViewDidLayout()
    {
        base.ViewDidLayout();
        ApplyWidthState(View.Bounds.Width);
        var dark = IsDarkAppearance();
        if (dark == _dark || _reader is null || _disposedReader) return;
        _dark = dark;
        ViewModel.IsDarkWebviewRenderer = dark;
        Observe(_reader.SetThemeAsync(dark));
    }

    /// <summary>
    /// The reader's adaptive layout. The command bar folds into More on its own (it gives way to the
    /// subject); at Mac reading pane and pop-out widths the margins also tighten.
    /// </summary>
    private void ApplyWidthState(nfloat width)
    {
        if (width <= 0) return;
        var state = width < CompactWidth ? ReaderWidthState.Compact : width < NarrowWidth ? ReaderWidthState.Narrow : ReaderWidthState.Wide;
        if (state == _widthState) return;
        _widthState = state;

        double inset = state == ReaderWidthState.Wide ? 24 : 14;
        _subjectLeading.Constant = (nfloat)inset;
        _header.EdgeInsets = new NSEdgeInsets(state == ReaderWidthState.Wide ? 14 : 12, (nfloat)inset, 10, (nfloat)inset);
        foreach (var constraint in _headerWidths) constraint.Constant = (nfloat)(-2 * inset);
        _bodyLeading.Constant = (nfloat)inset;
        _bodyTrailing.Constant = (nfloat)(-inset);
        _bodyBottom.Constant = (nfloat)(-inset);
        _stripRow.EdgeInsets = new NSEdgeInsets(0, (nfloat)inset, 14, (nfloat)inset);
        _avatar.Hidden = state == ReaderWidthState.Compact;
    }

    // ---- Wino Intelligence hooks (MailRenderingPageViewController.Intelligence.cs) ----

    /// <summary>Returns the HTML the body shows: the translation while one is applied, otherwise <paramref name="html"/>.</summary>
    private partial Task<string> ResolveActiveHtmlAsync(string html);

    /// <summary>Builds the intelligence header inside <paramref name="host"/> (hidden, between the chips and the image banner).</summary>
    private partial void BuildIntelligenceHeader(NSView host);

    /// <summary>A new message is about to load into the reader.</summary>
    private partial void IntelligenceBeginItem(MailItemViewModel? item);

    /// <summary>The body HTML of the current message is being rendered; completes when the intelligence context is loaded.</summary>
    private partial Task IntelligenceHtmlRendered(string html);

    /// <summary>The rendered body was cleared (the reader went idle); any translation of it is dropped.</summary>
    private partial void IntelligenceContentCleared();

    /// <summary>The reader is leaving or being disposed. Must be safe to call more than once.</summary>
    private partial void IntelligenceDispose();

#if DEBUG
    /// <summary>
    /// Debug-bridge commands for the reader (DEBUG builds only): <c>reader-badges</c> (unsubscribe
    /// and S/MIME state with the status row), <c>reader-print</c> (opens the print panel sheet and
    /// returns at once so <c>snap</c> can capture it), <c>reader-pdf PATH</c> (exports the message;
    /// a relative path lands in the debug folder) and <c>reader-source</c> (opens the message source sheet).
    /// </summary>
    private void RegisterReaderDebugCommands()
    {
        MacDebugBridge.Register("reader-badges", _ => ReaderDebugAsync(() =>
            $"canUnsubscribe={ViewModel.CanUnsubscribe} signed={ViewModel.IsSmimeSigned} valid={ViewModel.SmimeSignaturesValid} " +
            $"encrypted={ViewModel.IsSmimeEncrypted} | {_statusRow.Dump()} | printPresenter={(ViewModel.PrintPresenter is null ? "none" : "set")}"));
        MacDebugBridge.Register("reader-print", _ => ReaderDebugAsync(() =>
        {
            if (_disposedReader || ViewModel.PrintPresenter is not { } presenter) return "no print presenter";
            Observe(presenter.PrintAsync(new Wino.Core.Domain.Models.Printing.MailPrintRequest(string.IsNullOrWhiteSpace(ViewModel.Subject) ? Translator.MailItemNoSubject : ViewModel.Subject)));
            return "print panel requested";
        }));
        MacDebugBridge.Register("reader-pdf", async args =>
        {
            if (args.Length == 0) return "usage: reader-pdf PATH";
            var path = string.Join(' ', args);
            if (!Path.IsPathRooted(path)) path = Path.Combine(Path.GetTempPath(), "wino-debug", path);
            IMailPrintPresenter? presenter = null;
            await Dispatcher.ExecuteOnUIThread(() => presenter = _disposedReader ? null : ViewModel.PrintPresenter);
            if (presenter is null) return "no print presenter";
            var result = await presenter.ExportPdfAsync(path);
            return $"{result.Status} {path} {result.ErrorMessage}".TrimEnd();
        });
        MacDebugBridge.Register("reader-view", args => ReaderDebugAsync(() =>
        {
            if (args.Length > 0 && (args[0] == "on") != _readerViewEnabled) ToggleReaderView();
            return $"readerView={_readerViewEnabled} rendered={!string.IsNullOrWhiteSpace(_currentRenderedHtml)}";
        }));
        MacDebugBridge.Register("reader-recipients", args => ReaderDebugAsync(() =>
        {
            // "reader-recipients card" opens the sender's contact card.
            if (args.Length > 0 && args[0] == "card" && _senderLink is not null) ShowContactCard(ViewModel.FromName, ViewModel.FromAddress, _senderLink);
            return _recipientRows.Dump() + $" card={_contactCard?.Shown == true}";
        }));
        MacDebugBridge.Register("reader-source", _ => ReaderDebugAsync(() =>
        {
            var item = ViewModel.MenuItems.OfType<MailOperationMenuItem>().FirstOrDefault(candidate => candidate.Operation == MailOperation.ViewMessageSource);
            if (item is null) return "no message source operation";
            Observe(ViewModel.OperationClickedCommand.ExecuteAsync(item));
            return "message source requested";
        }));
    }

    private async Task<string> ReaderDebugAsync(Func<string> read)
    {
        var result = string.Empty;
        await Dispatcher.ExecuteOnUIThread(() => result = read());
        return result;
    }
#endif

    private void CloseRequested(object? sender, EventArgs args)
    {
        // A popped-out reader closes its own window (Windows CloseHostedInstance); the pane goes idle otherwise.
        if (ClosePopOutWindow()) return;
        WeakReferenceMessenger.Default.Send(new DisposeRenderingFrameRequested());
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { ReportError(exception); }
    }

    protected override async Task DeactivateAsync()
    {
        ViewModel.CloseRequested -= CloseRequested;
        ViewModel.ComposeRequested -= ComposeRequested;
        CloseContactCard();
        ViewModel.PreferencesService.PreferenceChanged -= PreferenceChanged;
        ViewModel.RenderHtmlAsyncFunc = null;
        ViewModel.ClearRenderedHtmlAsyncFunc = null;
        ViewModel.PrintPresenter = null!;
        ViewModel.OnNavigatedFrom(NavigationMode.New, null);
        IntelligenceDispose();
        await DisposeReaderAsync();
    }

    private void PreferenceChanged(object? sender, string name)
    {
        if (name != nameof(IPreferencesService.IsShowActionLabelsEnabled)) return;
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() => _commandBar.ShowsLabels = ViewModel.PreferencesService.IsShowActionLabelsEnabled);
    }

    private async Task DisposeReaderAsync()
    {
        if (_reader is null || _disposedReader) return;
        _disposedReader = true;
        _linkTarget.Clear();
        ViewModel.PrintPresenter = null!;
        try { await _reader.DisposeAsync(); }
        catch (Exception exception) { ReportError(exception); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ViewModel.CloseRequested -= CloseRequested;
            ViewModel.ComposeRequested -= ComposeRequested;
            ViewModel.PreferencesService.PreferenceChanged -= PreferenceChanged;
            IntelligenceDispose();
            if (!_disposedReader && _reader is not null) _ = DisposeReaderAsync();
        }
        base.Dispose(disposing);
    }

    /// <summary>Attachment tile: click opens, right-click shows Open and Save.</summary>
    private sealed class AttachmentTile : WinoSurfaceView
    {
        public event EventHandler? Opened;

        public AttachmentTile()
        {
            AccessibilityElement = true;
            AccessibilityRole = NSAccessibilityRoles.ButtonRole;
        }

        public override void MouseUp(NSEvent theEvent)
        {
            if (Bounds.Contains(ConvertPointFromView(theEvent.LocationInWindow, null))) Opened?.Invoke(this, EventArgs.Empty);
        }

        public override void MouseDown(NSEvent theEvent)
        {
        }

        public override bool AccessibilityPerformPress()
        {
            Opened?.Invoke(this, EventArgs.Empty);
            return true;
        }
    }
}
