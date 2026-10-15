using TooltipCompat = AndroidX.AppCompat.Widget.TooltipCompat;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Android.Content;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.Chip;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Mail.Compose;

/// <summary>
/// To / Cc / Bcc (Mac and iOS Recipients partials, Windows TokenItemAdding and ResolveRecipientSuggestionsAsync): each
/// field is a ChipGroup of input chips followed by an AutoCompleteTextView. The ViewModel collections are the source of
/// truth; chips are rewritten from them. Typed text becomes a recipient on the keyboard's Next action, a comma or
/// semicolon, or when the field loses focus with a complete address; suggestions come from the shared recipient
/// suggestion service after a short pause. The address rules are the shared <see cref="ComposeRecipientRules"/>.
/// </summary>
public sealed partial class ComposeFragment
{
    private readonly Dictionary<string, string> _recipientNames = new(StringComparer.OrdinalIgnoreCase);
    private RecipientRow? _toRow;
    private RecipientRow? _ccRow;
    private RecipientRow? _bccRow;
    private CancellationTokenSource? _suggestionDebounce;
    private int _suggestionVersion;

    /// <summary>One recipient field: its views, its ViewModel collection and its suggestion adapter.</summary>
    private sealed class RecipientRow(View root, ChipGroup chips, ComposeRecipientInput input, MaterialButton toggle,
        ObservableCollection<AccountContact> collection, ComposeSuggestionAdapter adapter)
    {
        public View Root { get; } = root;
        public ChipGroup Chips { get; } = chips;
        public ComposeRecipientInput Input { get; } = input;
        public MaterialButton Toggle { get; } = toggle;
        public ObservableCollection<AccountContact> Collection { get; } = collection;
        public ComposeSuggestionAdapter Adapter { get; } = adapter;
        public string TypedText => Input.Text ?? string.Empty;
    }

    private void BuildRecipients(View view, Context context, ComposePageViewModel viewModel, IDispatcher dispatcher)
    {
        _toRow = BuildRow(view.Require<View>(Resource.Id.compose_to), context, FieldCaption(Translator.ComposerTo), viewModel.ToItems, viewModel, dispatcher);
        _ccRow = BuildRow(view.Require<View>(Resource.Id.compose_cc), context, "Cc", viewModel.CCItems, viewModel, dispatcher);
        _bccRow = BuildRow(view.Require<View>(Resource.Id.compose_bcc), context, "Bcc", viewModel.BCCItems, viewModel, dispatcher);

        // Cc/Bcc reveal on the To row (canvas: the chevron at the end of To).
        var toggle = _toRow.Toggle;
        toggle.Visibility = ViewStates.Visible;
        toggle.Icon = WinoIcons.Drawable(context, WinoIconGlyph.ChevronDown, ComposeStyle.Dp(context, Resource.Dimension.wino_compose_glyph_ccbcc), WinoColors.SecondaryText(context));
        toggle.IconTint = null;
        toggle.ContentDescription = Translator.Composer_CcBcc;
        TooltipCompat.SetTooltipText(toggle, Translator.Composer_CcBcc);
        OnClick(toggle, () =>
        {
            viewModel.IsCCBCCVisible = !viewModel.IsCCBCCVisible;
            if (viewModel.IsCCBCCVisible) FocusRecipientInput(_ccRow);
        });
    }

