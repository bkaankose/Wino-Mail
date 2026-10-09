using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AppKit;
using Foundation;
using MimeKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Contacts;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.MacOS.Views.Mail.Compose;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// To / Cc / Bcc token fields kept in step with the ViewModel collections, and the recipient suggestion
/// popup (Windows ResolveRecipientSuggestionsAsync, TokenItemAdding, AddListMembers and
/// SuppressSuggestionClicked): contacts, remembered correspondents and contact lists that expand into their
/// members, with "Don't suggest" on remembered correspondents. A token's context menu copies its address.
/// </summary>
public sealed partial class ComposePageViewController
{
    private const char TokenAttachment = '￼';
    private const ushort ReturnKeyCode = 36;
    private const ushort EnterKeyCode = 76;
    private const ushort TabKeyCode = 48;
    private const ushort DownKeyCode = 125;
    private const ushort UpKeyCode = 126;
    private readonly Dictionary<string, string> _recipientNames = new(StringComparer.OrdinalIgnoreCase);
    private RecipientDelegate? _recipientDelegate;
    private ComposeRecipientSuggestionPopup? _suggestionPopup;
    private CancellationTokenSource? _suggestionDebounce;
    private NSObject? _resignKeyObserver;
    private readonly List<NSObject> _fieldFrameObservers = [];
    private int _suggestionVersion;
    private bool _suggestionPending;
    private bool _suggestionArrowed;
    private bool _committingSuggestion;
    private bool _syncingRecipients;

    private NSTokenField TokenField(string label)
    {
        var field = new NSTokenField
        {
            Bordered = false,
            DrawsBackground = false,
            Font = WinoStyle.Body,
            FocusRingType = NSFocusRingType.None,
            TokenStyle = NSTokenStyle.Rounded,
            TranslatesAutoresizingMaskIntoConstraints = false,
            PostsFrameChangedNotifications = true
        };
        field.Delegate = _recipientDelegate;
        field.CharacterSet = NSCharacterSet.FromString(",;");
        WinoAccessibility.Label(field, label);
        return field;
    }

    private ObservableCollection<AccountContact> CollectionFor(NSTokenField field)
        => ReferenceEquals(field, _ccField) ? ViewModel.CCItems : ReferenceEquals(field, _bccField) ? ViewModel.BCCItems : ViewModel.ToItems;

    private void ObserveRecipients(ObservableCollection<AccountContact> collection, NSTokenField field)
    {
        NotifyCollectionChangedEventHandler handler = (_, _) => _ = Dispatcher.ExecuteOnUIThread(() => { if (!_syncingRecipients) WriteTokens(field, collection); });
        collection.CollectionChanged += handler;
        Bindings.Own(new ActionDisposable(() => collection.CollectionChanged -= handler));
        WriteTokens(field, collection);
    }

    private void WriteTokens(NSTokenField field, IEnumerable<AccountContact> contacts)
    {
        var addresses = new List<NSObject>();
        foreach (var contact in contacts)
        {
            if (string.IsNullOrWhiteSpace(contact.Address)) continue;
            if (!string.IsNullOrWhiteSpace(contact.Name)) _recipientNames[contact.Address] = contact.Name;
            addresses.Add(new NSString(contact.Address));
        }
        field.ObjectValue = NSArray.FromNSObjects(addresses.ToArray());
    }

    private static IReadOnlyList<string> ReadTokens(NSTokenField field)
    {
        if (field.ObjectValue is not NSArray array) return [];
        var result = new List<string>();
        for (nuint index = 0; index < array.Count; index++)
        {
            var value = array.GetItem<NSObject>(index)?.ToString();
            if (!string.IsNullOrWhiteSpace(value)) result.Add(value.Trim());
        }
        return result;
    }

