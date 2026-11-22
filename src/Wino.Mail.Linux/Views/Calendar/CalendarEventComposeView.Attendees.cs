using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Compose;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Calendar;
using Wino.Mail.ViewModels.Calendar.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// Attendees in the side pane: the organizer line, then the mail composer's recipient token box (ED-03) with its
/// contact suggestions (ED-04). A chip stands for one attendee of the ViewModel; its menu switches Required and
/// Optional and removes it. Enter adds the typed addresses and reports an invalid one; leaving the field adds what
/// is valid without a message (Windows).
/// </summary>
internal sealed partial class CalendarEventComposeView
{
    private const uint KeyUp = 0xff52, KeyDown = 0xff54, KeyReturn = 0xff0d, KeyPadEnter = 0xff8d, KeyTab = 0xff09, KeyEscape = 0xff1b;

    private readonly Gtk.Label _attendeesTitle = Gtk.Label.New(Translator.CalendarEventCompose_Attendees);
    private readonly Gtk.Label _organizer = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _attendeeNotice = Gtk.Label.New(string.Empty);
    private WinoRecipientTokenBox? _invite;
    private WinoRecipientSuggestionPopup? _attendeeSuggestions;
    private CancellationTokenSource? _suggestionDelay;
    private int _suggestionVersion;
    private Wino.Presentation.Bindings.BindingScope? _attendeeScope;

    partial void BuildAttendees(Gtk.Box host)
    {
        var header = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var icon = WinoIcons.Label(WinoIconGlyph.CalendarAttendees, 16);
        icon.AddCssClass("dim-label");
        _attendeesTitle.AddCssClass("heading");
        _attendeesTitle.SetXalign(0);
        header.Append(icon);
        header.Append(_attendeesTitle);
        _organizer.AddCssClass("caption");
        _organizer.AddCssClass("dim-label");
        _organizer.SetXalign(0);
        _organizer.SetEllipsize(Pango.EllipsizeMode.End);
        _attendeeNotice.AddCssClass("caption");
        _attendeeNotice.AddCssClass("dim-label");
        _attendeeNotice.SetXalign(0);
        _attendeeNotice.SetWrap(true);

        var box = _invite = new WinoRecipientTokenBox("attendees", Translator.CalendarEventDetails_InviteSomeone, "calendar-attendees");
        box.Entry.SetPlaceholderText(Translator.CalendarEventDetails_InviteSomeone);
        box.Parse = static text => ComposeRecipientRules.TryParseTyped(text, out var address, out var name) ? new RecipientToken(address, name) : null;
        box.Validate = (address, _) =>
        {
            if (!ViewModel.Attendees.Any(attendee => attendee.Email.Equals(address, StringComparison.OrdinalIgnoreCase))) return true;
            ViewModel.NotifyAddressExists();
            return false;
        };
        box.Refused += (_, text) => ViewModel.NotifyInvalidEmail(text);
        box.Committed += (sender, address, name) => Observe(AddAttendeeAsync(sender, address, name));
        box.Removed += (_, address) =>
        {
            if (ViewModel.Attendees.FirstOrDefault(attendee => attendee.Email.Equals(address, StringComparison.OrdinalIgnoreCase)) is { } attendee)
                ViewModel.RemoveAttendeeCommand.Execute(attendee);
        };
        box.ChipMenuRequested += ShowAttendeeMenu;
        box.TextChanged += InviteTextChanged;
        box.FocusLeft += _ =>
        {
            CloseAttendeeSuggestions();
            if (ComposeRecipientRules.IsCompleteAddress(box.TypedText)) box.CommitTyped();
        };
        box.KeyFilter = HandleInviteKey;
        Bindings.Own(box);

        var popup = _attendeeSuggestions = new WinoRecipientSuggestionPopup();
        popup.Chosen += item => Observe(ChooseSuggestionAsync(item));

        host.SetSpacing(8);
        host.Append(header);
        host.Append(_organizer);
        host.Append(box.Root);
        host.Append(_attendeeNotice);

        Bind.BindCollection(ViewModel.Attendees, ApplyAttendees);
        Bind.BindAny(static vm => (vm.SelectedCalendar, vm.OrganizerDisplayName, vm.OrganizerAddress, vm.CanAddAttendees, vm.IsAttendeeLimitReached, vm.InvitationsNotSentText, vm.AttendeeLimitText, vm.HasAttendeeLimit), _ => ApplyAttendeeState());
    }