    private RecipientRow BuildRow(View root, Context context, string caption, ObservableCollection<AccountContact> collection,
        ComposePageViewModel viewModel, IDispatcher dispatcher)
    {
        root.Require<TextView>(Resource.Id.recipient_caption).Text = caption;
        var chips = root.Require<ChipGroup>(Resource.Id.recipient_chips);
        var input = root.Require<ComposeRecipientInput>(Resource.Id.recipient_input);
        var toggle = root.Require<MaterialButton>(Resource.Id.recipient_toggle);
        input.ContentDescription = caption;
        input.Hint = string.Empty;
        input.DropDownAnchor = root.Id;
        var adapter = new ComposeSuggestionAdapter(context);
        input.Adapter = adapter;
        var row = new RecipientRow(root, chips, input, toggle, collection, adapter);

        EventHandler<global::Android.Text.TextChangedEventArgs> changed = (_, _) => RecipientTextChanged(row);
        // Typing invalidates the shown suggestions before the widget reacts to the new text.
        EventHandler<global::Android.Text.TextChangedEventArgs> changing = (_, _) => input.ShowsSuggestions = false;
        input.BeforeTextChanged += changing;
        EventHandler<TextView.EditorActionEventArgs> action = (_, args) =>
        {
            args.Handled = args.ActionId is ImeAction.Next or ImeAction.Done or ImeAction.Go || args.Event?.KeyCode == Keycode.Enter;
            if (args.Handled) Observe(ReturnInFieldAsync(row));
        };
        EventHandler<View.FocusChangeEventArgs> focus = (_, args) =>
        {
            if (args.HasFocus) return;
            // Leaving the field keeps a complete address as a recipient; anything else stays as typed.
            if (ComposeRecipientRules.IsCompleteAddress(row.TypedText)) Observe(CommitTypedTextAsync(row, row.TypedText, false));
        };
        EventHandler<AdapterView.ItemClickEventArgs> picked = (_, args) =>
        {
            if (adapter.ItemAt(args.Position) is { } suggestion) Observe(CommitSuggestionAsync(row, suggestion));
        };
        EventHandler<View.KeyEventArgs> key = (_, args) =>
        {
            args.Handled = false;
            // Delete in an empty field removes the last recipient (hardware keyboards and IMEs that send the key).
            if (args.KeyCode == Keycode.Del && args.Event?.Action == KeyEventActions.Down && row.TypedText.Length == 0 && row.Collection.Count > 0)
            {
                row.Collection.RemoveAt(row.Collection.Count - 1);
                ScheduleAutosave();
                args.Handled = true;
            }
        };
        input.TextChanged += changed;
        input.EditorAction += action;
        input.FocusChange += focus;
        input.ItemClick += picked;
        input.KeyPress += key;

        NotifyCollectionChangedEventHandler recipients = (_, _) => _ = dispatcher.ExecuteOnUIThread(() => { if (!Bindings.IsDisposed) WriteChips(row); });
        collection.CollectionChanged += recipients;
        Bindings.Own(new ActionDisposable(() =>
        {
            input.TextChanged -= changed;
            input.BeforeTextChanged -= changing;
            input.EditorAction -= action;
            input.FocusChange -= focus;
            input.ItemClick -= picked;
            input.KeyPress -= key;
            collection.CollectionChanged -= recipients;
        }));
        WriteChips(row);
        return row;
    }

    /// <summary>Rewrites the row's chips from its collection; the text field stays last.</summary>
    private void WriteChips(RecipientRow row)
    {
        var context = row.Chips.Context;
        if (context is null) return;
        for (int index = row.Chips.ChildCount - 1; index >= 0; index--)
            if (row.Chips.GetChildAt(index) is Chip chip) row.Chips.RemoveViewAt(index);
        foreach (var contact in row.Collection.ToList())
        {
            if (!string.IsNullOrWhiteSpace(contact.Address) && string.IsNullOrWhiteSpace(contact.Name) && _recipientNames.TryGetValue(contact.Address, out var name))
                contact.Name = name;
            var label = string.IsNullOrWhiteSpace(contact.Name) ? contact.Address ?? string.Empty : contact.Name;
            var chip = WinoChips.Input(context, label, contact.Address);
            chip.ContentDescription = string.IsNullOrWhiteSpace(contact.Name) || contact.Name == contact.Address ? contact.Address : $"{contact.Name}, {contact.Address}";
            chip.CloseIconContentDescription = $"{Translator.Buttons_Remove} {label}";
            var current = contact;
            chip.SetOnCloseIconClickListener(new ClickListener(() =>
            {
                row.Collection.Remove(current);
                ScheduleAutosave();
            }));
            row.Chips.AddView(chip, row.Chips.ChildCount - 1);
        }
    }

    private void ApplyCcBccVisibility(bool visible)
    {
        if (_ccRow is not null) _ccRow.Root.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
        if (_bccRow is not null) _bccRow.Root.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
        if (_toRow?.Toggle is { } toggle && toggle.Context is { } context)
            toggle.Icon = WinoIcons.Drawable(context, visible ? WinoIconGlyph.ChevronUp : WinoIconGlyph.ChevronDown, ComposeStyle.Dp(context, Resource.Dimension.wino_compose_glyph_ccbcc), WinoColors.SecondaryText(context));
    }