    private async Task SyncAllRecipientsAsync()
    {
        string[] to = [], cc = [], bcc = [];
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            to = ReadTokens(_toField).ToArray();
            cc = ReadTokens(_ccField).ToArray();
            bcc = ReadTokens(_bccField).ToArray();
        });
        await SyncRecipientsAsync(_toField, to);
        await SyncRecipientsAsync(_ccField, cc);
        await SyncRecipientsAsync(_bccField, bcc);
    }

    /// <summary>Makes the ViewModel collection match the tokens: removals first, then resolved additions.</summary>
    private async Task SyncRecipientsAsync(NSTokenField field, IReadOnlyList<string> tokens)
    {
        var collection = CollectionFor(field);
        var addresses = new List<string>();
        var invalid = new List<string>();
        foreach (var token in tokens)
        {
            if (MailboxAddress.TryParse(token, out var mailbox) && mailbox.Address.Contains('@'))
            {
                addresses.Add(mailbox.Address);
                if (!string.IsNullOrWhiteSpace(mailbox.Name)) _recipientNames[mailbox.Address] = mailbox.Name;
            }
            else invalid.Add(token);
        }

        _syncingRecipients = true;
        try
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                foreach (var contact in collection.ToArray())
                    if (!addresses.Contains(contact.Address?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)) collection.Remove(contact);
            });
            foreach (var address in addresses.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (ComposePageViewModel.ContainsAddress(collection, address)) continue;
                var recipient = await ViewModel.GetAddressInformationAsync(address, collection);
                if (recipient is null) continue;
                if (string.IsNullOrWhiteSpace(recipient.Name) && _recipientNames.TryGetValue(address, out var name)) recipient.Name = name;
                await Dispatcher.ExecuteOnUIThread(() => ViewModel.TryAddRecipient(collection, recipient));
            }
        }
        finally { _syncingRecipients = false; }

        if (invalid.Count > 0)
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                foreach (var token in invalid) ViewModel.NotifyInvalidEmail(token);
                WriteTokens(field, collection);
            });
        }
    }

    private string DisplayString(string address)
        => _recipientNames.TryGetValue(address, out var name) && !string.IsNullOrWhiteSpace(name) ? name : address;

    private void ScheduleRecipientSync(NSTokenField field)
    {
        // Runs after AppKit commits the new token into the field's object value.
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (Bindings.IsDisposed) return;
            var tokens = ReadTokens(field);
            Observe(SyncRecipientsAsync(field, tokens));
            ScheduleAutosave();
        });
    }

    // ---- Suggestion popup ----

    private void BindRecipientPopup()
    {
        _suggestionPopup = new ComposeRecipientSuggestionPopup(_pictures);
        _suggestionPopup.Picked += (_, suggestion) =>
        {
            if (_suggestionPopup?.Anchor is NSTokenField field) Observe(CommitSuggestionAsync(field, suggestion));
        };
        _suggestionPopup.SuppressRequested += (_, suggestion) => SuppressSuggestion(suggestion);
        _resignKeyObserver = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.DidResignKeyNotification, notification =>
        {
            if (notification.Object is NSWindow window && window == View.Window) CloseSuggestions();
        });
        foreach (var field in new[] { _toField, _ccField, _bccField })
        {
            _fieldFrameObservers.Add(NSNotificationCenter.DefaultCenter.AddObserver(NSView.FrameChangedNotification, notification =>
            {
                if (_suggestionPopup is { IsVisible: true } popup && ReferenceEquals(popup.Anchor, notification.Object)) popup.Reposition();
            }, field));
        }
    }

    private void CloseSuggestions()
    {
        _suggestionDebounce?.Cancel();
        _suggestionVersion++;
        _suggestionPending = false;
        _suggestionArrowed = false;
        _suggestionPopup?.Close();
    }

    private void DisposeRecipients()
    {
        CloseSuggestions();
        if (_resignKeyObserver is not null)
        {
            NSNotificationCenter.DefaultCenter.RemoveObserver(_resignKeyObserver);
            _resignKeyObserver = null;
        }
        foreach (var observer in _fieldFrameObservers) NSNotificationCenter.DefaultCenter.RemoveObserver(observer);
        _fieldFrameObservers.Clear();
        _suggestionPopup?.Dispose();
        _suggestionPopup = null;
        foreach (var field in new[] { _toField, _ccField, _bccField })
            if (field is not null) field.Delegate = null!;
        _recipientDelegate?.Dispose();
        _recipientDelegate = null;
    }

    private NSTokenField? FieldEditedBy(NSResponder? responder)
    {
        if (responder is not NSTextView editor) return null;
        foreach (var field in new[] { _toField, _ccField, _bccField })
            if (field?.CurrentEditor is { } current && ReferenceEquals(current, editor)) return field;
        return null;
    }

    /// <summary>The text typed since the last token or delimiter, with its range in the field editor.</summary>
    private static (string Query, NSRange Range) TypedText(NSTextView editor)
    {
        var text = editor.Value ?? string.Empty;
        var caret = (int)Math.Min((long)editor.SelectedRange.Location, text.Length);
        int start = 0;
        for (int index = caret - 1; index >= 0; index--)
        {
            if (text[index] is TokenAttachment or ',' or ';') { start = index + 1; break; }
        }
        int end = caret;
        while (end < text.Length && text[end] is not (TokenAttachment or ',' or ';')) end++;
        return (text[start..end].Trim(), new NSRange(start, end - start));
    }

    /// <summary>Typed text changed: suggest after 120 ms (at least two characters), never while an input method composes.</summary>
    private void RecipientTextChanged(NSTokenField field)
    {
        if (_committingSuggestion || Bindings.IsDisposed) return;
        if (field.CurrentEditor is not NSTextView editor || HasMarkedText(editor))
        {
            if (field.CurrentEditor is null) CloseSuggestions();
            return;
        }
        var (query, _) = TypedText(editor);
        _suggestionDebounce?.Cancel();
        var version = ++_suggestionVersion;
        _suggestionArrowed = false;
        if (query.Length < 2)
        {
            _suggestionPending = false;
            _suggestionPopup?.Close();
            return;
        }
        _suggestionPending = true;
        var source = _suggestionDebounce = new CancellationTokenSource();
        Observe(RefreshSuggestionsAsync(field, query, version, source.Token));
    }

    private async Task RefreshSuggestionsAsync(NSTokenField field, string query, int version, CancellationToken token)
    {
        try { await Task.Delay(120, token); }
        catch (OperationCanceledException) { return; }
        var results = await SuggestAsync(field, query);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            // A slower earlier query never replaces the result of a newer one.
            if (version != _suggestionVersion || Bindings.IsDisposed || _suggestionPopup is null) return;
            _suggestionPending = false;
            if (field.CurrentEditor is not NSTextView editor || HasMarkedText(editor) || TypedText(editor).Query != query)
            {
                _suggestionPopup.Close();
                return;
            }
            _suggestionPopup.Show(field, results, query);
        });
    }

    /// <summary>Contacts, remembered correspondents and lists; someone already in this field is not suggested again.</summary>
    private async Task<List<RecipientSuggestion>> SuggestAsync(NSTokenField field, string query)
    {
        List<RecipientSuggestion> suggestions;
        try { suggestions = await ViewModel.RecipientSuggestionService.SuggestAsync(ViewModel.ComposingAccount?.Id, query); }
        catch (Exception exception)
        {
            Logger.CaptureException(exception, "ComposePage.ResolveRecipientSuggestions");
            return [];
        }
        var collection = CollectionFor(field);
        return suggestions.Where(suggestion => suggestion.IsList || !ComposePageViewModel.ContainsAddress(collection, suggestion.Address)).ToList();
    }

    /// <summary>Arrow keys, Return, Tab and Esc for the popup; false lets the key reach the field.</summary>
    private bool HandleSuggestionKey(NSEvent theEvent, NSEventModifierMask flags, NSResponder? responder)
    {
        if (flags != 0 || _suggestionPopup is null || FieldEditedBy(responder) is not { } field || responder is not NSTextView editor) return false;
        var popup = _suggestionPopup;
        var key = theEvent.KeyCode;

        if (!popup.IsVisible)
        {
            // Return before the debounced search answered: search now, like Windows GetTopSuggestionAsync.
            if (key is not (ReturnKeyCode or EnterKeyCode) || !_suggestionPending) return false;
            var (query, _) = TypedText(editor);
            if (query.Length < 2 || IsCompleteAddress(query)) return false;
            CloseSuggestions();
            Observe(CommitTopSuggestionAsync(field, query));
            return true;
        }

        switch (key)
        {
            case DownKeyCode:
                _suggestionArrowed = true;
                popup.MoveSelection(1);
                return true;
            case UpKeyCode:
                _suggestionArrowed = true;
                popup.MoveSelection(-1);
                return true;
            case EscapeKeyCode:
                CloseSuggestions();
                return true;
            case ReturnKeyCode or EnterKeyCode:
            {
                // A complete address is taken as typed, even with suggestions showing.
                var (query, _) = TypedText(editor);
                if (IsCompleteAddress(query) && !_suggestionArrowed)
                {
                    CloseSuggestions();
                    return false;
                }
                if (popup.Highlighted is not { } highlighted) { CloseSuggestions(); return false; }
                Observe(CommitSuggestionAsync(field, highlighted));
                return true;
            }
            case TabKeyCode:
                if (_suggestionArrowed && popup.Highlighted is { } chosen)
                {
                    Observe(CommitSuggestionAsync(field, chosen));
                    return true;
                }
                CloseSuggestions();
                return false;
        }
        return false;
    }

    private static bool IsCompleteAddress(string text)
        => MailboxAddress.TryParse(text, out var mailbox) && mailbox.Address.Contains('@') && mailbox.Address.Contains('.');

    private async Task CommitTopSuggestionAsync(NSTokenField field, string query)
    {
        var suggestions = await SuggestAsync(field, query);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (Bindings.IsDisposed || field.CurrentEditor is not NSTextView editor || TypedText(editor).Query != query) return;
            if (suggestions.FirstOrDefault() is { } top) Observe(CommitSuggestionAsync(field, top));
            // Nothing matched: commit the typed text as the field would have.
            else editor.InsertText(new NSString(","), editor.SelectedRange);
        });
    }

    /// <summary>
    /// Replaces the typed text with the suggestion's address (a list adds every member not already here)
    /// and lets the field tokenize it, which syncs the ViewModel and autosaves.
    /// </summary>
    private Task CommitSuggestionAsync(NSTokenField field, RecipientSuggestion suggestion)
    {
        CloseSuggestions();
        if (field.CurrentEditor is not NSTextView editor) return Task.CompletedTask;
        var collection = CollectionFor(field);
        var existing = ReadTokens(field);
        bool Present(string address) => ComposePageViewModel.ContainsAddress(collection, address) || existing.Contains(address, StringComparer.OrdinalIgnoreCase);

        var addresses = new List<string>();
        if (suggestion.IsList)
        {
            foreach (var member in suggestion.ListMembers)
            {
                var address = member.PrimaryEmailAddress;
                if (string.IsNullOrWhiteSpace(address) || Present(address) || addresses.Contains(address, StringComparer.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(member.DisplayName)) _recipientNames[address] = member.DisplayName;
                addresses.Add(address);
            }
        }
        else if (!string.IsNullOrWhiteSpace(suggestion.Address) && !Present(suggestion.Address))
        {
            if (!string.IsNullOrWhiteSpace(suggestion.DisplayName)) _recipientNames[suggestion.Address] = suggestion.DisplayName;
            addresses.Add(suggestion.Address);
        }

        var (_, range) = TypedText(editor);
        _committingSuggestion = true;
        try
        {
            if (addresses.Count == 0)
            {
                ViewModel.NotifyAddressExists();
                editor.InsertText(new NSString(string.Empty), range);
            }
            else
            {
                // The field's tokenizing character commits each address as a token.
                editor.InsertText(new NSString(string.Join(",", addresses) + ","), range);
            }
        }
        finally { _committingSuggestion = false; }
        ScheduleRecipientSync(field);
        return Task.CompletedTask;
    }

    /// <summary>Hides a remembered correspondent from suggestions without closing the list.</summary>
    private void SuppressSuggestion(RecipientSuggestion suggestion)
    {
        _suggestionPopup?.Remove(suggestion);
        Observe(SuppressSuggestionAsync(suggestion));
    }

    private async Task SuppressSuggestionAsync(RecipientSuggestion suggestion)
    {
        try { await ViewModel.SuppressSuggestionAsync(suggestion); }
        catch (Exception exception) { Logger.CaptureException(exception, "ComposePage.SuppressSuggestion"); }
    }

    private static void CopyAddress(string address)
    {
        var pasteboard = NSPasteboard.GeneralPasteboard;
        pasteboard.ClearContents();
        pasteboard.SetStringForType(address, NSPasteboardType.String.GetConstant()!);
    }

    private sealed class RecipientDelegate(ComposePageViewController owner) : NSTokenFieldDelegate
    {
        // The suggestion popup replaces AppKit's string completions.
        public override string[] GetCompletionStrings(NSTokenField tokenField, string substring, nint tokenIndex, nint selectedIndex) => [];

        public override NSObject GetRepresentedObject(NSTokenField tokenField, string editingString)
        {
            var text = editingString?.Trim() ?? string.Empty;
            if (MailboxAddress.TryParse(text, out var mailbox) && mailbox.Address.Contains('@'))
            {
                if (!string.IsNullOrWhiteSpace(mailbox.Name)) owner._recipientNames[mailbox.Address] = mailbox.Name;
                return new NSString(mailbox.Address);
            }
            return new NSString(text);
        }

        public override string GetDisplayString(NSTokenField tokenField, NSObject representedObject)
            => owner.DisplayString(representedObject?.ToString() ?? string.Empty);

        public override string GetEditingString(NSTokenField tokenField, NSObject representedObject)
            => representedObject?.ToString() ?? string.Empty;

        public override NSArray ShouldAddObjects(NSTokenField tokenField, NSArray tokens, nuint index)
        {
            owner.ScheduleRecipientSync(tokenField);
            return tokens;
        }

        public override bool HasMenu(NSTokenField tokenField, NSObject representedObject) => !string.IsNullOrWhiteSpace(representedObject?.ToString());

        /// <summary>Windows ContactTokenContextRequested: Copy the address.</summary>
        public override NSMenu GetMenu(NSTokenField tokenField, NSObject representedObject)
        {
            var address = representedObject?.ToString() ?? string.Empty;
            var menu = new NSMenu { AutoEnablesItems = false };
            menu.AddItem(new NSMenuItem(Translator.Buttons_Copy, (_, _) => CopyAddress(address)));
            return menu;
        }

        [Export("controlTextDidChange:")]
        public void TextChanged(NSNotification notification)
        {
            if (notification.Object is NSTokenField field) owner.RecipientTextChanged(field);
        }

        [Export("controlTextDidEndEditing:")]
        public void EditingEnded(NSNotification notification)
        {
            owner.CloseSuggestions();
            if (notification.Object is NSTokenField field) owner.ScheduleRecipientSync(field);
        }
    }
}