    partial void ReleaseAttendees()
    {
        CloseAttendeeSuggestions();
        _attendeeSuggestions?.Dispose();
        _attendeeSuggestions = null;
    }

    private void ApplyAttendees()
    {
        if (_invite is not { } box) return;
        box.SetRecipients([.. ViewModel.Attendees.Select(static attendee => new RecipientToken(attendee.Email,
            attendee.HasDistinctDisplayName ? attendee.DisplayName : null))]);
        _attendeeScope?.Dispose();
        var scope = _attendeeScope = Bindings.Own(new Wino.Presentation.Bindings.BindingScope());
        foreach (var attendee in ViewModel.Attendees)
        {
            var captured = attendee;
            scope.Own(new Wino.Presentation.Bindings.PropertyBinding<CalendarComposeAttendeeViewModel, bool>(attendee, nameof(CalendarComposeAttendeeViewModel.IsOptional), static a => a.IsOptional,
                optional => box.SetBadge(captured.Email, optional ? captured.AttendanceTypeText : null), Dispatcher, ReportError));
        }
        var count = ViewModel.Attendees.Count;
        _attendeesTitle.SetText(count > 0 ? $"{Translator.CalendarEventCompose_Attendees} {count}" : Translator.CalendarEventCompose_Attendees);
        ApplyAttendeeState();
    }

    private void ApplyAttendeeState()
    {
        _organizer.SetText(ViewModel.SelectedCalendar is null ? string.Empty : $"{Translator.CalendarEventCompose_Organizer}: {ViewModel.OrganizerDisplayName} <{ViewModel.OrganizerAddress}>");
        _organizer.SetVisible(ViewModel.SelectedCalendar is not null);
        _invite?.Root.SetSensitive(ViewModel.CanAddAttendees);
        _invite?.Entry.SetSensitive(!ViewModel.IsAttendeeLimitReached);
        var notice = !string.IsNullOrEmpty(ViewModel.InvitationsNotSentText) ? ViewModel.InvitationsNotSentText
            : ViewModel.HasAttendeeLimit ? ViewModel.AttendeeLimitText
            : !ViewModel.CanAddAttendees ? Translator.CalendarCapability_AttendeesNotSupported : string.Empty;
        _attendeeNotice.SetText(notice ?? string.Empty);
        _attendeeNotice.SetVisible(!string.IsNullOrEmpty(notice));
    }

