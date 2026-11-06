using MimeKit;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Contacts;
using Wino.Mail.UIKit.Views.Mail.Compose;

namespace Wino.Mail.UIKit.Views.Calendar;

/// <summary>
/// The invite field (Mac Attendees partial, Windows InviteBox AutoSuggestBox): contact suggestions under the field after
/// 120 ms and two characters (the mail composer's suggestion list), Return takes the first suggestion or the typed
/// addresses, and typed or pasted addresses split on ; , and whitespace. Return reports invalid addresses; leaving the
/// field adds what it can quietly. Attendees themselves are listed under the field, not as tokens.
/// </summary>
public sealed partial class CalendarEventComposePageViewController
{
    private static readonly char[] AddressSeparators = [';', ',', ' ', '\t', '\r', '\n'];
    private const int SuggestionLimit = 8;
    private const double SuggestionRowHeight = 56;
    private const int SuggestionVisibleRows = 5;

    private ComposeRecipientSuggestionList? _inviteSuggestions;
    private NSLayoutConstraint? _suggestionTop;
    private NSLayoutConstraint? _suggestionLeading;
    private NSLayoutConstraint? _suggestionWidth;
    private CancellationTokenSource? _inviteSearch;
    private int _inviteSearchVersion;
    private bool _closing;

    private void ConfigureInviteField()
    {
        _invite.TextField.Placeholder = Translator.CalendarEventDetails_InviteSomeone;
        _invite.TextField.AccessibilityLabel = Translator.CalendarEventDetails_InviteSomeone;
        _invite.TextField.ReturnKeyType = UIReturnKeyType.Done;
        _invite.TextChanged += (_, _) => ScheduleInviteSearch();
        // A typed or pasted ; or , : the text before it is one address (Mac splitting).
        _invite.CommitRequested += (_, text) => Observe(AddTypedAttendeesAsync(text, notifyErrors: true, keepRejected: false));
        _invite.ReturnPressed += (_, _) => InviteReturn();
        _invite.EditingEnded += (_, _) => InviteEditingEnded();
    }

    /// <summary>The suggestion list floats over the zone, so rows below the field stay tappable while it scrolls.</summary>
    private void BuildInviteSuggestions(UIView host)
    {
        var list = new ComposeRecipientSuggestionList(_pictures) { Hidden = true };
        list.Picked += InviteSuggestionPicked;
        host.AddSubview(list);
        var top = list.TopAnchor.ConstraintEqualTo(host.TopAnchor);
        var leading = list.LeadingAnchor.ConstraintEqualTo(host.LeadingAnchor);
        var width = list.WidthAnchor.ConstraintEqualTo(320);
        NSLayoutConstraint.ActivateConstraints([top, leading, width]);
        _suggestionTop = top;
        _suggestionLeading = leading;
        _suggestionWidth = width;
        _inviteSuggestions = list;
        _formScroll.Scrolled += (_, _) => PositionInviteSuggestions();
        _sideScroll.Scrolled += (_, _) => PositionInviteSuggestions();
    }

    /// <summary>Under the field, or above it when there is more room there (the keyboard is up).</summary>
    private void PositionInviteSuggestions()
    {
        if (_inviteSuggestions is not { IsVisible: true } list || list.Superview is not { } host
            || _suggestionTop is null || _suggestionLeading is null || _suggestionWidth is null) return;
        var frame = _invite.ConvertRectToView(_invite.Bounds, host);
        double fieldTop = frame.Y;
        double fieldBottom = frame.GetMaxY();
        double height = Math.Min(list.Items.Count, SuggestionVisibleRows) * SuggestionRowHeight;
        double below = (double)host.Bounds.Height - fieldBottom;
        double top = below >= height + 4 || below >= fieldTop ? fieldBottom + 2 : fieldTop - height - 2;
        _suggestionTop.Constant = (nfloat)top;
        _suggestionLeading.Constant = (nfloat)((double)frame.X + 6);
        _suggestionWidth.Constant = (nfloat)Math.Max(200.0, (double)frame.Width - 12);
    }

    private void InviteReturn()
    {
        if (_inviteSuggestions is { IsVisible: true, Highlighted: { } top })
        {
            CommitSuggestion(top);
            return;
        }
        CloseInviteSuggestions();
        Observe(AddTypedAttendeesAsync(_invite.TypedText, notifyErrors: true, keepRejected: true));
    }

    /// <summary>Windows adds typed addresses when the box loses focus, without error messages; not while the page closes.</summary>
    private void InviteEditingEnded()
    {
        CloseInviteSuggestions();
        if (_closing || _released) return;
        var typed = _invite.TypedText;
        if (typed.Length > 0) Observe(AddTypedAttendeesAsync(typed, notifyErrors: false, keepRejected: true));
    }

    private void InviteSuggestionPicked(object? sender, RecipientSuggestion suggestion) => CommitSuggestion(suggestion);

    private void CommitSuggestion(RecipientSuggestion suggestion)
    {
        CloseInviteSuggestions();
        _invite.ClearTypedText();
        if (string.IsNullOrWhiteSpace(suggestion.Address)) return;
        Observe(TryAddAttendeeAsync(suggestion.Address, notifyErrors: true));
    }

