using System.Collections.Specialized;
using CommunityToolkit.Mvvm.Messaging;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Editor;
using Wino.Mail.Rendering;
using EngineMailView = Wino.Renderer.UIKit.MailView;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.Controls.UIKit.MailList;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;
using Wino.Core.Domain.Messaging.Client.Mails;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// The message reader (Mac MailRenderingPageViewController, Windows MailRenderingPage), idiom-neutral: the command bar
/// on top (iPad; on iPhone the phone shell shows the same <see cref="IMailReaderCommands"/> in its bottom toolbar), then
/// one vertical scroll with the header (subject, sender avatar and name, recipients, date, the status row with
/// Unsubscribe and S/MIME, category chips, the Wino Intelligence header, the remote content bars), the HTML body in a
/// card through the native mail renderer (MailRenderingPageViewController.Engine.cs; links through the ViewModel's OpenLinkAsync),
/// and the attachment strip with Save all; the loading skeleton covers the content while the message downloads. Print
/// and PDF go through <see cref="UIKitEngineMailPrintPresenter"/>. The controller is reused across selections
/// (<see cref="RenavigateAsync"/>). Recipients and the contact card live in the .Recipients partial, the intelligence
/// header in .Intelligence, the calendar invitation card in .Invitation, the privacy button and bars in .Privacy, debug commands in .Debug.
/// </summary>
public sealed partial class MailRenderingPageViewController(MailRenderingPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger, IServiceProvider services)
    : WinoViewController<MailRenderingPageViewModel>(viewModel, dispatcher, logger), IMailReaderCommands
{
    private static readonly bool IsPhone = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Phone;
    private static readonly double Gutter = IsPhone ? 16 : 24;

    /// <summary>The body card's distance from the zone edges (phone); the iPad keeps the header gutter.</summary>
    private static readonly double CardMargin = IsPhone ? 12 : Gutter;

    /// <summary>CSS pixels between the card edge and the mail text, kept when the mail zeroes its own body padding.</summary>
    private const float BodyInset = 16;

    private readonly IServiceProvider _services = services;
    private readonly UIKitSenderPictureProvider _pictures = services.GetRequiredService<UIKitSenderPictureProvider>();
    private readonly MailReaderCommandBar _commandBar = new();
    private readonly UILabel _subject = WinoStyle.Label(string.Empty, WinoStyle.Scaled(20, UIFontWeight.Bold), WinoStyle.PrimaryText, 0);
    private readonly WinoContactPicture _avatar = new(40);
    private readonly UILabel _senderName = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong);
    private readonly UILabel _senderAddress = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText);
    private readonly UILabel _date = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText);
    private readonly UILabel _barSubject = WinoStyle.Label(string.Empty, WinoStyle.Scaled(16, UIFontWeight.Semibold), WinoStyle.PrimaryText);
    private bool _barSubjectShown;
    private readonly MailReaderStatusRow _statusRow = new();
    private readonly UIStackView _chips = WinoLayout.HStack(8);
    private readonly UIView _intelligenceHost = new() { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
    private readonly UIStackView _actionCardsHost = WinoLayout.VStack(8);
    private readonly EngineMailView _mailView = new() { TranslatesAutoresizingMaskIntoConstraints = false, SizesToContent = true };
    private readonly ReaderLinkTargetBar _linkTarget = new();
    private readonly UIStackView _attachmentTiles = WinoLayout.HStack(10);
    private readonly UIView _attachmentStrip = new() { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
    private readonly UIButton _saveAllButton = new(UIButtonType.System) { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly MailLoadingView _loading = new();
    private readonly UIView _loadingHost = new() { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
    private readonly UIScrollView _scroll = new() { TranslatesAutoresizingMaskIntoConstraints = false, AlwaysBounceVertical = true };
    private NSLayoutConstraint? _webHeight;
    private readonly EngineReaderPresentation _presentation = new();
    private EngineMailMessageReader? _reader;
    private MailItemViewModel? _currentItem;
    private string _currentRenderedHtml = string.Empty;
    private IReadOnlyList<MailReaderCommand?> _primaryCommands = [];
    private MailOperationMenuItem[] _overflow = [];
    private bool _showsCommandBar = !IsPhone;
    private bool _dark;
    private bool _disposedReader;

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
        _senderName.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        // The date's line carries the privacy button; the stack's first baseline is the date's.
        var dateLine = WinoLayout.HStack(4, _date, BuildPrivacyButton());
        dateLine.Alignment = UIStackViewAlignment.Center;
        WinoLayout.Priorities(dateLine, UILayoutConstraintAxis.Horizontal, 1000, 1000);
        var nameLine = WinoLayout.HStack(8, _senderName, dateLine);
        nameLine.Alignment = UIStackViewAlignment.FirstBaseline;
        var senderText = WinoLayout.VStack(1, nameLine, _senderAddress);
        senderText.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        senderText.SetContentCompressionResistancePriority(200, UILayoutConstraintAxis.Horizontal);
        AttachSenderLink(senderText);
        _date.SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Horizontal);
        _date.SetContentHuggingPriority(1000, UILayoutConstraintAxis.Horizontal);
        var senderRow = WinoLayout.HStack(12, _avatar, senderText);
        senderRow.Alignment = UIStackViewAlignment.Center;

        // Once the header subject scrolls under the bar it becomes the bar's title.
        _barSubject.LineBreakMode = UILineBreakMode.TailTruncation;
        _barSubject.TextAlignment = UITextAlignment.Center;
        _barSubject.Alpha = 0;
        _barSubject.IsAccessibilityElement = false;
        _barSubject.SetContentCompressionResistancePriority(1, UILayoutConstraintAxis.Horizontal);
        // The bar animates its title view's own alpha on push and pop, so the fade runs on the label inside it.
        var barTitle = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(_barSubject, barTitle);
        NavigationItem.TitleView = barTitle;

        _statusRow.UnsubscribeInvoked += (_, _) => Observe(ViewModel.UnsubscribeCommand.ExecuteAsync(null));
        _statusRow.SignatureInvoked += (_, _) => Observe(ViewModel.ShowSmimeSigningCertificateInfoCommand.ExecuteAsync(null));

        _chips.Hidden = true;

        var privacyBars = BuildPrivacyBars();

        BuildIntelligenceHeader(_intelligenceHost);
        // Read from the MIME, so invitations synced before they were recognized still get the card.
        var invitationCard = BuildInvitationCard();

        // To, Cc and Bcc under the sender, indented to the name column (avatar 40 + spacing 12).
        var recipients = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(BuildRecipientRows(), recipients, 0, 52, 0, 0);

        var header = WinoLayout.Insets(WinoLayout.VStack(12, _subject, senderRow, recipients, _statusRow, _chips, _actionCardsHost, _intelligenceHost, privacyBars, invitationCard), 12, Gutter, 10, Gutter);
        header.Alignment = UIStackViewAlignment.Fill;
        header.SetCustomSpacing(4, senderRow);
        // The chip row hugs its chips from the leading edge.
        _chips.Alignment = UIStackViewAlignment.Center;
        _chips.Distribution = UIStackViewDistribution.Fill;

        // ---- Body card ----
        WinoAccessibility.Help(_mailView, Translator.Reader_MessageBodyAutomationHelpText);
        var card = new WinoSurfaceView
        {
            CornerRadius = WinoStyle.GroupRadius,
            Fill = WinoStyle.Dynamic(UIColor.White, WinoStyle.Hex(0x1F1F22)),
            Stroke = WinoStyle.ZoneStroke,
            StrokeWidth = 0.5
        };
        WinoLayout.Fill(_mailView, card, 1, 1, 1, 1);
        _webHeight = _mailView.HeightAnchor.ConstraintEqualTo(320);
        _webHeight.Active = true;
        var body = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(card, body, 6, CardMargin, 16, CardMargin);
        // The body is as tall as its mail; the page scroll owns vertical scrolling, and the mail view draws the part
        // the page shows.
        _mailView.ContentHeightChanged += (_, _) => UpdateBodyHeight();

        // ---- Attachments ----
        var tileScroll = new UIScrollView { TranslatesAutoresizingMaskIntoConstraints = false, ShowsHorizontalScrollIndicator = false, AlwaysBounceHorizontal = true };
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
        _linkTarget.Attach(root, _scroll, _mailView);
        _scroll.Scrolled += (_, _) =>
        {
            _linkTarget.Clear();
            UpdateBarSubject();
        };

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
        InitializeEngineReader();
        InitializeActionCards();
        _dark = IsDarkAppearance();
        ViewModel.IsDarkWebviewRenderer = _dark;
        ViewModel.MessageReader = this;
        ViewModel.PrintPresenter = CreatePrintPresenter();
        ViewModel.CloseRequested += CloseRequested;

        Bind(nameof(ViewModel.Subject), vm => vm.Subject, subject =>
        {
            _subject.Text = string.IsNullOrWhiteSpace(subject) ? Translator.MailItemNoSubject : subject;
            _barSubject.Text = _subject.Text;
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
            UpdateBarSubject();
        });
        Bind(nameof(ViewModel.CurrentRenderModel), vm => vm.CurrentRenderModel, _ =>
        {
            UpdatePrivacy();
            _statusRow.Update(ViewModel.CanUnsubscribe, ViewModel.IsSmimeSigned, ViewModel.SmimeSignaturesValid, ViewModel.IsSmimeEncrypted);
            UpdateRecipients();
            UpdateAttachments();
        });
        BindPrivacy();
        ObserveCollection(ViewModel.ToItems);
        ObserveCollection(ViewModel.CcItems);
        ObserveCollection(ViewModel.BccItems);
        ObserveCollection(ViewModel.DisplayedAttachments, attachments: true);
        ObserveCollection(ViewModel.Attachments, attachments: true);
        UpdateChips();

        ApplyReaderPresentation();
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
        _date.Text = HeaderDate(ViewModel.CreationDate, ViewModel.PreferencesService.MailTimeFormatPreference);
        _date.AccessibilityLabel = MailRowPresentation.FormatAccessibleDate(ViewModel.CreationDate);
    }

    /// <summary>
    /// The header date beside the sender name: the time today, then "Yesterday", the weekday this week, the short month
    /// and day this year, with the time; the short date for older mail. The long form is the accessibility label.
    /// </summary>
    private static string HeaderDate(DateTime value, TimeFormatPreference timeFormat)
    {
        if (value == default) return string.Empty;

        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var local = value.ToLocalTime();
        var today = DateTime.Today;
        var time = DateTimeDisplayFormatter.FormatTime(local, DateTimeDisplayFormatter.GetTimeDisplayType(timeFormat, culture), culture);

        if (local.Date == today) return time;
        if (local.Date == today.AddDays(-1)) return $"{Translator.Yesterday} {time}";
        if (local.Date > today.AddDays(-7)) return $"{local.ToString("ddd", culture)} {time}";
        if (local.Year == today.Year) return $"{local.ToString(culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM"), culture)} {time}";
        return local.ToString("d", culture);
    }

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        UpdateBarSubject();
    }

    private void UpdateBarSubject()
    {
        if (_subject.Superview is null) return;
        var subjectFoot = _subject.ConvertRectToView(_subject.Bounds, _scroll).GetMaxY();
        bool shown = ViewModel.IsMailContentReady && _subject.Bounds.Height > 0
            && _scroll.ContentOffset.Y + _scroll.AdjustedContentInset.Top >= subjectFoot - 4;
        if (shown == _barSubjectShown) return;
        _barSubjectShown = shown;
        if (UIAccessibility.IsReduceMotionEnabled) _barSubject.Alpha = shown ? 1 : 0;
        else UIView.Animate(0.18, () => _barSubject.Alpha = shown ? 1 : 0);
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
        readerView.State = ViewModel.IsReaderViewEnabled ? UIMenuElementState.On : UIMenuElementState.Off;
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
        ViewModel.IsReaderViewEnabled = !ViewModel.IsReaderViewEnabled;
        _reader.IsReaderView = ViewModel.IsReaderViewEnabled;
    }

    private void ToggleTheme()
    {
        if (_reader is null || _disposedReader) return;
        _dark = !_dark;
        ViewModel.IsDarkWebviewRenderer = _dark;
        ApplyReaderPresentation();
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
        var icon = AttachmentBadge.Create(attachment, 36);
        var name = WinoStyle.Label(attachment.FileName, WinoStyle.Fixed(13, UIFontWeight.Semibold));
        name.LineBreakMode = UILineBreakMode.MiddleTruncation;
        name.WidthAnchor.ConstraintLessThanOrEqualTo(180).Active = true;
        var details = WinoStyle.Label(attachment.DetailsText, WinoStyle.Caption, WinoStyle.SecondaryText);
        var text = WinoLayout.VStack(1, name, details);
        text.Alignment = UIStackViewAlignment.Leading;
        var row = WinoLayout.Insets(WinoLayout.HStack(10, icon, text), 8, 8, 8, 12);
        var tile = new WinoPressableView
        {
            Fill = WinoStyle.Dynamic(WinoStyle.Hex(0xF7F7F8), WinoStyle.Hex(0xFFFFFF, 0.06)),
            Stroke = WinoStyle.ZoneStroke,
            CornerRadius = WinoStyle.ControlRadius
        };
        row.UserInteractionEnabled = false;
        WinoLayout.Fill(row, tile);
        WinoAccessibility.Element(tile, UIAccessibilityTrait.Button, $"{attachment.FileName}, {attachment.DetailsText}");
        tile.Clicked += (_, _) => Observe(ViewModel.OpenAttachmentCommand.ExecuteAsync(attachment));
        tile.AddInteraction(new UIContextMenuInteraction(new MenuInteraction(() => UIMenu.Create(
        [
            UIAction.Create(Translator.Buttons_Open, WinoIcons.Image(WinoIconGlyph.Open, 18), null, _ => Observe(ViewModel.OpenAttachmentCommand.ExecuteAsync(attachment))),
            UIAction.Create(Translator.Buttons_Save, WinoIcons.Image(WinoIconGlyph.Save, 18), null, _ => Observe(ViewModel.SaveAttachmentCommand.ExecuteAsync(attachment)))
        ]))));
        return tile;
    }

    // ---- Rendering ----

    /// <summary>The body is as tall as the laid-out mail, at least the height of a short message's card.</summary>
    private void UpdateBodyHeight()
    {
        if (_webHeight is null || _disposedReader) return;
        var height = Math.Max(120, Math.Ceiling(_mailView.IntrinsicContentSize.Height));
        if (Math.Abs(_webHeight.Constant - height) < 1) return;
        _webHeight.Constant = (nfloat)height;
    }

    /// <summary>
    /// A reader created before it enters a window has unspecified traits, which read as light; the window scene's
    /// traits (which carry the app's appearance override) decide then.
    /// </summary>
    private bool IsDarkAppearance()
    {
        var traits = ViewIfLoaded?.TraitCollection ?? TraitCollection;
        if (ViewIfLoaded?.Window is null || traits.UserInterfaceStyle == UIUserInterfaceStyle.Unspecified)
        {
            foreach (var scene in UIApplication.SharedApplication.ConnectedScenes)
                if (scene is UIWindowScene windowScene && windowScene.Windows.FirstOrDefault(static window => window.IsKeyWindow) is { } key)
                    return WinoTraits.IsDark(key.TraitCollection);
        }
        return WinoTraits.IsDark(traits);
    }

    public override void ViewIsAppearing(bool animated)
    {
        base.ViewIsAppearing(animated);
        // The trait registration does not report the first resolved appearance, so a reader opened right after
        // a switch to dark kept its white body (lab, 3 Nov 2026).
        AppearanceChanged();
    }

    private void AppearanceChanged()
    {
        var dark = IsDarkAppearance();
        if (dark == _dark || _reader is null || _disposedReader) return;
        _dark = dark;
        ViewModel.IsDarkWebviewRenderer = dark;
        ApplyReaderPresentation();
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
        ViewModel.MessageReader = null;
        ViewModel.RenderHtmlAsyncFunc = static _ => Task.CompletedTask;
        ViewModel.ClearRenderedHtmlAsyncFunc = static () => Task.CompletedTask;
        await base.DeactivateAsync();
        IntelligenceDispose();
        InvitationDispose();
        await DisposeReaderAsync();
    }

    private async Task DisposeReaderAsync()
    {
        if (_reader is not { } reader || _disposedReader) return;
        // The print presenter's reader getter returns null from here on, so print and PDF report Unavailable.
        _disposedReader = true;
        _linkTarget.Clear();
        try { await reader.ClearAsync(); reader.Detach(); }
        catch (Exception exception) { ReportError(exception); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ViewModel.CloseRequested -= CloseRequested;
            CommandsChanged = null;
            IntelligenceDispose();
            InvitationDispose();
            if (!_disposedReader && _reader is not null) _ = DisposeReaderAsync();
        }
        base.Dispose(disposing);
    }
}

/// <summary>A context menu provider for a view (attachment tiles, recipient links).</summary>
internal sealed class MenuInteraction(Func<UIMenu?> menu) : UIContextMenuInteractionDelegate
{
    public override UIContextMenuConfiguration? GetConfigurationForMenu(UIContextMenuInteraction interaction, CGPoint location)
        => menu() is { } built ? UIContextMenuConfiguration.Create(null, null, _ => built) : null;
}