    /// <summary>A committed chip becomes an attendee through the ViewModel, which may refuse it (limit, capability); the chips then follow the ViewModel.</summary>
    internal async Task AddAttendeeAsync(WinoRecipientTokenBox box, string address, string? name)
    {
        var attendee = await ViewModel.GetAttendeeAsync(address);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (IsReleased) return;
            if (attendee is not null)
            {
                if (attendee.DisplayName.Length == 0 && name is not null) attendee = new CalendarComposeAttendeeViewModel(name, attendee.Email, attendee.ResolvedContact);
                ViewModel.AddAttendee(attendee);
            }
            ApplyAttendees();
        });
    }

    private void ShowAttendeeMenu(WinoRecipientTokenBox box, WinoRecipientChip chip, double? x, double? y)
    {
        if (ViewModel.Attendees.FirstOrDefault(attendee => attendee.Email.Equals(chip.Address, StringComparison.OrdinalIgnoreCase)) is not { } attendee) return;
        var menu = WinoMenus.Create()
            .Check(Translator.CalendarEventCompose_AttendeeTypeTooltip, attendee.IsOptional, _ => ViewModel.ToggleAttendeeOptionalCommand.Execute(attendee))
            .Separator()
            .Item(Translator.CalendarEventCompose_RemoveAttendee, () => ViewModel.RemoveAttendeeCommand.Execute(attendee), icon: WinoIconGlyph.Delete, destructive: true);
        ContextMenu.Show(chip.Root, menu.Build(), x, y);
    }

    private void InviteTextChanged(WinoRecipientTokenBox box)
    {
        _suggestionDelay?.Cancel();
        _suggestionDelay?.Dispose();
        _suggestionDelay = null;
        var query = box.TypedText;
        if (!ComposeRecipientRules.ShouldSuggest(query))
        {
            CloseAttendeeSuggestions();
            return;
        }
        var version = ++_suggestionVersion;
        var delay = _suggestionDelay = new CancellationTokenSource();
        Observe(RefreshAttendeeSuggestionsAsync(box, query, version, delay.Token));
    }

    // A slower earlier search must not replace a newer one: only the latest version for the text still typed is shown.
    private async Task RefreshAttendeeSuggestionsAsync(WinoRecipientTokenBox box, string query, int version, CancellationToken token)
    {
        try { await Task.Delay(ComposeRecipientRules.SuggestionDelay, token); }
        catch (OperationCanceledException) { return; }
        var contacts = await ViewModel.SearchContactsAsync(query);
        if (token.IsCancellationRequested) return;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (IsReleased || version != _suggestionVersion || box.TypedText != query || _attendeeSuggestions is not { } popup) return;
            var items = contacts
                .Where(contact => !ViewModel.Attendees.Any(attendee => attendee.Email.Equals(contact.Address, StringComparison.OrdinalIgnoreCase)))
                .Take(8)
                .Select(contact => new RecipientSuggestionItem(contact.Address, contact.Name, null, query, false, false, contact))
                .ToArray();
            if (items.Length == 0) popup.Close();
            else popup.Show(box.Entry, items);
        });
    }

    private bool HandleInviteKey(uint keyval, Gdk.ModifierType state)
    {
        if (_invite is not { } box || _attendeeSuggestions is not { IsOpen: true } popup) return false;
        switch (keyval)
        {
            case KeyDown: return popup.MoveSelection(1);
            case KeyUp: return popup.MoveSelection(-1);
            case KeyReturn or KeyPadEnter or KeyTab:
                if (ComposeRecipientRules.IsCompleteAddress(box.TypedText) && popup.SelectedIndex < 0) return false;
                popup.Choose(Math.Max(0, popup.SelectedIndex));
                return true;
            case KeyEscape:
                CloseAttendeeSuggestions();
                return true;
            default: return false;
        }
    }

    private async Task ChooseSuggestionAsync(RecipientSuggestionItem item)
    {
        if (_invite is not { } box) return;
        CloseAttendeeSuggestions();
        box.Entry.SetText(string.Empty);
        await AddAttendeeAsync(box, item.Address, item.DisplayName);
    }

    private void CloseAttendeeSuggestions()
    {
        _suggestionDelay?.Cancel();
        _suggestionDelay?.Dispose();
        _suggestionDelay = null;
        _attendeeSuggestions?.Close();
    }

#if DEBUG
    internal string DescribeAttendees()
        => $"attendees=[{string.Join("; ", ViewModel.Attendees.Select(static a => $"{a.Email}{(a.IsOptional ? " optional" : string.Empty)}"))}] chips=[{string.Join(", ", _invite?.Addresses ?? [])}] badges=[{string.Join(", ", ViewModel.Attendees.Where(static a => a.IsOptional).Select(static a => a.AttendanceTypeText))}] sensitive={_invite?.Entry.GetSensitive()} notice='{_attendeeNotice.GetText()}' suggestions={_attendeeSuggestions?.Describe()}";

    internal void DebugTypeInvite(string text)
    {
        if (_invite is not { } box) return;
        box.Entry.SetText(text);
    }

    internal bool DebugCommitInvite() => _invite?.CommitTyped() ?? false;

    internal void DebugChooseSuggestion(int index) => _attendeeSuggestions?.Choose(index);

    internal void DebugAttendee(string command, int index)
    {
        if (index < 0 || index >= ViewModel.Attendees.Count) return;
        var attendee = ViewModel.Attendees[index];
        if (command == "optional") ViewModel.ToggleAttendeeOptionalCommand.Execute(attendee);
        else if (command == "remove") ViewModel.RemoveAttendeeCommand.Execute(attendee);
    }
#endif
}