    private void ScheduleInviteSearch()
    {
        CancelInviteSearch();
        var version = ++_inviteSearchVersion;
        if (_invite.HasMarkedText) return;
        var text = _invite.TypedText;
        if (text.Length < 2)
        {
            _inviteSuggestions?.Close();
            return;
        }
        var search = new CancellationTokenSource();
        _inviteSearch = search;
        Observe(SearchInviteAsync(text, version, search.Token));
    }

    private void CancelInviteSearch()
    {
        _inviteSearch?.Cancel();
        _inviteSearch?.Dispose();
        _inviteSearch = null;
    }

    private void CloseInviteSuggestions()
    {
        CancelInviteSearch();
        _inviteSearchVersion++;
        _inviteSuggestions?.Close();
    }

    private async Task SearchInviteAsync(string text, int version, CancellationToken token)
    {
        try { await Task.Delay(120, token); }
        catch (OperationCanceledException) { return; }
        var contacts = await ViewModel.SearchContactsAsync(text);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            // A slower earlier query never replaces the result of a newer one.
            if (token.IsCancellationRequested || version != _inviteSearchVersion || _released || _closing || _inviteSuggestions is not { } list) return;
            if (!_invite.TextField.IsFirstResponder || _invite.TypedText != text)
            {
                list.Close();
                return;
            }
            var rows = (contacts ?? [])
                .Where(contact => contact is not null && !string.IsNullOrWhiteSpace(contact.Address)
                    && !ViewModel.Attendees.Any(attendee => string.Equals(attendee.Email, contact.Address, StringComparison.OrdinalIgnoreCase)))
                .DistinctBy(contact => contact.Address, StringComparer.OrdinalIgnoreCase)
                .Take(SuggestionLimit)
                .Select(contact => SuggestionFor(contact, text))
                .Where(suggestion => !suggestion.IsList)
                .ToList();
            list.Show(_invite, rows, text);
            PositionInviteSuggestions();
        });
    }

    /// <summary>
    /// The recipient suggestion service already returns rows; a plain contact (no suggestion service) is wrapped. The
    /// event ViewModel has no "Don't suggest" (that rule lives in the mail composer's ViewModel), so a remembered
    /// correspondent is shown without the button.
    /// </summary>
    private static RecipientSuggestion SuggestionFor(AccountContact contact, string query)
    {
        if (contact is RecipientSuggestion { CanSuppress: false } suggestion) return suggestion;
        if (contact is RecipientSuggestion history)
        {
            var row = new RecipientSuggestion
            {
                Source = RecipientSuggestionSource.Contact,
                SourceLabel = history.SourceLabel,
                MatchQuery = query,
                Score = history.Score,
                MailAccountId = history.MailAccountId,
                DisplayName = history.DisplayName,
                IsAutoCollected = true
            };
            row.Address = history.Address;
            return row;
        }
        return RecipientSuggestion.FromContact(contact, contact.Address, string.Empty, query, 0);
    }

    /// <summary>
    /// One address or a pasted list. With <paramref name="keepRejected"/> what could not be added stays in the field to
    /// correct (the text came from the field); otherwise the field is left as it is.
    /// </summary>
    private async Task AddTypedAttendeesAsync(string text, bool notifyErrors, bool keepRejected)
    {
        var addresses = (text ?? string.Empty).Split(AddressSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (addresses.Length == 0) return;
        var rejected = new List<string>();
        foreach (var address in addresses)
            if (!await TryAddAttendeeAsync(address, notifyErrors)) rejected.Add(address);
        if (!keepRejected) return;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (_released) return;
            _invite.ClearTypedText();
            if (rejected.Count > 0) _invite.TextField.Text = string.Join("; ", rejected);
        });
    }

    /// <summary>False when the address is invalid (it stays to correct); true when it was added or is already there.</summary>
    private async Task<bool> TryAddAttendeeAsync(string address, bool notifyErrors)
    {
        if (!MailboxAddress.TryParse(address, out var mailbox) || !mailbox.Address.Contains('@'))
        {
            if (notifyErrors) ViewModel.NotifyInvalidEmail(address);
            return false;
        }
        var exists = false;
        await Dispatcher.ExecuteOnUIThread(() => exists = ViewModel.Attendees.Any(attendee => string.Equals(attendee.Email, mailbox.Address, StringComparison.OrdinalIgnoreCase)));
        if (exists)
        {
            // Already in the list: nothing left to correct.
            if (notifyErrors) ViewModel.NotifyAddressExists();
            return true;
        }
        var attendee = await ViewModel.GetAttendeeAsync(mailbox.Address);
        if (attendee is null)
        {
            // The ViewModel's validator is stricter than MIME parsing.
            if (notifyErrors) ViewModel.NotifyInvalidEmail(address);
            return false;
        }
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (_released) return;
            ViewModel.AddAttendee(attendee);
        });
        return true;
    }

    private void ReleaseInvite()
    {
        CancelInviteSearch();
        if (_inviteSuggestions is { } list)
        {
            _inviteSuggestions = null;
            list.Picked -= InviteSuggestionPicked;
            list.Close();
        }
    }
}