    private void FocusRecipientInput(RecipientRow? row)
    {
        if (row is null) return;
        row.Input.RequestFocus();
        if (row.Input.Context?.GetSystemService(Context.InputMethodService) is InputMethodManager input)
            input.ShowSoftInput(row.Input, ShowFlags.Implicit);
    }

    /// <summary>Commits what is still typed in each field (a complete address only), before saving or sending.</summary>
    private async Task SyncAllRecipientsAsync()
    {
        var pending = new List<(RecipientRow Row, string Text)>();
        await (_dispatcher?.ExecuteOnUIThread(() =>
        {
            foreach (var row in new[] { _toRow, _ccRow, _bccRow })
                if (row is not null && ComposeRecipientRules.IsCompleteAddress(row.TypedText)) pending.Add((row, row.TypedText));
        }) ?? Task.CompletedTask);
        foreach (var (row, text) in pending) await CommitTypedTextAsync(row, text, false);
    }

    /// <summary>Adds the typed address (with an optional name) and clears the field; an invalid address is reported when asked.</summary>
    private async Task CommitTypedTextAsync(RecipientRow row, string text, bool notifyInvalid)
    {
        var typed = text.Trim().TrimEnd(',', ';').Trim();
        if (typed.Length == 0 || ViewModel is not { } viewModel) return;
        if (!ComposeRecipientRules.TryParseTyped(typed, out var address, out var name))
        {
            if (notifyInvalid) viewModel.NotifyInvalidEmail(typed);
            return;
        }
        if (name is not null) _recipientNames[address] = name;
        await (_dispatcher?.ExecuteOnUIThread(() =>
        {
            if (row.TypedText.Contains(typed, StringComparison.Ordinal)) row.Input.Text = string.Empty;
            CloseSuggestions();
        }) ?? Task.CompletedTask);
        await AddAddressesAsync(row, [address]);
    }

