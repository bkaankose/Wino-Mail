using System.Net;
using CoreGraphics;
using Foundation;
using UIKit;
using WebKit;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Editor;
using Wino.Editor.WebKit;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Calendar;

/// <summary>
/// The people card (organizer first, RSVP glyphs), the HTML description through the shared WebKit reader session (as
/// tall as its document; the page scroll owns vertical scrolling) and the attachment rows (tap opens, the inline buttons
/// and the context menu offer Open and Save, a spinner replaces the buttons while busy).
/// </summary>
public sealed partial class EventDetailsPageViewController
{
    private const double MinDescriptionHeight = 44;

    private readonly UILabel _peopleTitle = WinoStyle.Label(Translator.CalendarEventDetails_People, WinoStyle.Caption, WinoStyle.SecondaryText);
    private readonly UIStackView _peopleList = WinoLayout.VStack(2);
    private readonly WinoSurfaceView _peopleCard = new() { Fill = CardFill, CornerRadius = WinoStyle.ControlRadius, Hidden = true };
    private readonly DescriptionWebView _descriptionWeb = new();
    private readonly WinoSurfaceView _descriptionCard = new()
    {
        CornerRadius = WinoStyle.GroupRadius,
        Fill = WinoStyle.Dynamic(UIColor.White, WinoStyle.Hex(0x1F1F22)),
        Stroke = WinoStyle.ZoneStroke,
        Hidden = true
    };
    private readonly UIStackView _attachmentList = WinoLayout.VStack(2);
    private readonly WinoSurfaceView _attachmentsCard = new() { Fill = CardFill, CornerRadius = WinoStyle.ControlRadius, Hidden = true };

    private NSLayoutConstraint? _descriptionHeight;
    private IDisposable? _contentSizeObserver;
    private WebKitHtmlMailReaderSession? _descriptionReader;
    private Task _readerReady = Task.CompletedTask;
    private bool _readerDisposed;
    private bool _dark;
    private string? _renderedDescription;
    private int _descriptionVersion;
    private BindingScope? _attachmentBindings;

    // ------------------------------------------------------------ people

    private UIView BuildPeopleCard()
    {
        _peopleTitle.AccessibilityTraits = UIAccessibilityTrait.Header;
        _peopleList.Alignment = UIStackViewAlignment.Fill;
        var column = WinoLayout.Insets(WinoLayout.VStack(8, _peopleTitle, _peopleList), 10, 12, 10, 12);
        column.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(column, _peopleCard);
        return _peopleCard;
    }

    private void ApplyAttendees()
    {
        ClearArranged(_peopleList);
        var attendees = ViewModel.CurrentEvent?.Attendees;
        if (attendees is null || attendees.Count == 0)
        {
            _peopleCard.Hidden = true;
            return;
        }
        _peopleCard.Hidden = false;
        _peopleTitle.Text = $"{Translator.CalendarEventDetails_People} · {attendees.Count}";
        foreach (var attendee in attendees.ToArray()) _peopleList.AddArrangedSubview(AttendeeRow(attendee));
    }

