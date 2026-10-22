using System.Collections.Specialized;
using CommunityToolkit.Mvvm.Messaging;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using UIKit;
using WebKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Editor;
using Wino.Editor.WebKit;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.Controls.UIKit.MailList;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Mails;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// The message reader (Mac MailRenderingPageViewController, Windows MailRenderingPage), idiom-neutral: the command bar
/// on top (iPad; on iPhone the phone shell shows the same <see cref="IMailReaderCommands"/> in its bottom toolbar), then
/// one vertical scroll with the header (subject, sender avatar and name, recipients, date, the status row with
/// Unsubscribe and S/MIME, category chips, the Wino Intelligence header, the remote-image banner), the HTML body in a
/// card through the shared WebKit reader session (remote content by CSP, links through <see cref="IExternalLauncher"/>),
/// and the attachment strip with Save all; the loading skeleton covers the content while the message downloads. Print
/// and PDF go through <see cref="UIKitMailPrintPresenter"/>. The controller is reused across selections
/// (<see cref="RenavigateAsync"/>). Recipients and the contact card live in the .Recipients partial, the intelligence
/// header in .Intelligence, debug commands in .Debug.
/// </summary>
public sealed partial class MailRenderingPageViewController(MailRenderingPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger, IServiceProvider services)
    : WinoViewController<MailRenderingPageViewModel>(viewModel, dispatcher, logger), IMailReaderCommands
{
    private static readonly bool IsPhone = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Phone;
    private static readonly double Gutter = IsPhone ? 16 : 24;

    /// <summary>The body card's distance from the zone edges (phone); the iPad keeps the header gutter.</summary>
    private static readonly double CardMargin = IsPhone ? 12 : Gutter;

    /// <summary>CSS pixels between the card edge and the mail text, kept when the mail zeroes its own body padding.</summary>
    private const double BodyInset = 16;

    private readonly IServiceProvider _services = services;
    private readonly UIKitSenderPictureProvider _pictures = services.GetRequiredService<UIKitSenderPictureProvider>();
    private readonly MailReaderCommandBar _commandBar = new();
    private readonly UILabel _subject = WinoStyle.Label(string.Empty, WinoStyle.Scaled(20, UIFontWeight.Bold), WinoStyle.PrimaryText, 0);
    private readonly WinoContactPicture _avatar = new(40);
    private readonly UILabel _senderName = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong);
    private readonly UILabel _senderAddress = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText);
    private readonly UILabel _date = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
    private readonly MailReaderStatusRow _statusRow = new();
    private readonly UIStackView _chips = WinoLayout.HStack(8);
    private readonly UIView _intelligenceHost = new() { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
    private readonly WinoInfoBar _imageBanner = new(WinoInfoBarSeverity.Warning, null, Translator.ImageRenderingDisabled);
    private readonly ReaderWebView _webView = new();
    private readonly ReaderLinkTargetBar _linkTarget = new();
    private readonly UIStackView _attachmentTiles = WinoLayout.HStack(10);
    private readonly UIView _attachmentStrip = new() { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
    private readonly UIButton _saveAllButton = new(UIButtonType.System) { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly MailLoadingView _loading = new();
    private readonly UIView _loadingHost = new() { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
    private readonly UIScrollView _scroll = new() { TranslatesAutoresizingMaskIntoConstraints = false, AlwaysBounceVertical = true };
    private NSLayoutConstraint? _webHeight;
    private IDisposable? _contentSizeObserver;
    private WebKitHtmlMailReaderSession? _reader;
    private MailItemViewModel? _currentItem;
    private string _currentRenderedHtml = string.Empty;
    private IReadOnlyList<MailReaderCommand?> _primaryCommands = [];
    private MailOperationMenuItem[] _overflow = [];
    private bool _showsCommandBar = !IsPhone;
    private bool _dark;
    private bool _disposedReader;
    private bool _readerViewEnabled;

    /// <summary>Set by a host that closes the reader itself (the EML sheet); otherwise Close clears the rendering frame.</summary>
    internal Action? CloseOverride { get; set; }

    public IReadOnlyList<MailReaderCommand?> PrimaryCommands => _primaryCommands;

    public event EventHandler? CommandsChanged;

    public bool ShowsCommandBar
    {
        get => _showsCommandBar;
        set
        {
            _showsCommandBar = value;
            _commandBar.Hidden = !value;
        }
    }

    public override void LoadView()
    {
        var root = new UIView { BackgroundColor = WinoStyle.ZoneFill };
        // The subject is the page's title: a compact bar keeps the stack's large-title band (empty for the reader,
        // which has no navigation title) from opening above the header.
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;

        // ---- Header ----
        _subject.AccessibilityTraits = UIAccessibilityTrait.Header;
        _subject.UserInteractionEnabled = true;
        _senderAddress.SetContentCompressionResistancePriority(200, UILayoutConstraintAxis.Horizontal);
        _senderName.SetContentCompressionResistancePriority(400, UILayoutConstraintAxis.Horizontal);
        var senderText = WinoLayout.VStack(1, _senderName, _senderAddress);
        senderText.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        senderText.SetContentCompressionResistancePriority(200, UILayoutConstraintAxis.Horizontal);
        AttachSenderLink(senderText);
        _date.SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Horizontal);
        _date.SetContentHuggingPriority(1000, UILayoutConstraintAxis.Horizontal);
        var senderRow = WinoLayout.HStack(12, _avatar, senderText, _date);
        senderRow.Alignment = UIStackViewAlignment.Center;

        _statusRow.UnsubscribeInvoked += (_, _) => Observe(ViewModel.UnsubscribeCommand.ExecuteAsync(null));
        _statusRow.SignatureInvoked += (_, _) => Observe(ViewModel.ShowSmimeSigningCertificateInfoCommand.ExecuteAsync(null));

        _chips.Hidden = true;

        _imageBanner.ActionTitle = Translator.Buttons_EnableImageRendering;
        _imageBanner.IsClosable = true;
        _imageBanner.Hidden = true;
        _imageBanner.ActionInvoked += (_, _) => Observe(ViewModel.ForceImageLoadingCommand.ExecuteAsync(null));

        BuildIntelligenceHeader(_intelligenceHost);

        // To, Cc and Bcc under the sender, indented to the name column (avatar 40 + spacing 12).
        var recipients = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(BuildRecipientRows(), recipients, 0, 52, 0, 0);

        var header = WinoLayout.Insets(WinoLayout.VStack(12, _subject, senderRow, recipients, _statusRow, _chips, _intelligenceHost, _imageBanner), 12, Gutter, 10, Gutter);
        header.Alignment = UIStackViewAlignment.Fill;
        header.SetCustomSpacing(4, senderRow);
        // The chip row hugs its chips from the leading edge.
        _chips.Alignment = UIStackViewAlignment.Center;
        _chips.Distribution = UIStackViewDistribution.Fill;

        // ---- Body card ----
        WinoAccessibility.Label(_webView, Translator.Reader_MessageBodyAutomationName);
        WinoAccessibility.Help(_webView, Translator.Reader_MessageBodyAutomationHelpText);
        var card = new WinoSurfaceView
        {
            CornerRadius = WinoStyle.GroupRadius,
            Fill = WinoStyle.Dynamic(UIColor.White, WinoStyle.Hex(0x1F1F22)),
            Stroke = WinoStyle.ZoneStroke,
            StrokeWidth = 0.5
        };
        WinoLayout.Fill(_webView, card, 1, 1, 1, 1);
        _webHeight = _webView.HeightAnchor.ConstraintEqualTo(320);
        _webHeight.Active = true;
        var body = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(card, body, 6, CardMargin, 16, CardMargin);
        // The body is as tall as its document; the page scroll owns vertical scrolling, the web view keeps horizontal
        // panning for wide messages.
        _contentSizeObserver = _webView.ScrollView.AddObserver("contentSize", NSKeyValueObservingOptions.New, _ => UpdateWebHeight());

        // ---- Attachments ----
        var tileScroll = new UIScrollView { TranslatesAutoresizingMaskIntoConstraints = false, ShowsHorizontalScrollIndicator = false, AlwaysBounceHorizontal = true };
        tileScroll.HeightAnchor.ConstraintEqualTo(60).Active = true;
        tileScroll.AddSubview(_attachmentTiles);
        NSLayoutConstraint.ActivateConstraints(
        [
            _attachmentTiles.LeadingAnchor.ConstraintEqualTo(tileScroll.ContentLayoutGuide.LeadingAnchor),
            _attachmentTiles.TrailingAnchor.ConstraintEqualTo(tileScroll.ContentLayoutGuide.TrailingAnchor),
            _attachmentTiles.TopAnchor.ConstraintEqualTo(tileScroll.ContentLayoutGuide.TopAnchor),
            _attachmentTiles.BottomAnchor.ConstraintEqualTo(tileScroll.ContentLayoutGuide.BottomAnchor),
            _attachmentTiles.HeightAnchor.ConstraintEqualTo(tileScroll.FrameLayoutGuide.HeightAnchor)
        ]);
        var saveAll = UIButtonConfiguration.GrayButtonConfiguration;
        saveAll.Title = Translator.Reader_SaveAllAttachmentButtonText;
        saveAll.Image = WinoIcons.Image(WinoIconGlyph.ArrowDownload, 14, null, Translator.Reader_SaveAllAttachmentButtonText);
        saveAll.ImagePadding = 6;
        saveAll.CornerStyle = UIButtonConfigurationCornerStyle.Medium;
        _saveAllButton.Configuration = saveAll;
        _saveAllButton.PrimaryActionTriggered += (_, _) => Observe(ViewModel.SaveAllAttachmentsCommand.ExecuteAsync(null));
        var stripStack = WinoLayout.VStack(8, tileScroll, _saveAllButton);
        stripStack.Alignment = UIStackViewAlignment.Leading;
        tileScroll.WidthAnchor.ConstraintEqualTo(stripStack.WidthAnchor).Active = true;
        WinoLayout.Fill(stripStack, _attachmentStrip, 0, Gutter, 16, Gutter);

        var content = WinoLayout.VStack(0, header, _attachmentStrip, body);
        content.Alignment = UIStackViewAlignment.Fill;
        _scroll.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints(
        [
            content.LeadingAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.LeadingAnchor),
            content.TrailingAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.TrailingAnchor),
            content.TopAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.TopAnchor),
            content.BottomAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.BottomAnchor),
            content.WidthAnchor.ConstraintEqualTo(_scroll.FrameLayoutGuide.WidthAnchor)
        ]);

        _commandBar.Hidden = !_showsCommandBar;
        var column = WinoLayout.VStack(0, _commandBar, _scroll);
        column.Alignment = UIStackViewAlignment.Fill;
        root.AddSubview(column);
        NSLayoutConstraint.ActivateConstraints(
        [
            column.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            column.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            column.TopAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TopAnchor),
            column.BottomAnchor.ConstraintEqualTo(root.BottomAnchor)
        ]);
        // The page scrolls the reader, not the document, so the bar sits on the visible scroll frame and hides on scroll.
        _linkTarget.Attach(root, _scroll, _webView);
        _scroll.Scrolled += (_, _) => _linkTarget.Clear();

        _loadingHost.BackgroundColor = WinoStyle.ZoneFill;
        WinoLayout.Fill(_loading, _loadingHost);
        root.AddSubview(_loadingHost);
        NSLayoutConstraint.ActivateConstraints(
        [
            _loadingHost.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            _loadingHost.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            _loadingHost.TopAnchor.ConstraintEqualTo(_scroll.TopAnchor),
            _loadingHost.BottomAnchor.ConstraintEqualTo(root.BottomAnchor)
        ]);

        WinoTraits.OnAppearanceChanged(root, _ => AppearanceChanged());
        UpdateCommandBar();
        View = root;
