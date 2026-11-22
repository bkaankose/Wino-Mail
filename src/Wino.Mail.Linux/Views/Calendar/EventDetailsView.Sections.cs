using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Presentation;
using Wino.Editor;
using Wino.Editor.WebKitGtk;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Calendar;
using Wino.Mail.ViewModels.Calendar.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>The detail rows, the people card, the notes (rendered by a reader web session, Windows) and the attachments.</summary>
internal sealed partial class EventDetailsView
{
    private const int NotesHeight = 240;

    private readonly DetailRow _when = new(WinoIconGlyph.Clock);
    private readonly DetailRow _location = new(WinoIconGlyph.Location);
    private readonly DetailRow _online = new(WinoIconGlyph.EventJoinOnline);
    private readonly DetailRow _repeat = new(WinoIconGlyph.CalendarEventRepeat);
    private readonly DetailRow _reminderRow = new(WinoIconGlyph.Reminder);
    private readonly DetailRow _calendar = new(WinoIconGlyph.Calendar);
    private readonly Gtk.Box _peopleCard = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
    private readonly Gtk.Label _peopleTitle = Gtk.Label.New(string.Empty);
    private readonly Gtk.Box _peopleList = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
    private readonly Adw.Bin _notesHost = Adw.Bin.New();
    private readonly Gtk.Box _attachmentsCard = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
    private readonly Gtk.Box _attachmentList = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
    private BindingScope? _attachmentScope;
    private WebKitGtkHtmlMailReaderSession? _notes;
    private int _notesVersion;
    private string? _renderedNotes;

    private void BuildSections()
    {
        var details = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        foreach (var row in (DetailRow[])[_when, _location, _online, _repeat, _reminderRow, _calendar]) details.Append(row.Root);
        _column.Append(details);
        BuildLinks();

        Card(_peopleCard, _peopleTitle, _peopleList);
        _column.Append(_peopleCard);

        _notesHost.SetSizeRequest(-1, NotesHeight);
        _notesHost.AddCssClass("card");
        _notesHost.SetOverflow(Gtk.Overflow.Hidden);
        _notesHost.SetVisible(false);
        _column.Append(_notesHost);

        var attachmentsTitle = Gtk.Label.New(Translator.CalendarEventDetails_Attachments);
        Card(_attachmentsCard, attachmentsTitle, _attachmentList);
        _column.Append(_attachmentsCard);
    }

    private static void Card(Gtk.Box card, Gtk.Label title, Gtk.Box list)
    {
        card.AddCssClass("card");
        title.SetXalign(0);
        title.AddCssClass("caption");
        title.AddCssClass("dim-label");
        foreach (var part in (Gtk.Widget[])[title, list])
        {
            part.SetMarginStart(12);
            part.SetMarginEnd(12);
        }
        title.SetMarginTop(10);
        list.SetMarginBottom(10);
        card.Append(title);
        card.Append(list);
        card.SetVisible(false);
    }

    private void ApplyEventFields(CalendarItemViewModel item)
    {
        var settings = ViewModel.CurrentSettings;
        _title.SetText(string.IsNullOrWhiteSpace(item.Title) ? Translator.MailItemNoSubject : item.Title);
        _dot.SetColor(CalendarAgenda.ColorHex(item), item.AssignedCalendar?.Name);
        _when.Text = CalendarEventDisplay.FormatDateRange(item.Period.Start, item.Period.End, item.IsAllDayEvent, item.IsMultiDayEvent, settings);
        _location.Text = item.Location;
        var hasJoinLink = CalendarJoinLinkResolver.TryGetEffectiveJoinUri(item.CalendarItem, out var uri);
        _online.Text = hasJoinLink ? uri?.Host : null;
        _join.SetSensitive(hasJoinLink);
        _repeat.Text = item.IsRecurringEvent ? CalendarEventDisplay.FormatRecurrence(item.CalendarItem.Recurrence, item.Period.Start, item.Period.End, item.IsAllDayEvent, settings) : null;
        if (string.IsNullOrEmpty(_repeat.Text) && item.CalendarItem.RecurringCalendarItemId is { } seriesId) Observe(ApplySeriesRecurrenceAsync(item, seriesId));
        var calendar = item.AssignedCalendar;
        _calendar.Text = calendar is null ? null : string.IsNullOrEmpty(calendar.MailAccount?.Name) ? calendar.Name : $"{calendar.Name} · {calendar.MailAccount.Name}";
        UpdateReminderRow();
        ApplyNotes(item.CalendarItem.Description);
        ApplyReadOnly(item);
    }