    private static UIView AttendeeRow(CalendarEventAttendee attendee)
    {
        string email = attendee.Email ?? string.Empty;
        string display = string.IsNullOrWhiteSpace(attendee.Name) ? email : attendee.Name;
        var picture = new WinoContactPicture(28);
        picture.SetIdentity(display, email);
        var name = WinoStyle.Label(display, WinoStyle.Subheadline);
        WinoLayout.Priorities(name, UILayoutConstraintAxis.Horizontal, 250, 250);
        var children = new List<UIView> { picture, name };
        if (attendee.IsOrganizer)
        {
            var badge = new WinoSurfaceView { Fill = WinoStyle.Accent, CornerRadius = 4 };
            var badgeText = WinoStyle.Label(Translator.CalendarEventDetails_Organizer, WinoStyle.CaptionStrong, UIColor.White);
            WinoLayout.Fill(badgeText, badge, 1, 6, 1, 6);
            WinoLayout.Priorities(badge, UILayoutConstraintAxis.Horizontal, 1000, 1000);
            children.Add(badge);
        }
        children.Add(WinoLayout.Spacer());
        var (glyph, tint) = CalendarTileMapper.AttendeeStatusGlyph(attendee.AttendenceStatus);
        var status = new WinoIconView(glyph, 16, tint);
        WinoLayout.Size(status, 16, 16);
        children.Add(status);
        var row = WinoLayout.HStack(8, children.ToArray());
        row.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)36).Active = true;
        var parts = new List<string> { display };
        if (!string.Equals(display, email, StringComparison.OrdinalIgnoreCase) && email.Length > 0) parts.Add(email);
        if (attendee.IsOrganizer) parts.Add(Translator.CalendarEventDetails_Organizer);
        var statusText = CalendarTileMapper.AttendeeStatusText(attendee.AttendenceStatus);
        if (!string.IsNullOrEmpty(statusText)) parts.Add(statusText);
        WinoAccessibility.Element(row, UIAccessibilityTrait.None, string.Join(", ", parts));
        return row;
    }

    // ------------------------------------------------------------ description

    private UIView BuildDescriptionCard()
    {
        WinoLayout.Fill(_descriptionWeb, _descriptionCard, 1, 1, 1, 1);
        _descriptionHeight = _descriptionWeb.HeightAnchor.ConstraintEqualTo((nfloat)MinDescriptionHeight);
        _descriptionHeight.Active = true;
        _contentSizeObserver = _descriptionWeb.ScrollView.AddObserver("contentSize", NSKeyValueObservingOptions.New, _ => UpdateDescriptionHeight());
        return _descriptionCard;
    }

    private void PrepareDescriptionReader()
    {
        var reader = _descriptionReader = new WebKitHtmlMailReaderSession(_descriptionWeb);
        reader.Configure(ViewModel.ExternalLauncher);
        reader.OperationFailed += (_, exception) => ReportError(exception);
        _dark = IsDarkAppearance();
        ViewModel.IsDarkWebviewRenderer = _dark;
        _readerReady = PrepareReaderAsync(reader);
    }

    private async Task PrepareReaderAsync(WebKitHtmlMailReaderSession reader)
    {
        try
        {
            await reader.InitializeAsync();
            await reader.SetThemeAsync(_dark);
            await reader.SetReaderTypographyAsync($"{ViewModel.PreferencesService.ReaderFont}, sans-serif", ViewModel.PreferencesService.ReaderFontSize);
        }
        catch (Exception exception) { ReportError(exception); }
    }

    /// <summary>Renders the event description (HTML, or plain text wrapped in a paragraph); the same text renders once.</summary>
    private void ApplyDescription(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            _descriptionVersion++;
            _renderedDescription = null;
            _descriptionCard.Hidden = true;
            return;
        }
        _descriptionCard.Hidden = false;
        if (html == _renderedDescription) return;
        _renderedDescription = html;
        int version = ++_descriptionVersion;
        Observe(RenderDescriptionAsync(html, version));
    }

    private async Task RenderDescriptionAsync(string html, int version)
    {
        await _readerReady;
        if (version != _descriptionVersion || _readerDisposed || _descriptionReader is not { } reader) return;
        string document = html.Contains('<') ? html : $"<p>{WebUtility.HtmlEncode(html).Replace("\n", "<br>")}</p>";
        // A shorter description must be able to shrink the view: measure from a small frame again.
        if (_descriptionHeight is not null) _descriptionHeight.Constant = (nfloat)MinDescriptionHeight;
        await reader.RenderAsync(new HtmlMailReaderRequest(document, RemoteContentPolicy.ImagesAndFontsAllowed));
        UpdateDescriptionHeight();
    }

    private void UpdateDescriptionHeight()
    {
        if (_descriptionHeight is null || _readerDisposed) return;
        var height = Math.Max(MinDescriptionHeight, (double)_descriptionWeb.ScrollView.ContentSize.Height);
        if (Math.Abs(_descriptionHeight.Constant - height) < 1) return;
        _descriptionHeight.Constant = (nfloat)height;
    }

    private void AppearanceChanged()
    {
        var dark = IsDarkAppearance();
        if (dark == _dark || _descriptionReader is not { } reader || _readerDisposed) return;
        _dark = dark;
        ViewModel.IsDarkWebviewRenderer = dark;
        Observe(reader.SetThemeAsync(dark));
    }

    private async Task DisposeDescriptionReaderAsync()
    {
        if (_descriptionReader is not { } reader || _readerDisposed) return;
        _readerDisposed = true;
        _contentSizeObserver?.Dispose();
        _contentSizeObserver = null;
        try { await reader.DisposeAsync(); }
        catch (Exception exception) { ReportError(exception); }
    }

    // ------------------------------------------------------------ attachments

    private UIView BuildAttachmentsCard()
    {
        var title = WinoStyle.Label(Translator.CalendarEventDetails_Attachments, WinoStyle.Caption, WinoStyle.SecondaryText);
        title.AccessibilityTraits = UIAccessibilityTrait.Header;
        _attachmentList.Alignment = UIStackViewAlignment.Fill;
        var column = WinoLayout.Insets(WinoLayout.VStack(8, title, _attachmentList), 10, 12, 10, 12);
        column.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(column, _attachmentsCard);
        return _attachmentsCard;
    }

    /// <summary>
    /// Attachment rows like the mail reader: a tap opens, the context menu offers Open and Save, and the inline Open and
    /// Save buttons stay visible. A spinner replaces the buttons while busy.
    /// </summary>
    private void ApplyAttachments()
    {
        _attachmentBindings?.Dispose();
        _attachmentBindings = null;
        ClearArranged(_attachmentList);
        _attachmentsCard.Hidden = !ViewModel.HasAttachments;
        if (!ViewModel.HasAttachments || _released) return;
        var scope = _attachmentBindings = new BindingScope();
        foreach (var attachment in ViewModel.Attachments.ToArray()) _attachmentList.AddArrangedSubview(AttachmentRow(attachment, scope));
    }

    private UIView AttachmentRow(CalendarAttachmentViewModel attachment, BindingScope scope)
    {
        string fileName = attachment.FileName ?? string.Empty;
        var icon = new WinoIconView(WinoIconGlyph.Attachment, 16, WinoStyle.SecondaryText);
        WinoLayout.Size(icon, 16, 16);
        var name = WinoStyle.Label(fileName, WinoStyle.Subheadline);
        name.LineBreakMode = UILineBreakMode.MiddleTruncation;
        WinoLayout.Priorities(name, UILayoutConstraintAxis.Horizontal, 1, 250);
        var size = WinoStyle.Label(attachment.ReadableSize, WinoStyle.Caption, WinoStyle.TertiaryText);
        WinoLayout.Priorities(size, UILayoutConstraintAxis.Horizontal, 750, 750);
        var open = InlineButton(Translator.Buttons_Open, $"{Translator.Buttons_Open} {fileName}", () => OpenAttachment(attachment));
        var save = InlineButton(Translator.Buttons_Save, $"{Translator.Buttons_Save} {fileName}", () => SaveAttachment(attachment));
        var spinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium) { TranslatesAutoresizingMaskIntoConstraints = false, HidesWhenStopped = true };
        var content = WinoLayout.HStack(8, icon, name, size, spinner, open, save);

        var row = new WinoSurfaceView();
        WinoLayout.Fill(content, row);
        row.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)40).Active = true;
        // Buttons keep their own taps; a tap elsewhere on the row opens the file.
        row.AddGestureRecognizer(new UITapGestureRecognizer(() => OpenAttachment(attachment)));
        row.AddInteraction(new UIContextMenuInteraction(new AttachmentMenuInteraction(() => AttachmentMenu(attachment))));
        name.IsAccessibilityElement = true;
        name.AccessibilityTraits = UIAccessibilityTrait.Button;
        name.AccessibilityLabel = $"{fileName}, {attachment.ReadableSize}";

        scope.Own(new PropertyBinding<CalendarAttachmentViewModel, bool>(attachment, nameof(attachment.IsBusy), a => a.IsBusy, busy =>
        {
            open.Hidden = busy;
            save.Hidden = busy;
            if (busy) spinner.StartAnimating(); else spinner.StopAnimating();
        }, Dispatcher, ReportError));
        return row;
    }

    private static UIButton InlineButton(string title, string accessibilityLabel, Action action)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Title = title;
        configuration.ButtonSize = UIButtonConfigurationSize.Small;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(6, 6, 6, 6);
        var button = new UIButton(UIButtonType.System) { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Priorities(button, UILayoutConstraintAxis.Horizontal, 1000, 1000);
        WinoAccessibility.Label(button, accessibilityLabel);
        button.PrimaryActionTriggered += (_, _) => action();
        return button;
    }

    private UIMenu AttachmentMenu(CalendarAttachmentViewModel attachment)
    {
        var open = UIAction.Create(Translator.Buttons_Open, WinoIcons.Image(WinoIconGlyph.Open, 18), null, _ => OpenAttachment(attachment));
        var save = UIAction.Create(Translator.Buttons_Save, WinoIcons.Image(WinoIconGlyph.Save, 18), null, _ => SaveAttachment(attachment));
        if (attachment.IsBusy)
        {
            open.Attributes = UIMenuElementAttributes.Disabled;
            save.Attributes = UIMenuElementAttributes.Disabled;
        }
        return UIMenu.Create([open, save]);
    }

    private void OpenAttachment(CalendarAttachmentViewModel attachment)
    {
        if (!attachment.IsBusy) Observe(ViewModel.OpenAttachmentCommand.ExecuteAsync(attachment));
    }

    private void SaveAttachment(CalendarAttachmentViewModel attachment)
    {
        if (!attachment.IsBusy) Observe(ViewModel.SaveAttachmentCommand.ExecuteAsync(attachment));
    }

    private static void ClearArranged(UIStackView stack)
    {
        foreach (var view in stack.ArrangedSubviews)
        {
            stack.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
            view.Dispose();
        }
    }

    // ------------------------------------------------------------ helper views

    /// <summary>A glyph + wrapping label row (Clock, Location, Reminder, Calendar) that hides itself when empty.</summary>
    private sealed class EventDetailRow : UIView
    {
        private readonly UILabel _label;

        public EventDetailRow(WinoIconGlyph glyph)
        {
            TranslatesAutoresizingMaskIntoConstraints = false;
            var icon = new WinoIconView(glyph, 18, WinoStyle.SecondaryText);
            WinoLayout.Size(icon, 18, 18);
            _label = WinoStyle.Label(string.Empty, WinoStyle.Subheadline, WinoStyle.SecondaryText, 0);
            var stack = WinoLayout.HStack(10, icon, _label);
            stack.Alignment = UIStackViewAlignment.Top;
            WinoLayout.Fill(stack, this);
            Hidden = true;
        }

        public string? Text
        {
            get => _label.Text;
            set
            {
                _label.Text = value ?? string.Empty;
                Hidden = string.IsNullOrWhiteSpace(value);
            }
        }
    }

    /// <summary>The description web view: transparent over its card, as tall as its document (the page scroll owns vertical scrolling), horizontal panning kept for wide content.</summary>
    private sealed class DescriptionWebView : WKWebView
    {
        public DescriptionWebView() : base(CGRect.Empty, new WKWebViewConfiguration())
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

    /// <summary>The attachment row's Open and Save context menu.</summary>
    private sealed class AttachmentMenuInteraction(Func<UIMenu> menu) : UIContextMenuInteractionDelegate
    {
        public override UIContextMenuConfiguration? GetConfigurationForMenu(UIContextMenuInteraction interaction, CGPoint location)
            => UIContextMenuConfiguration.Create(null, null, _ => menu());
    }
}