#if DEBUG
        RegisterReaderDebugCommands();
#endif
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        _currentItem = parameter as MailItemViewModel;
        IntelligenceBeginItem(_currentItem);
        var reader = _reader = new WebKitHtmlMailReaderSession(_webView) { ContentInset = BodyInset };
        reader.Configure(ViewModel.ExternalLauncher);
        reader.OperationFailed += (_, exception) => ReportError(exception);
        reader.LinkHovered += (_, args) => _linkTarget.Show(args);
        reader.NavigationRequested += (_, _) => _linkTarget.Clear();
        _dark = IsDarkAppearance();
        ViewModel.IsDarkWebviewRenderer = _dark;
        ViewModel.RenderHtmlAsyncFunc = RenderAsync;
        ViewModel.ClearRenderedHtmlAsyncFunc = ClearAsync;
        ViewModel.PrintPresenter = new UIKitMailPrintPresenter(() => _disposedReader ? null : _webView, Dispatcher);
        ViewModel.CloseRequested += CloseRequested;

        Bind(nameof(ViewModel.Subject), vm => vm.Subject, subject =>
        {
            _subject.Text = string.IsNullOrWhiteSpace(subject) ? Translator.MailItemNoSubject : subject;
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
            _loadingHost.Hidden = ready;
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
        ObserveCollection(ViewModel.DisplayedAttachments, attachments: true);
        ObserveCollection(ViewModel.Attachments, attachments: true);
        UpdateChips();

        await reader.InitializeAsync();
        await reader.SetThemeAsync(_dark);
        // Nothing to load without a message (a debug or empty activation): the reader stays on its skeleton-free empty body.
        if (parameter is not null) await ViewModel.InitializeNavigationAsync(mode, parameter);
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
            _scroll.SetContentOffset(new CGPoint(0, -_scroll.AdjustedContentInset.Top), false);
        });
        if (parameter is not null) await ViewModel.InitializeNavigationAsync(NavigationMode.New, parameter);
    }

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
        _senderName.Text = name ?? string.Empty;
        _senderAddress.Text = string.Equals(name, ViewModel.FromAddress, StringComparison.OrdinalIgnoreCase) ? string.Empty : ViewModel.FromAddress ?? string.Empty;
        _senderAddress.Hidden = string.IsNullOrEmpty(_senderAddress.Text);
        _avatar.SetIdentity(name, ViewModel.FromAddress);
        _avatar.Image = _pictures.Get(ViewModel.FromAddress);
    }

    private void UpdateRecipients()
    {
        UpdateRecipientRows();
        _date.Text = MailRowPresentation.FormatReaderDate(ViewModel.CreationDate, ViewModel.PreferencesService.MailTimeFormatPreference);
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
            foreach (var category in item.Categories.Take(IsPhone ? 3 : 4))
            {
                var background = WinoStyle.FromHexString(category.BackgroundColorHex) ?? WinoStyle.SubtleFill;
                var chip = new WinoChipView(24) { CornerRadius = 4, Text = category.Name ?? string.Empty, Fill = background, MaxTextWidth = 160 };
                chip.TextColor = WinoStyle.FromHexString(category.TextColorHex)
                    ?? (WinoStyle.FromHexString(category.BackgroundColorHex) is { } solid ? WinoStyle.ReadableText(solid) : WinoStyle.SecondaryText);
                _chips.AddArrangedSubview(chip);
            }
            if (_chips.ArrangedSubviews.Length > 0) _chips.AddArrangedSubview(WinoLayout.Spacer());
        }
        _chips.Hidden = _chips.ArrangedSubviews.Length == 0;
    }

    // ---- Commands ----

    private MailReaderCommand Command(MailOperationMenuItem item)
        => new(item.Operation, MailOperationDisplay.GetGlyph(item.Operation), MailReaderCommandLayout.BarTitle(item.Operation),
            () => Observe(ViewModel.OperationClickedCommand.ExecuteAsync(item)), item.IsEnabled,
            item.Operation == MailOperation.Move ? (Action<UIView>)(anchor => ShowMove(item, anchor)) : null);

    /// <summary>
    /// Move from the reader: the idiom's <see cref="IMailMovePresenter"/> (the iPad popover anchored to the button, the
    /// iPhone Move sheet) for the open message, else the shared folder picker through the ViewModel.
    /// </summary>
    private void ShowMove(MailOperationMenuItem item, UIView anchor)
    {
        if (_currentItem?.MailCopy is { } mail && _services.GetService<IMailMovePresenter>() is { } presenter && presenter.TryShowMove(new[] { mail }, anchor)) return;
        Observe(ViewModel.OperationClickedCommand.ExecuteAsync(item));
    }

    /// <summary>
    /// Rebuilds the primary commands and the More content from the ViewModel's menu through the shared
    /// <see cref="MailReaderCommandLayout"/>; the iOS readers offer Move next to Delete.
    /// </summary>
    private void UpdateCommandBar()
    {
        var plan = MailReaderCommandLayout.Build(ViewModel.MenuItems, _currentItem is not null, offerMove: true);
        _primaryCommands = plan.Primary.Select(slot => slot is null ? null
            : slot.Item is { } item ? Command(item)
            : new MailReaderCommand(slot.Operation, MailOperationDisplay.GetGlyph(slot.Operation), slot.Title, static () => { }, false)).ToArray();
        _overflow = plan.Overflow.ToArray();
        _commandBar.SetCommands(_primaryCommands, CreateMoreMenu);
        CommandsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The More menu: the remaining operations, then Reader View and the reader theme toggle (Windows OperationCommandBar).</summary>
    public UIMenu CreateMoreMenu(UIView? anchor)
    {
        var groups = new List<UIMenuElement>();
        var group = new List<UIMenuElement>();
        foreach (var item in _overflow)
        {
            if (item.Operation == MailOperation.Seperator)
            {
                if (group.Count > 0) groups.Add(UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, group.ToArray()));
                group.Clear();
                continue;
            }
            var title = MailOperationDisplay.GetTitle(item.Operation);
            if (string.IsNullOrEmpty(title)) continue;
            group.Add(Command(item).CreateAction(anchor));
        }
        if (group.Count > 0) groups.Add(UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, group.ToArray()));

        var readerView = UIAction.Create(Translator.Reader_ReaderView, WinoIcons.Image(WinoIconGlyph.Document, 18), null, _ => ToggleReaderView());
        readerView.State = _readerViewEnabled ? UIMenuElementState.On : UIMenuElementState.Off;
        if (string.IsNullOrWhiteSpace(_currentRenderedHtml)) readerView.Attributes = UIMenuElementAttributes.Disabled;
        var theme = UIAction.Create(_dark ? Translator.Composer_LightTheme : Translator.Composer_DarkTheme,
            WinoIcons.Image(_dark ? WinoIconGlyph.LightEditor : WinoIconGlyph.DarkEditor, 18), null, _ => ToggleTheme());
        groups.Add(UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, [readerView, theme]));
        return UIMenu.Create(groups.ToArray());
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

    private UIView CreateTile(MailAttachmentViewModel attachment)
    {
        var extension = Path.GetExtension(attachment.FileName ?? string.Empty).TrimStart('.').ToUpperInvariant();
        if (extension.Length > 4) extension = extension[..4];
        var icon = new WinoSurfaceView { Fill = ExtensionColor(extension), CornerRadius = 6 };
        WinoLayout.Size(icon, 36, 36);
        var iconLabel = WinoStyle.Label(extension, WinoStyle.Fixed(10, UIFontWeight.Bold), UIColor.White);
        icon.AddSubview(iconLabel);
        iconLabel.CenterXAnchor.ConstraintEqualTo(icon.CenterXAnchor).Active = true;
        iconLabel.CenterYAnchor.ConstraintEqualTo(icon.CenterYAnchor).Active = true;
        var name = WinoStyle.Label(attachment.FileName, WinoStyle.Fixed(13, UIFontWeight.Semibold));
        name.LineBreakMode = UILineBreakMode.MiddleTruncation;
        name.WidthAnchor.ConstraintLessThanOrEqualTo(180).Active = true;
        var size = WinoStyle.Label(attachment.ReadableSize, WinoStyle.Caption, WinoStyle.SecondaryText);
        var row = WinoLayout.Insets(WinoLayout.HStack(10, icon, WinoLayout.VStack(1, name, size)), 8, 8, 8, 12);
        var tile = new WinoPressableView
        {
            Fill = WinoStyle.Dynamic(WinoStyle.Hex(0xF7F7F8), WinoStyle.Hex(0xFFFFFF, 0.06)),
            Stroke = WinoStyle.ZoneStroke,
            CornerRadius = WinoStyle.ControlRadius
        };
        row.UserInteractionEnabled = false;
        WinoLayout.Fill(row, tile);
        WinoAccessibility.Element(tile, UIAccessibilityTrait.Button, $"{attachment.FileName}, {attachment.ReadableSize}");
        tile.Clicked += (_, _) => Observe(ViewModel.OpenAttachmentCommand.ExecuteAsync(attachment));
        tile.AddInteraction(new UIContextMenuInteraction(new MenuInteraction(() => UIMenu.Create(
        [
            UIAction.Create(Translator.Buttons_Open, WinoIcons.Image(WinoIconGlyph.Open, 18), null, _ => Observe(ViewModel.OpenAttachmentCommand.ExecuteAsync(attachment))),
            UIAction.Create(Translator.Buttons_Save, WinoIcons.Image(WinoIconGlyph.Save, 18), null, _ => Observe(ViewModel.SaveAttachmentCommand.ExecuteAsync(attachment)))
        ]))));
        return tile;
    }

    private static UIColor ExtensionColor(string extension) => extension switch
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
        // A shorter message must be able to shrink the body: measure from a small frame again.
        if (_webHeight is not null) _webHeight.Constant = 120;
        await _reader.SetThemeAsync(_dark);
        var intelligence = IntelligenceHtmlRendered(_currentRenderedHtml);
        await RenderActiveContentAsync();
        await _reader.SetAccessibilityContextAsync(new ReaderAccessibilityContext(
            string.IsNullOrWhiteSpace(ViewModel.Subject) ? Translator.MailItemNoSubject : ViewModel.Subject,
            string.IsNullOrWhiteSpace(ViewModel.FromName) ? ViewModel.FromAddress : $"{ViewModel.FromName} <{ViewModel.FromAddress}>",
            MailRowPresentation.FormatReaderDate(ViewModel.CreationDate, ViewModel.PreferencesService.MailTimeFormatPreference),
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
        UpdateWebHeight();
    }

    private Task ClearAsync()
    {
        _currentRenderedHtml = string.Empty;
        _linkTarget.Clear();
        IntelligenceContentCleared();
        return _reader is null || _disposedReader ? Task.CompletedTask : _reader.ClearAsync();
    }

    private void UpdateWebHeight()
    {
        if (_webHeight is null || _disposedReader) return;
        var height = Math.Max(120, (double)_webView.ScrollView.ContentSize.Height);
        if (Math.Abs(_webHeight.Constant - height) < 1) return;
        _webHeight.Constant = (nfloat)height;
    }

    private bool IsDarkAppearance() => WinoTraits.IsDark(ViewIfLoaded?.TraitCollection ?? TraitCollection);

    private void AppearanceChanged()
    {
        var dark = IsDarkAppearance();
        if (dark == _dark || _reader is null || _disposedReader) return;
        _dark = dark;
        ViewModel.IsDarkWebviewRenderer = dark;
        Observe(_reader.SetThemeAsync(dark));
    }

    // ---- Wino Intelligence hooks (MailRenderingPageViewController.Intelligence.cs) ----

    private partial Task<string> ResolveActiveHtmlAsync(string html);
    private partial void BuildIntelligenceHeader(UIView host);
    private partial void IntelligenceBeginItem(MailItemViewModel? item);
    private partial Task IntelligenceHtmlRendered(string html);
    private partial void IntelligenceContentCleared();
    private partial void IntelligenceDispose();

    private void CloseRequested(object? sender, EventArgs args)
    {
        if (CloseOverride is { } close)
        {
            close();
            return;
        }
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
        CloseContactCard();
        ViewModel.RenderHtmlAsyncFunc = static _ => Task.CompletedTask;
        ViewModel.ClearRenderedHtmlAsyncFunc = static () => Task.CompletedTask;
        await base.DeactivateAsync();
        IntelligenceDispose();
        await DisposeReaderAsync();
    }

    private async Task DisposeReaderAsync()
    {
        if (_reader is not { } reader || _disposedReader) return;
        // The print presenter's web view getter returns null from here on, so print and PDF report Unavailable.
        _disposedReader = true;
        _linkTarget.Clear();
        _contentSizeObserver?.Dispose();
        _contentSizeObserver = null;
        try { await reader.DisposeAsync(); }
        catch (Exception exception) { ReportError(exception); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ViewModel.CloseRequested -= CloseRequested;
            CommandsChanged = null;
            IntelligenceDispose();
            _contentSizeObserver?.Dispose();
            _contentSizeObserver = null;
            if (!_disposedReader && _reader is not null) _ = DisposeReaderAsync();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// The body web view: transparent over the card, vertical scrolling handed to the page scroll (the view is as tall
    /// as its document), horizontal panning kept for wide messages.
    /// </summary>
    private sealed class ReaderWebView : WKWebView
    {
        public ReaderWebView() : base(CGRect.Empty, new WKWebViewConfiguration())
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
            Opaque = false;
            BackgroundColor = UIColor.Clear;
            ScrollView.BackgroundColor = UIColor.Clear;
            ScrollView.Bounces = false;
            ScrollView.AlwaysBounceVertical = false;
            ScrollView.DirectionalLockEnabled = true;
            ScrollView.ShowsVerticalScrollIndicator = false;
            ScrollView.ContentInsetAdjustmentBehavior = UIScrollViewContentInsetAdjustmentBehavior.Never;
            if (OperatingSystem.IsIOSVersionAtLeast(16, 4)) Inspectable = IsDebugBuild;
        }

#if DEBUG
        private const bool IsDebugBuild = true;
#else
        private const bool IsDebugBuild = false;
#endif
    }
}

/// <summary>A context menu provider for a view (attachment tiles, recipient links).</summary>
internal sealed class MenuInteraction(Func<UIMenu?> menu) : UIContextMenuInteractionDelegate
{
    public override UIContextMenuConfiguration? GetConfigurationForMenu(UIContextMenuInteraction interaction, CGPoint location)
        => menu() is { } built ? UIContextMenuConfiguration.Create(null, null, _ => built) : null;
}
