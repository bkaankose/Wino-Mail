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
/// the header (subject 19 bold, 40pt sender avatar, name, address, recipients, date, intelligence
/// and category chips), the remote-image banner, the HTML body in a card through the shared reader
/// engine, the attachment strip with Save all, and the loading skeleton.
/// The controller is reused across selections; <see cref="RenavigateAsync"/> loads the next message.
/// </summary>
public sealed class MailRenderingPageViewController(MailRenderingPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<MailRenderingPageViewModel>(viewModel, dispatcher, logger)
{
    private MailReaderCommandBar _commandBar = null!;
    private NSTextField _subject = null!;
    private WinoContactPicture _avatar = null!;
    private NSTextField _senderName = null!;
    private NSTextField _senderAddress = null!;
    private NSTextField _recipients = null!;
    private NSTextField _date = null!;
    private NSStackView _chips = null!;
    private WinoInfoBar _imageBanner = null!;
    private WKWebView _webView = null!;
    private AppKitHtmlMailReaderSession? _reader;
    private NSStackView _attachmentTiles = null!;
    private NSView _attachmentStrip = null!;
    private NSButton _saveAllButton = null!;
    private MailLoadingView _loading = null!;
    private MailItemViewModel? _currentItem;
    private bool _dark;
    private bool _disposedReader;

    public override void LoadView()
    {
        var root = new NSView();

        _commandBar = new MailReaderCommandBar();

        // ---- Header ----
        _subject = WinoStyle.Label(string.Empty, WinoStyle.ReaderSubject, WinoStyle.PrimaryText, 3);
        _subject.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        _subject.Selectable = true;

        _avatar = new WinoContactPicture(40);
        _senderName = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong);
        _senderName.Selectable = true;
        _senderAddress = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText);
        _senderAddress.Selectable = true;
        _senderAddress.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        var nameLine = WinoLayout.HStack(6, _senderName, _senderAddress);
        nameLine.Alignment = NSLayoutAttribute.FirstBaseline;
        _recipients = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
        _recipients.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        var senderText = WinoLayout.VStack(1, nameLine, _recipients);
        senderText.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        senderText.SetContentCompressionResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);
        _date = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
        _date.SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);
        var senderRow = WinoLayout.HStack(12, _avatar, senderText, _date);

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

        var header = WinoLayout.VStack(12, _subject, senderRow, _chips, _imageBanner);
        header.EdgeInsets = new NSEdgeInsets(18, 24, 10, 24);
        foreach (var view in new NSView[] { _subject, senderRow, _imageBanner })
            view.WidthAnchor.ConstraintEqualTo(header.WidthAnchor, 1, -48).Active = true;
        // The tile row hugs its chips from the leading edge; it is only capped (and clips) at the header width.
        _chips.WidthAnchor.ConstraintLessThanOrEqualTo(header.WidthAnchor, 1, -48).Active = true;

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
#endif
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        _currentItem = parameter as MailItemViewModel;
        _reader = new AppKitHtmlMailReaderSession(_webView);
        _reader.Configure(ViewModel.ExternalLauncher);
        _reader.OperationFailed += (_, exception) => ReportError(exception);
        _dark = IsDarkAppearance();
        ViewModel.IsDarkWebviewRenderer = _dark;
        ViewModel.RenderHtmlAsyncFunc = RenderAsync;
        ViewModel.ClearRenderedHtmlAsyncFunc = ClearAsync;
        ViewModel.CloseRequested += CloseRequested;

        Bind(nameof(ViewModel.Subject), vm => vm.Subject, subject =>
            _subject.StringValue = string.IsNullOrWhiteSpace(subject) ? Translator.MailItemNoSubject : subject);
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
            UpdateRecipients();
            UpdateAttachments();
        });
        ObserveCollection(ViewModel.ToItems);
        ObserveCollection(ViewModel.CcItems);
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
            UpdateChips();
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
        static string Names(IEnumerable<AccountContactViewModel> contacts)
            => string.Join(", ", contacts.Select(static contact => string.IsNullOrWhiteSpace(contact.Name) ? contact.Address : contact.Name));
        var parts = new List<string>();
        if (ViewModel.ToItems.Count > 0) parts.Add($"{Translator.ComposerTo.Trim()} {Names(ViewModel.ToItems)}");
        // "Cc:" is the protocol label; no translation key exists.
        if (ViewModel.CcItems.Count > 0) parts.Add($"Cc: {Names(ViewModel.CcItems)}");
        _recipients.StringValue = string.Join(" · ", parts);
        _recipients.ToolTip = _recipients.StringValue;
        _date.StringValue = MailRowMapper.FormatReaderDate(ViewModel.CreationDate);
    }

    /// <summary>Intelligence tiles (accent, Sparkle glyph) and category chips under the sender row.</summary>
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
            if (item.HasIntelligenceTiles)
            {
                foreach (var tile in item.IntelligenceTiles.Take(4))
                {
                    var chip = new WinoChipView(22) { CornerRadius = 4, Text = tile.Text, ToolTip = tile.AccessibleText, MaxTextWidth = 220 };
                    var tint = tile.IsWarning ? WinoStyle.Caution : WinoStyle.Accent;
                    chip.Fill = tint.ColorWithAlphaComponent((nfloat)0.13);
                    chip.TextColor = tint;
                    chip.SetGlyph(string.IsNullOrEmpty(tile.Glyph) ? WinoIcons.Glyph(WinoIconGlyph.Sparkle) : tile.Glyph, 12);
                    _chips.AddArrangedSubview(chip);
                }
            }
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
        // Windows OperationCommandBar: the editor theme toggle.
        var theme = new NSMenuItem(_dark ? Translator.Composer_LightTheme : Translator.Composer_DarkTheme, (_, _) => ToggleTheme())
        {
            Image = WinoIcons.Image(_dark ? WinoIconGlyph.LightEditor : WinoIconGlyph.DarkEditor, 16)
        };
        menu.AddItem(theme);
        return menu;
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
        var options = ViewModel.CurrentRenderModel?.MailRenderingOptions;
        var policy = (options?.LoadImages ?? true) ? RemoteContentPolicy.ImagesAndFontsAllowed : RemoteContentPolicy.Blocked;
        await _reader.SetThemeAsync(_dark);
        await _reader.RenderAsync(new HtmlMailReaderRequest(string.IsNullOrEmpty(html) ? " " : html, policy,
            HtmlMailRenderMode.Original, options?.RenderPlaintextLinks ?? true));
        await _reader.SetAccessibilityContextAsync(new ReaderAccessibilityContext(
            string.IsNullOrWhiteSpace(ViewModel.Subject) ? Translator.MailItemNoSubject : ViewModel.Subject,
            string.IsNullOrWhiteSpace(ViewModel.FromName) ? ViewModel.FromAddress : $"{ViewModel.FromName} <{ViewModel.FromAddress}>",
            MailRowMapper.FormatReaderDate(ViewModel.CreationDate),
            Translator.Reader_MessageBodyAutomationName,
            Translator.Reader_PlainTextFallbackAutomationName,
            ViewModel.CurrentRenderModel?.AccessibleText ?? string.Empty));
    }

    private Task ClearAsync() => _reader is null || _disposedReader ? Task.CompletedTask : _reader.ClearAsync();

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

    private void CloseRequested(object? sender, EventArgs args) => WeakReferenceMessenger.Default.Send(new DisposeRenderingFrameRequested());

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { ReportError(exception); }
    }

    protected override async Task DeactivateAsync()
    {
        ViewModel.CloseRequested -= CloseRequested;
        ViewModel.RenderHtmlAsyncFunc = null;
        ViewModel.ClearRenderedHtmlAsyncFunc = null;
        ViewModel.OnNavigatedFrom(NavigationMode.New, null!);
        await DisposeReaderAsync();
    }

    private async Task DisposeReaderAsync()
    {
        if (_reader is null || _disposedReader) return;
        _disposedReader = true;
        try { await _reader.DisposeAsync(); }
        catch (Exception exception) { ReportError(exception); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ViewModel.CloseRequested -= CloseRequested;
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