    /// <summary>Resolves each address (a known contact supplies the name) and adds it unless it is already there.</summary>
    private async Task AddAddressesAsync(RecipientRow row, IReadOnlyList<string> addresses)
    {
        if (ViewModel is not { } viewModel) return;
        var added = false;
        foreach (var address in addresses.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ComposePageViewModel.ContainsAddress(row.Collection, address))
            {
                viewModel.NotifyAddressExists();
                continue;
            }
            var recipient = await viewModel.GetAddressInformationAsync(address, row.Collection);
            if (recipient is null) continue;
            // An unknown address comes back named after itself; a typed or suggested name is better.
            if ((string.IsNullOrWhiteSpace(recipient.Name) || string.Equals(recipient.Name, address, StringComparison.OrdinalIgnoreCase))
                && _recipientNames.TryGetValue(address, out var name)) recipient.Name = name;
            await (_dispatcher?.ExecuteOnUIThread(() => added |= viewModel.TryAddRecipient(row.Collection, recipient)) ?? Task.CompletedTask);
        }
        if (added) await (_dispatcher?.ExecuteOnUIThread(ScheduleAutosave) ?? Task.CompletedTask);
    }

    /// <summary>
    /// Next on the keyboard: the top suggestion unless a complete address is typed (Windows GetTopSuggestionAsync),
    /// otherwise the typed text; an empty field moves on (To → Cc → Bcc → Subject).
    /// </summary>
    private async Task ReturnInFieldAsync(RecipientRow row)
    {
        var typed = row.TypedText.Trim();
        if (typed.Length == 0)
        {
            CloseSuggestions();
            MoveFocusAfter(row);
            return;
        }
        if (!ComposeRecipientRules.IsCompleteAddress(typed) && ComposeRecipientRules.ShouldSuggest(typed))
        {
            var top = row.Input.IsPopupShowing && row.Adapter.Count > 0 ? row.Adapter.ItemAt(0) : (await SuggestAsync(row, typed)).FirstOrDefault();
            if (top is not null && row.TypedText.Trim() == typed)
            {
                await CommitSuggestionAsync(row, top);
                return;
            }
        }
        await CommitTypedTextAsync(row, typed, true);
    }

    private void MoveFocusAfter(RecipientRow row)
    {
        if (ReferenceEquals(row, _toRow) && ViewModel?.IsCCBCCVisible == true) FocusRecipientInput(_ccRow);
        else if (ReferenceEquals(row, _ccRow)) FocusRecipientInput(_bccRow);
        else _subject?.RequestFocus();
    }

    // ---- Suggestions ----

    /// <summary>Typed text changed: a comma or semicolon commits it; otherwise suggest after a short pause.</summary>
    private void RecipientTextChanged(RecipientRow row)
    {
        if (Bindings.IsDisposed) return;
        var text = row.TypedText;
        if (text.Length > 1 && (text.EndsWith(',') || text.EndsWith(';')))
        {
            Observe(CommitTypedTextAsync(row, text, true));
            return;
        }
        var query = text.Trim();
        _suggestionDebounce?.Cancel();
        var version = ++_suggestionVersion;
        if (!ComposeRecipientRules.ShouldSuggest(query))
        {
            row.Input.DismissDropDown();
            return;
        }
        var source = _suggestionDebounce = new CancellationTokenSource();
        Observe(RefreshSuggestionsAsync(row, query, version, source.Token));
    }

    private async Task RefreshSuggestionsAsync(RecipientRow row, string query, int version, CancellationToken token)
    {
        try { await Task.Delay(ComposeRecipientRules.SuggestionDelay, token); }
        catch (OperationCanceledException) { return; }
        var results = await SuggestAsync(row, query);
        await (_dispatcher?.ExecuteOnUIThread(() =>
        {
            // A slower earlier query never replaces the result of a newer one.
            if (version != _suggestionVersion || Bindings.IsDisposed) return;
            if (!row.Input.HasFocus || row.TypedText.Trim() != query || results.Count == 0)
            {
                row.Input.DismissDropDown();
                return;
            }
            row.Input.ShowsSuggestions = true;
            row.Adapter.SetItems(results);
            row.Input.DropDownWidth = row.Root.Width;
            row.Input.ShowDropDown();
        }) ?? Task.CompletedTask);
    }

    /// <summary>Contacts, remembered correspondents and lists; someone already in this field is not suggested again.</summary>
    private async Task<List<RecipientSuggestion>> SuggestAsync(RecipientRow row, string query)
    {
        if (ViewModel is not { } viewModel) return [];
        try
        {
            var suggestions = await viewModel.RecipientSuggestionService.SuggestAsync(viewModel.ComposingAccount?.Id, query);
            return ComposeRecipientRules.ExcludeExisting(suggestions, row.Collection);
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "Recipient suggestions failed.");
            return [];
        }
    }

    /// <summary>Takes a suggestion: a list adds every member not already here; the typed text is cleared.</summary>
    private async Task CommitSuggestionAsync(RecipientRow row, RecipientSuggestion suggestion)
    {
        var addresses = new List<string>();
        await (_dispatcher?.ExecuteOnUIThread(() =>
        {
            CloseSuggestions();
            row.Input.Text = string.Empty;
            foreach (var (address, name) in ComposeRecipientRules.AddressesFor(suggestion, row.Collection))
            {
                if (name is not null) _recipientNames[address] = name;
                addresses.Add(address);
            }
        }) ?? Task.CompletedTask);
        if (addresses.Count == 0)
        {
            ViewModel?.NotifyAddressExists();
            return;
        }
        await AddAddressesAsync(row, addresses);
    }

    private void CloseSuggestions()
    {
        _suggestionDebounce?.Cancel();
        _suggestionVersion++;
        foreach (var row in new[] { _toRow, _ccRow, _bccRow })
        {
            if (row is null) continue;
            row.Input.ShowsSuggestions = false;
            row.Input.DismissDropDown();
        }
    }

    /// <summary>Back closes an open suggestion list first.</summary>
    private bool CloseSuggestionsIfOpen()
    {
        if (new[] { _toRow, _ccRow, _bccRow }.Any(row => row?.Input.IsPopupShowing == true))
        {
            CloseSuggestions();
            return true;
        }
        return false;
    }

    private sealed class ClickListener(Action clicked) : Java.Lang.Object, View.IOnClickListener
    {
        public void OnClick(View? view) => clicked();
    }
}
