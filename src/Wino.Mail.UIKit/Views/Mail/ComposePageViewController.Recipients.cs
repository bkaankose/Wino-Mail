using System.Collections.ObjectModel;
using System.Collections.Specialized;
using MimeKit;
using UIKit;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Contacts;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views.Mail.Compose;
using Wino.Mail.ViewModels;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// To / Cc / Bcc token fields kept in step with the ViewModel collections, and the recipient suggestion list (Mac
/// Recipients partial, Windows ResolveRecipientSuggestionsAsync, TokenItemAdding, AddListMembers and
/// SuppressSuggestionClicked): contacts, remembered correspondents and contact lists that expand into their members,
/// with "Don't suggest" on remembered correspondents. The collections are the source of truth: typed text becomes a
/// recipient when it is committed, and removing a token removes the recipient.
/// </summary>
public sealed partial class ComposePageViewController
{
    private readonly Dictionary<string, string> _recipientNames = new(StringComparer.OrdinalIgnoreCase);
    private ComposeRecipientSuggestionList? _suggestionList;
    private NSLayoutConstraint[] _suggestionConstraints = [];
    private CancellationTokenSource? _suggestionDebounce;
    private int _suggestionVersion;
    private bool _suggestionPending;

    private void BuildRecipientFields()
    {
        foreach (var field in new[] { ToField, CcField, BccField })
        {
            var current = field;
            current.TextChanged += (_, _) => RecipientTextChanged(current);
            current.CommitRequested += (_, text) => Observe(CommitTypedTextAsync(current, text, true));
            current.ReturnPressed += (_, _) => Observe(ReturnInFieldAsync(current));
            current.EditingEnded += (_, _) =>
            {
                CloseSuggestions();
                // Leaving the field keeps a complete address as a recipient; anything else stays as typed.
                if (IsCompleteAddress(current.TypedText)) Observe(CommitTypedTextAsync(current, current.TypedText, false));
            };
            current.RemoveRequested += (_, contact) =>
            {
                CollectionFor(current).Remove(contact);
                ScheduleAutosave();
            };
        }
    }

    private ObservableCollection<AccountContact> CollectionFor(ComposeRecipientField field)
        => ReferenceEquals(field, _ccField) ? ViewModel.CCItems : ReferenceEquals(field, _bccField) ? ViewModel.BCCItems : ViewModel.ToItems;

    private void ObserveRecipients(ObservableCollection<AccountContact> collection, ComposeRecipientField field)
    {
        NotifyCollectionChangedEventHandler handler = (_, _) => _ = Dispatcher.ExecuteOnUIThread(() => { if (!Bindings.IsDisposed) WriteTokens(field, collection); });
        collection.CollectionChanged += handler;
        Bindings.Own(new ActionDisposable(() => collection.CollectionChanged -= handler));
        WriteTokens(field, collection);
    }

    private void WriteTokens(ComposeRecipientField field, IEnumerable<AccountContact> contacts)
    {
        var list = contacts.ToList();
        foreach (var contact in list)
            if (!string.IsNullOrWhiteSpace(contact.Address) && string.IsNullOrWhiteSpace(contact.Name) && _recipientNames.TryGetValue(contact.Address, out var name))
                contact.Name = name;
        field.SetContacts(list);
    }

