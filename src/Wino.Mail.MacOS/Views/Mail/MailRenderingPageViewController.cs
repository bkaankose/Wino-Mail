using System.Collections.Specialized;
using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using CoreGraphics;
using WebKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Models.Navigation;
using Wino.Editor;
using Wino.Editor.AppKit;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.MailList;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Mails;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// Reading pane message view, laid out like the Windows MailRenderingPage inside the reader zone:
/// the command bar on top (Reply, Reply all, Forward | Archive, Delete, Move, Flag, Mark read, More),
/// the header (subject 19 bold, 40pt sender avatar, name, address, recipients, date, the status row
/// with Unsubscribe and the S/MIME indicators, category chips, the Wino Intelligence header), the
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

    public override void LoadView()
    {
        var root = new NSView();

        _commandBar = new MailReaderCommandBar();

        // ---- Header ----
        _subject = WinoStyle.Label(string.Empty, WinoStyle.ReaderSubject, WinoStyle.PrimaryText, 3);
        _subject.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _subject.Selectable = true;

        _avatar = new WinoContactPicture(40);
        // The sender line is one link that opens the sender's contact card (AttachSenderLink).
        _senderName = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong);
        _senderAddress = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText);
        _senderAddress.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        var nameLine = WinoLayout.HStack(6, _senderName, _senderAddress);
        nameLine.Alignment = NSLayoutAttribute.FirstBaseline;
        AttachSenderLink(nameLine);
        var senderText = WinoLayout.VStack(1, nameLine);
        senderText.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        senderText.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        _date = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
        _date.SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);
        var senderRow = WinoLayout.HStack(12, _avatar, senderText, _date);

        // Unsubscribe link and S/MIME indicators, under the sender like the Windows header.
        _statusRow = new MailReaderStatusRow();
        _statusRow.UnsubscribeInvoked += (_, _) => Observe(ViewModel.UnsubscribeCommand.ExecuteAsync(null));
        _statusRow.SignatureInvoked += (_, _) => Observe(ViewModel.ShowSmimeSigningCertificateInfoCommand.ExecuteAsync(null));

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

        // To, Cc and Bcc under the sender, indented to the name column (avatar 40 + spacing 12).
        var recipients = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(BuildRecipientRows(), recipients, 0, 52, 0, 0);

        var header = WinoLayout.VStack(12, _subject, senderRow, recipients, _statusRow, _chips, _intelligenceHost, _imageBanner);
        header.EdgeInsets = new NSEdgeInsets(18, 24, 10, 24);
        header.SetCustomSpacing(4, senderRow);
        foreach (var view in new NSView[] { _subject, senderRow, recipients, _intelligenceHost, _imageBanner })
            view.WidthAnchor.ConstraintEqualTo(header.WidthAnchor, 1, -48).Active = true;
        // The tile row hugs its chips from the leading edge; it is only capped (and clips) at the header width.
        _chips.WidthAnchor.ConstraintLessThanOrEqualTo(header.WidthAnchor, 1, -48).Active = true;
        _statusRow.WidthAnchor.ConstraintLessThanOrEqualTo(header.WidthAnchor, 1, -48).Active = true;

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
        var body = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(card, body, 6, 24, 24, 24);

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
        var stripRow = WinoLayout.HStack(10, tileScroll, _saveAllButton);
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
        foreach (var view in new NSView[] { _commandBar, header, body, _attachmentStrip })
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
            loadingHost.TopAnchor.ConstraintEqualTo(_commandBar.BottomAnchor),
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
            UpdatePopOutTitle(subject);
        });
        Bind(nameof(ViewModel.FromName), vm => vm.FromName, _ => UpdateSender());
        Bind(nameof(ViewModel.FromAddress), vm => vm.FromAddress, _ => UpdateSender());
        Bind(nameof(ViewModel.CreationDate), vm => vm.CreationDate, _ => UpdateRecipients());
        Bind(nameof(ViewModel.MenuItems), vm => vm.MenuItems, _ => UpdateCommandBar());
        Bind(nameof(ViewModel.IsMailContentReady), vm => vm.IsMailContentReady, ready =>
        {
            if (_loading.Superview is { } host) host.Hidden = ready;
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
    }

    private void UpdateRecipients()
    {
        UpdateRecipientRows();
        _date.StringValue = MailRowMapper.FormatReaderDate(ViewModel.CreationDate);
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

    private static readonly MailOperation[] PrimaryOrder =
    [
        MailOperation.Reply, MailOperation.ReplyAll, MailOperation.Forward, MailOperation.None,
        MailOperation.Archive, MailOperation.UnArchive, MailOperation.SoftDelete, MailOperation.HardDelete, MailOperation.Move,
        MailOperation.SetFlag, MailOperation.ClearFlag, MailOperation.MarkAsRead, MailOperation.MarkAsUnread
    ];

    /// <summary>The short label the Windows command bar shows ("Flag" rather than "Set flag").</summary>
    private static string BarTitle(MailOperation operation)
        => operation == MailOperation.SetFlag ? Translator.MailOperation_Flag : MailOperationPresentation.Title(operation);

    private void UpdateCommandBar()
    {
        var items = ViewModel.MenuItems.OfType<MailOperationMenuItem>().Where(static item => item.Operation != MailOperation.Seperator).ToArray();
        var commands = new List<MailReaderCommand?>();
        var primary = new HashSet<MailOperation>();
        if (!items.Any(item => PrimaryOrder.Contains(item.Operation)))
        {
            if (_currentItem is null && items.Length > 0)
            {
                // A saved .eml has only Save As and Print; show just those, like Windows.
                foreach (var item in items) { var captured = item; commands.Add(new MailReaderCommand(MailOperationPresentation.Glyph(item.Operation), BarTitle(item.Operation), () => Observe(ViewModel.OperationClickedCommand.ExecuteAsync(captured)), item.IsEnabled)); }
                _commandBar.SetCommands(commands, null);
                return;
            }
            // Nothing loaded yet (or the message failed to load): the default set, disabled, like the Windows bar.
            foreach (var operation in new[] { MailOperation.Reply, MailOperation.ReplyAll, MailOperation.Forward, MailOperation.None, MailOperation.Archive, MailOperation.SoftDelete, MailOperation.Move, MailOperation.SetFlag, MailOperation.MarkAsRead })
                commands.Add(operation == MailOperation.None ? null : new MailReaderCommand(MailOperationPresentation.Glyph(operation), BarTitle(operation), () => { }, false));
            _commandBar.SetCommands(commands, items.Length == 0 ? null : () => BuildMoreMenu(ViewModel.MenuItems.OfType<MailOperationMenuItem>().ToArray()));
            return;
        }

        foreach (var operation in PrimaryOrder)
        {
            if (operation == MailOperation.None) { commands.Add(null); continue; }
            var item = items.FirstOrDefault(candidate => candidate.Operation == operation);
            if (item is null) continue;
            primary.Add(operation);
            var captured = item;
            commands.Add(new MailReaderCommand(MailOperationPresentation.Glyph(operation), BarTitle(operation),
                () => Observe(ViewModel.OperationClickedCommand.ExecuteAsync(captured)), item.IsEnabled));
        }
        if (commands.Count > 0 && commands[^1] is null) commands.RemoveAt(commands.Count - 1);
        AppendPopOutCommand(commands);
        var overflow = ViewModel.MenuItems.OfType<MailOperationMenuItem>().Where(item => !primary.Contains(item.Operation)).ToArray();
        _commandBar.SetCommands(commands, () => BuildMoreMenu(overflow));
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
            var title = MailOperationPresentation.Title(item.Operation);
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
        _currentRenderedHtml = html ?? string.Empty;
        await _reader.SetThemeAsync(_dark);
        // The intelligence context loads while the body renders, like the Windows reader.
        var intelligence = IntelligenceHtmlRendered(_currentRenderedHtml);
        await RenderActiveContentAsync();
        await _reader.SetAccessibilityContextAsync(new ReaderAccessibilityContext(
            string.IsNullOrWhiteSpace(ViewModel.Subject) ? Translator.MailItemNoSubject : ViewModel.Subject,
            string.IsNullOrWhiteSpace(ViewModel.FromName) ? ViewModel.FromAddress : $"{ViewModel.FromName} <{ViewModel.FromAddress}>",
            MailRowMapper.FormatReaderDate(ViewModel.CreationDate),
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
        IntelligenceContentCleared();
        return _reader is null || _disposedReader ? Task.CompletedTask : _reader.ClearAsync();
    }

    private bool IsDarkAppearance()
        => View.EffectiveAppearance.FindBestMatch([NSAppearance.NameAqua.ToString(), NSAppearance.NameDarkAqua.ToString()]) == NSAppearance.NameDarkAqua.ToString();

    public override void ViewDidLayout()
    {
        base.ViewDidLayout();
        var dark = IsDarkAppearance();
        if (dark == _dark || _reader is null || _disposedReader) return;
        _dark = dark;
        ViewModel.IsDarkWebviewRenderer = dark;
        Observe(_reader.SetThemeAsync(dark));
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
        ViewModel.RenderHtmlAsyncFunc = null;
        ViewModel.ClearRenderedHtmlAsyncFunc = null;
        ViewModel.PrintPresenter = null!;
        ViewModel.OnNavigatedFrom(NavigationMode.New, null!);
        IntelligenceDispose();
        await DisposeReaderAsync();
    }

    private async Task DisposeReaderAsync()
    {
        if (_reader is null || _disposedReader) return;
        _disposedReader = true;
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