    /// <summary>An occurrence stores no rule of its own; the row shows its series' rule, as the series itself does.</summary>
    private async Task ApplySeriesRecurrenceAsync(CalendarItemViewModel item, Guid seriesId)
    {
        if (_services.GetService<ICalendarService>() is not { } calendarService) return;
        var series = await calendarService.GetCalendarItemAsync(seriesId);
        if (series is null || string.IsNullOrEmpty(series.Recurrence)) return;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (IsReleased || !ReferenceEquals(ViewModel.CurrentEvent, item)) return;
            _repeat.Text = CalendarEventDisplay.FormatRecurrence(series.Recurrence, item.Period.Start, item.Period.End, item.IsAllDayEvent, ViewModel.CurrentSettings);
        });
    }

    private void ApplyAttendees()
    {
        while (_peopleList.GetFirstChild() is { } child) _peopleList.Remove(child);
        var attendees = ViewModel.CurrentEvent?.Attendees;
        var count = attendees?.Count ?? 0;
        _peopleCard.SetVisible(count > 0);
        if (attendees is null || count == 0) return;
        _peopleTitle.SetText($"{Translator.CalendarEventDetails_People} · {count}");
        foreach (var attendee in attendees) _peopleList.Append(AttendeeRow(attendee));
    }

    private static Gtk.Widget AttendeeRow(CalendarEventAttendee attendee)
    {
        var displayName = string.IsNullOrWhiteSpace(attendee.Name) ? attendee.Email : attendee.Name;
        var picture = new WinoContactPicture(24);
        picture.SetIdentity(displayName, attendee.Email);
        var name = Gtk.Label.New(displayName ?? string.Empty);
        name.SetXalign(0);
        name.SetEllipsize(Pango.EllipsizeMode.End);
        name.SetTooltipText(attendee.Email);
        name.SetHexpand(true);
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        row.SetSizeRequest(-1, 30);
        row.Append(picture.Root);
        row.Append(name);
        if (attendee.IsOrganizer)
        {
            var badge = Gtk.Label.New(Translator.CalendarEventDetails_Organizer);
            badge.AddCssClass("caption");
            badge.AddCssClass("wino-pill");
            badge.SetValign(Gtk.Align.Center);
            row.Append(badge);
        }
        var (glyph, tone) = AttendeeStatusGlyph(attendee.AttendenceStatus);
        var status = WinoIcons.Label(glyph, 14);
        status.AddCssClass(tone);
        var statusText = CalendarEventDisplay.GetAttendeeStatusText(attendee.AttendenceStatus);
        status.SetTooltipText(statusText);
        WinoA11y.Label(status, statusText);
        row.Append(status);
        WinoA11y.Label(row, $"{displayName}, {statusText}");
        return row;
    }

    private string DescribeAttendees()
        => ViewModel.CurrentEvent?.Attendees is { Count: > 0 } attendees
            ? "[" + string.Join("; ", attendees.Select(static a => $"{a.Email}{(a.IsOrganizer ? " organizer" : string.Empty)} {a.AttendenceStatus}")) + "]"
            : "[]";

    // ------------------------------------------------------------ notes

    private void InitializeNotes()
    {
        Bindings.Own(GtkSignals.OnNotify(Adw.StyleManager.GetDefault(), "dark", () =>
        {
            if (_notes is { } session) Observe(session.SetThemeAsync(Adw.StyleManager.GetDefault().GetDark()));
        }));
    }

    private void ApplyNotes(string? description)
    {
        if (IsReleased) return;
        if (string.IsNullOrWhiteSpace(description))
        {
            _notesHost.SetVisible(false);
            _renderedNotes = null;
            return;
        }
        if (string.Equals(description, _renderedNotes, StringComparison.Ordinal) && _notes is not null) return;
        _renderedNotes = description;
        _notesHost.SetVisible(true);
        Observe(RenderNotesAsync(description, ++_notesVersion));
    }

    private async Task RenderNotesAsync(string description, int version)
    {
        var session = _notes;
        if (session is null)
        {
            session = _notes = new WebKitGtkHtmlMailReaderSession(Dispatcher);
            session.Configure(ViewModel.ExternalLauncher);
            session.LinkOpener = (uri, _) => ViewModel.OpenLinkAsync(uri);
            session.OperationFailed += (_, exception) => ReportError(exception);
            session.View.SetVexpand(true);
            WinoA11y.Label(session.View, Translator.CalendarEventCompose_Notes);
            _notesHost.SetChild(session.View);
            await session.InitializeAsync();
        }
        var preferences = ViewModel.PreferencesService;
        await session.SetThemeAsync(Adw.StyleManager.GetDefault().GetDark());
        await session.SetReaderTypographyAsync(preferences.ReaderFont, preferences.ReaderFontSize);
        if (version != _notesVersion || IsReleased) return;
        var html = description.Contains('<') ? description : $"<p>{WebUtility.HtmlEncode(description).Replace("\n", "<br>")}</p>";
        await session.RenderAsync(new HtmlMailReaderRequest(html, RemoteContentPolicy.ImagesAndFontsAllowed));
    }

    private async Task DisposeNotesAsync()
    {
        if (_notes is not { } session) return;
        _notes = null;
        await Dispatcher.ExecuteOnUIThread(() => _notesHost.SetChild(null));
        await session.DisposeAsync();
    }

    // ------------------------------------------------------------ attachments

    private void ApplyAttachments()
    {
        _attachmentScope?.Dispose();
        _attachmentScope = Bindings.Own(new BindingScope());
        while (_attachmentList.GetFirstChild() is { } child) _attachmentList.Remove(child);
        _attachmentsCard.SetVisible(ViewModel.HasAttachments);
        foreach (var attachment in ViewModel.Attachments) _attachmentList.Append(AttachmentRow(attachment, _attachmentScope));
    }

    private Gtk.Widget AttachmentRow(CalendarAttachmentViewModel attachment, BindingScope scope)
    {
        var icon = AttachmentFileIcons.Image(attachment.FileName, 28);
        var name = Gtk.Label.New(attachment.FileName);
        name.SetXalign(0);
        name.SetEllipsize(Pango.EllipsizeMode.Middle);
        var details = Gtk.Label.New(attachment.DetailsText);
        details.SetXalign(0);
        details.AddCssClass("caption");
        details.AddCssClass("dim-label");
        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 1);
        text.SetHexpand(true);
        text.SetValign(Gtk.Align.Center);
        text.Append(name);
        text.Append(details);
        var spinner = Adw.Spinner.New();
        spinner.SetVisible(false);
        var open = SmallButton(Translator.Buttons_Open, $"{Translator.Buttons_Open} {attachment.FileName}", () => OpenAttachment(attachment), scope);
        var save = SmallButton(Translator.Buttons_Save, $"{Translator.Buttons_Save} {attachment.FileName}", () => SaveAttachment(attachment), scope);
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        row.SetSizeRequest(-1, 42);
        foreach (var part in (Gtk.Widget[])[icon, text, spinner, open, save]) row.Append(part);
        row.SetTooltipText(attachment.FileName);
        WinoA11y.Label(row, $"{attachment.FileName}, {attachment.DetailsText}");
        scope.Own(new PropertyBinding<CalendarAttachmentViewModel, bool>(attachment, nameof(CalendarAttachmentViewModel.IsBusy), static a => a.IsBusy, busy =>
        {
            open.SetVisible(!busy);
            save.SetVisible(!busy);
            spinner.SetVisible(busy);
        }, Dispatcher, ReportError));
        scope.Own(ContextMenu.Attach(row, menu => menu
            .Item(Translator.Buttons_Open, () => OpenAttachment(attachment), !attachment.IsBusy, icon: WinoIconGlyph.Open)
            .Item(Translator.Buttons_Save, () => SaveAttachment(attachment), !attachment.IsBusy, icon: WinoIconGlyph.Save)));
        return row;
    }

    private static Gtk.Button SmallButton(string text, string name, Action run, BindingScope scope)
    {
        var button = Gtk.Button.NewWithLabel(text);
        button.AddCssClass("flat");
        button.SetValign(Gtk.Align.Center);
        WinoA11y.Rename(button, name);
        scope.Own(GtkSignals.OnClicked(button, run));
        return button;
    }

    internal void OpenAttachment(CalendarAttachmentViewModel attachment)
    {
        if (!attachment.IsBusy) Observe(ViewModel.OpenAttachmentCommand.ExecuteAsync(attachment));
    }

    internal void SaveAttachment(CalendarAttachmentViewModel attachment)
    {
        if (!attachment.IsBusy) Observe(ViewModel.SaveAttachmentCommand.ExecuteAsync(attachment));
    }

    /// <summary>A glyph and a wrapping, selectable label; hidden while its text is empty.</summary>
    private sealed class DetailRow
    {
        private readonly Gtk.Label _label = Gtk.Label.New(string.Empty);

        public DetailRow(WinoIconGlyph glyph)
        {
            var icon = WinoIcons.Label(glyph, 16);
            icon.AddCssClass("dim-label");
            icon.SetValign(Gtk.Align.Start);
            _label.SetWrap(true);
            _label.SetXalign(0);
            _label.SetSelectable(true);
            _label.SetHexpand(true);
            _label.SetLines(3);
            _label.SetEllipsize(Pango.EllipsizeMode.End);
            Root.Append(icon);
            Root.Append(_label);
            Root.SetVisible(false);
        }

        public Gtk.Box Root { get; } = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);

        public string? Text
        {
            get => _label.GetText();
            set
            {
                _label.SetText(value ?? string.Empty);
                Root.SetVisible(!string.IsNullOrWhiteSpace(value));
            }
        }
    }
}