    /// <summary>Commits what is still typed in each field (a complete address only), before saving or sending.</summary>
    private async Task SyncAllRecipientsAsync()
    {
        var pending = new List<(ComposeRecipientField Field, string Text)>();
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            foreach (var field in new[] { _toField, _ccField, _bccField })
                if (field is not null && !field.HasMarkedText && IsCompleteAddress(field.TypedText)) pending.Add((field, field.TypedText));
        });
        foreach (var (field, text) in pending) await CommitTypedTextAsync(field, text, false);
    }

    /// <summary>
    /// Adds the typed address (with an optional name, "Name &lt;address&gt;") to the field's collection and clears the
    /// typed text. An invalid address is reported (Windows NotifyInvalidEmail) when <paramref name="notifyInvalid"/>.
    /// </summary>
    private async Task CommitTypedTextAsync(ComposeRecipientField field, string text, bool notifyInvalid)
    {
        var typed = text.Trim();
        if (typed.Length == 0) return;
        if (!MailboxAddress.TryParse(typed, out var mailbox) || !mailbox.Address.Contains('@'))
        {
            if (notifyInvalid) ViewModel.NotifyInvalidEmail(typed);
            return;
        }
        if (!string.IsNullOrWhiteSpace(mailbox.Name)) _recipientNames[mailbox.Address] = mailbox.Name;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (string.Equals(field.TypedText, typed, StringComparison.Ordinal) || field.TypedText.Contains(typed, StringComparison.Ordinal)) field.ClearTypedText();
            CloseSuggestions();
        });
        await AddAddressesAsync(field, [mailbox.Address]);
    }

    /// <summary>Resolves each address (a known contact supplies the name) and adds it unless it is already there.</summary>
    private async Task AddAddressesAsync(ComposeRecipientField field, IReadOnlyList<string> addresses)
    {
        var collection = CollectionFor(field);
        var added = false;
        foreach (var address in addresses.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ComposePageViewModel.ContainsAddress(collection, address))
            {
                ViewModel.NotifyAddressExists();
                continue;
            }
            var recipient = await ViewModel.GetAddressInformationAsync(address, collection);
            if (recipient is null) continue;
            if (string.IsNullOrWhiteSpace(recipient.Name) && _recipientNames.TryGetValue(address, out var name)) recipient.Name = name;
            await Dispatcher.ExecuteOnUIThread(() => added |= ViewModel.TryAddRecipient(collection, recipient));
        }
        if (added) await Dispatcher.ExecuteOnUIThread(ScheduleAutosave);
    }

    private static bool IsCompleteAddress(string text)
        => MailboxAddress.TryParse(text, out var mailbox) && mailbox.Address.Contains('@') && mailbox.Address.Contains('.');

    /// <summary>
    /// Return: the highlighted suggestion unless a complete address is typed; while the search has not answered yet,
    /// search now and take the top suggestion (Windows GetTopSuggestionAsync); otherwise commit the text, or move on.
    /// </summary>
    private async Task ReturnInFieldAsync(ComposeRecipientField field)
    {
        var typed = field.TypedText;
        if (typed.Length == 0)
        {
            CloseSuggestions();
            MoveFocusAfter(field);
            return;
        }
        if (!IsCompleteAddress(typed))
        {
            if (_suggestionList is { IsVisible: true } list && ReferenceEquals(list.Anchor, field) && list.Highlighted is { } highlighted)
            {
                await CommitSuggestionAsync(field, highlighted);
                return;
            }
            if (_suggestionPending && typed.Length >= 2)
            {
                CloseSuggestions();
                var suggestions = await SuggestAsync(field, typed);
                if (suggestions.FirstOrDefault() is { } top && field.TypedText == typed)
                {
                    await CommitSuggestionAsync(field, top);
                    return;
                }
            }
        }
        await CommitTypedTextAsync(field, typed, true);
    }

    /// <summary>Return in an empty field: To → Cc (when shown) → Bcc → Subject.</summary>
    private void MoveFocusAfter(ComposeRecipientField field)
    {
        if (ReferenceEquals(field, _toField) && ViewModel.IsCCBCCVisible) CcField.Focus();
        else if (ReferenceEquals(field, _ccField)) BccField.Focus();
        else _subjectField.BecomeFirstResponder();
    }

    // ---- Suggestions ----

    private void BuildSuggestionList(UIView host)
    {
        _suggestionList = new ComposeRecipientSuggestionList(_pictures);
        host.AddSubview(_suggestionList);
    }

    private void BindRecipientSuggestions()
    {
        if (_suggestionList is not { } list) return;
        EventHandler<RecipientSuggestion> picked = (_, suggestion) =>
        {
            if (list.Anchor is { } field) Observe(CommitSuggestionAsync(field, suggestion));
        };
        EventHandler<RecipientSuggestion> suppress = (_, suggestion) => SuppressSuggestion(suggestion);
        list.Picked += picked;
        list.SuppressRequested += suppress;
        Bindings.Own(new ActionDisposable(() =>
        {
            list.Picked -= picked;
            list.SuppressRequested -= suppress;
        }));
    }

    private void ShowSuggestions(ComposeRecipientField field, IReadOnlyList<RecipientSuggestion> items, string query)
    {
        if (_suggestionList is not { } list) return;
        if (!ReferenceEquals(list.Anchor, field) || _suggestionConstraints.Length == 0)
        {
            NSLayoutConstraint.DeactivateConstraints(_suggestionConstraints);
            var host = _zone.ContentView;
            _suggestionConstraints =
            [
                list.TopAnchor.ConstraintEqualTo(field.BottomAnchor, 2),
                list.LeadingAnchor.ConstraintEqualTo(host.LeadingAnchor, 10),
                list.TrailingAnchor.ConstraintEqualTo(host.TrailingAnchor, -10)
            ];
            NSLayoutConstraint.ActivateConstraints(_suggestionConstraints);
        }
        list.Show(field, items, query);
    }

    private void CloseSuggestions()
    {
        _suggestionDebounce?.Cancel();
        _suggestionVersion++;
        _suggestionPending = false;
        _suggestionList?.Close();
    }

    private void DisposeRecipients()
    {
        CloseSuggestions();
        NSLayoutConstraint.DeactivateConstraints(_suggestionConstraints);
        _suggestionConstraints = [];
    }

    /// <summary>Typed text changed: suggest after 120 ms (at least two characters).</summary>
    private void RecipientTextChanged(ComposeRecipientField field)
    {
        if (Bindings.IsDisposed || field.HasMarkedText) return;
        var query = field.TypedText;
        _suggestionDebounce?.Cancel();
        var version = ++_suggestionVersion;
        if (query.Length < 2)
        {
            _suggestionPending = false;
            _suggestionList?.Close();
            return;
        }
        _suggestionPending = true;
        var source = _suggestionDebounce = new CancellationTokenSource();
        Observe(RefreshSuggestionsAsync(field, query, version, source.Token));
    }

    private async Task RefreshSuggestionsAsync(ComposeRecipientField field, string query, int version, CancellationToken token)
    {
        try { await Task.Delay(120, token); }
        catch (OperationCanceledException) { return; }
        var results = await SuggestAsync(field, query);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            // A slower earlier query never replaces the result of a newer one.
            if (version != _suggestionVersion || Bindings.IsDisposed) return;
            _suggestionPending = false;
            if (!field.TextField.IsFirstResponder || field.HasMarkedText || field.TypedText != query)
            {
                _suggestionList?.Close();
                return;
            }
            ShowSuggestions(field, results, query);
        });
    }

    /// <summary>Contacts, remembered correspondents and lists; someone already in this field is not suggested again.</summary>
    private async Task<List<RecipientSuggestion>> SuggestAsync(ComposeRecipientField field, string query)
    {
        List<RecipientSuggestion> suggestions;
        try { suggestions = await ViewModel.RecipientSuggestionService.SuggestAsync(ViewModel.ComposingAccount?.Id, query) ?? []; }
        catch (Exception exception)
        {
            Logger.CaptureException(exception, "ComposePage.ResolveRecipientSuggestions");
            return [];
        }
        var collection = CollectionFor(field);
        return suggestions.Where(suggestion => suggestion.IsList || !ComposePageViewModel.ContainsAddress(collection, suggestion.Address)).ToList();
    }

    /// <summary>Takes a suggestion: a list adds every member not already here; the typed text is cleared.</summary>
    private async Task CommitSuggestionAsync(ComposeRecipientField field, RecipientSuggestion suggestion)
    {
        var collection = CollectionFor(field);
        var addresses = new List<string>();
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            CloseSuggestions();
            field.ClearTypedText();
            if (suggestion.IsList)
            {
                foreach (var member in suggestion.ListMembers ?? Array.Empty<AccountContact>())
                {
                    var address = member.PrimaryEmailAddress;
                    if (string.IsNullOrWhiteSpace(address) || ComposePageViewModel.ContainsAddress(collection, address) || addresses.Contains(address, StringComparer.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrWhiteSpace(member.DisplayName)) _recipientNames[address] = member.DisplayName;
                    addresses.Add(address);
                }
            }
            else if (!string.IsNullOrWhiteSpace(suggestion.Address))
            {
                if (!string.IsNullOrWhiteSpace(suggestion.DisplayName)) _recipientNames[suggestion.Address] = suggestion.DisplayName;
                addresses.Add(suggestion.Address);
            }
        });
        if (addresses.Count == 0)
        {
            ViewModel.NotifyAddressExists();
            return;
        }
        await AddAddressesAsync(field, addresses);
    }

    /// <summary>Hides a remembered correspondent from suggestions without closing the list.</summary>
    private void SuppressSuggestion(RecipientSuggestion suggestion)
    {
        _suggestionList?.Remove(suggestion);
        Observe(SuppressSuggestionAsync(suggestion));
    }

    private async Task SuppressSuggestionAsync(RecipientSuggestion suggestion)
    {
        try { await ViewModel.SuppressSuggestionAsync(suggestion); }
        catch (Exception exception) { Logger.CaptureException(exception, "ComposePage.SuppressSuggestion"); }
    }
}
