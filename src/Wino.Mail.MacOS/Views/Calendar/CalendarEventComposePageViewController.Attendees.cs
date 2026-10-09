using AppKit;
using EmailValidation;
using Wino.Core.Domain.Entities.Shared;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.MacOS.Views.Shell;

namespace Wino.Mail.MacOS.Views.Calendar;

/// <summary>
/// The attendee field (Windows InviteBox AutoSuggestBox): contact suggestions under the field after
/// 120 ms and two characters, ↑/↓/Return/Esc for the list, and typed or pasted addresses split on
/// ; , and whitespace. Return reports invalid addresses; leaving the field adds what it can quietly.
/// </summary>
public sealed partial class CalendarEventComposePageViewController
{
    private static readonly char[] AddressSeparators = [';', ',', ' ', '\t', '\r', '\n'];
    private const int SuggestionLimit = 8;

    private ShellSearchSuggestionList? _inviteSuggestions;
    private CancellationTokenSource? _inviteSearch;
    private bool _closing;

    private void ConfigureInviteField()
    {
        _inviteSuggestions = new ShellSearchSuggestionList();
        _inviteSuggestions.Chosen += InviteSuggestionChosen;
        _invite.Changed += (_, _) => ScheduleInviteSearch();
        _invite.DoCommandBySelector = (_, _, selector) => InviteCommand(selector.Name);
        _invite.EditingEnded += (_, _) => InviteEditingEnded();
    }

    private bool InviteCommand(string selector)
    {
        var list = _inviteSuggestions;
        switch (selector)
        {
            case "moveDown:": return list?.Move(1) == true;
            case "moveUp:": return list?.Move(-1) == true;
            case "insertNewline:":
                if (list?.TryChoose() == true) return true;
                list?.Close();
                Observe(AddTypedAttendeesAsync(notifyErrors: true));
                return true;
            case "cancelOperation:":
                if (list?.IsVisible != true) return false;
                list.Close();
                return true;
            case "insertTab:":
            case "insertBacktab:":
                list?.Close();
                return false;
            default:
                return false;
        }
    }

    /// <summary>Windows adds typed addresses when the box loses focus, without error messages; not while the sheet closes.</summary>
    private void InviteEditingEnded()
    {
        CancelInviteSearch();
        _inviteSuggestions?.Close();
        if (_closing || _released) return;
        Observe(AddTypedAttendeesAsync(notifyErrors: false));
    }

    private void InviteSuggestionChosen(object? sender, ShellSearchSuggestion suggestion)
    {
        if (suggestion.Tag is not AccountContact contact) return;
        CancelInviteSearch();
        _invite.StringValue = string.Empty;
        Observe(TryAddAttendeeAsync(contact.Address, notifyErrors: true));
    }

    private void ScheduleInviteSearch()
    {
        CancelInviteSearch();
        var text = _invite.StringValue?.Trim() ?? string.Empty;
        if (text.Length < 2) { _inviteSuggestions?.Close(); return; }
        var search = new CancellationTokenSource();
        _inviteSearch = search;
        Observe(SearchInviteAsync(text, search.Token));
    }

    private void CancelInviteSearch()
    {
        _inviteSearch?.Cancel();
        _inviteSearch?.Dispose();
        _inviteSearch = null;
    }

    private async Task SearchInviteAsync(string text, CancellationToken token)
    {
        try { await Task.Delay(120, token); }
        catch (OperationCanceledException) { return; }
        var contacts = await ViewModel.SearchContactsAsync(text);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (token.IsCancellationRequested || _released || _closing || _inviteSuggestions is null) return;
            var rows = contacts
                .Where(contact => !string.IsNullOrWhiteSpace(contact.Address)
                    && !ViewModel.Attendees.Any(attendee => string.Equals(attendee.Email, contact.Address, StringComparison.OrdinalIgnoreCase)))
                .DistinctBy(contact => contact.Address, StringComparer.OrdinalIgnoreCase)
                .Take(SuggestionLimit)
                .Select(SuggestionFor)
                .ToList();
            _inviteSuggestions.Show(_invite, rows);
        });
    }

    private static ShellSearchSuggestion SuggestionFor(AccountContact contact)
    {
        bool hasName = !string.IsNullOrWhiteSpace(contact.Name);
        var picture = new WinoContactPicture(28);
        picture.SetIdentity(hasName ? contact.Name : contact.Address, contact.Address);
        return new ShellSearchSuggestion(hasName ? contact.Name : contact.Address, hasName ? contact.Address : null, contact, Leading: picture);
    }

    /// <summary>One address or a pasted list; what could not be added stays in the field to correct.</summary>
    private async Task AddTypedAttendeesAsync(bool notifyErrors)
    {
        var addresses = (_invite.StringValue ?? string.Empty).Split(AddressSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (addresses.Length == 0) return;
        var rejected = new List<string>();
        foreach (var address in addresses)
            if (!await TryAddAttendeeAsync(address, notifyErrors)) rejected.Add(address);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (_released) return;
            _invite.StringValue = string.Join("; ", rejected);
        });
    }

    private async Task<bool> TryAddAttendeeAsync(string address, bool notifyErrors)
    {
        if (!EmailValidator.Validate(address))
        {
            if (notifyErrors) ViewModel.NotifyInvalidEmail(address);
            return false;
        }
        var attendee = await ViewModel.GetAttendeeAsync(address);
        if (attendee is null)
        {
            // Already in the list: nothing left to correct.
            if (notifyErrors) ViewModel.NotifyAddressExists();
            return true;
        }
        await Dispatcher.ExecuteOnUIThread(() => ViewModel.AddAttendee(attendee));
        return true;
    }

    private void ReleaseInvite()
    {
        CancelInviteSearch();
        if (_inviteSuggestions is { } list)
        {
            _inviteSuggestions = null;
            list.Chosen -= InviteSuggestionChosen;
            list.Dispose();
        }
    }
}
