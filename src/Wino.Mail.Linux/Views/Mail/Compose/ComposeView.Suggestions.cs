using System.Collections.ObjectModel;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Contacts;
using Wino.Mail.Controls.Linux.Compose;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Linux.Views.Mail.Compose;

internal sealed partial class ComposeView
{
    private const uint KeyUp = 0xff52, KeyDown = 0xff54, KeyReturn = 0xff0d, KeyPadEnter = 0xff8d, KeyTab = 0xff09, KeyEscape = 0xff1b;
    private WinoRecipientSuggestionPopup? _suggestions;
    private WinoRecipientTokenBox? _suggestionBox;
    private int _suggestionVersion;
    private CancellationTokenSource? _suggestionDelay;

    internal WinoRecipientSuggestionPopup? Suggestions => _suggestions;
    internal int SuggestionVersion => _suggestionVersion;

    partial void InitializeSuggestions()
    {
        var popup = _suggestions = new WinoRecipientSuggestionPopup { PictureFor = address => _pictures.Get(address) };
        popup.Chosen += item =>
        {
            if (_suggestionBox is { } box && item.Tag is RecipientSuggestion suggestion) Observe(CommitSuggestionAsync(box, suggestion));
        };
        popup.RemoveRequested += item =>
        {
            if (_suggestionBox is { } box && item.Tag is RecipientSuggestion suggestion) Observe(SuppressAsync(box, suggestion));
        };
        foreach (var box in _recipients.All) BindRecipientSuggestions(box, CollectionFor(box));
    }

    partial void DeactivateSuggestions()
    {
        CloseSuggestions();
        _suggestions?.Dispose();
        _suggestions = null;
    }

    private void BindRecipientSuggestions(WinoRecipientTokenBox box, ObservableCollection<AccountContact> collection)
    {
        box.TextChanged += sender => RecipientTextChanged(sender, collection);
        box.KeyFilter = (keyval, state) => HandleSuggestionKey(box, keyval, state);
        box.FocusLeft += _ => CloseSuggestions();
    }

    private void RecipientTextChanged(WinoRecipientTokenBox box, ObservableCollection<AccountContact> collection)
    {
        _suggestionDelay?.Cancel();
        _suggestionDelay?.Dispose();
        _suggestionDelay = null;
        var query = box.TypedText;
        if (!ComposeRecipientRules.ShouldSuggest(query))
        {
            CloseSuggestions();
            return;
        }
        var version = ++_suggestionVersion;
        var delay = _suggestionDelay = new CancellationTokenSource();
        Observe(RefreshSuggestionsAsync(box, collection, query, version, delay.Token));
    }

    // A slower earlier search must not replace a newer one: only the latest version for the text still typed is shown.
    private async Task RefreshSuggestionsAsync(WinoRecipientTokenBox box, ObservableCollection<AccountContact> collection, string query, int version, CancellationToken token)
    {
        try { await Task.Delay(ComposeRecipientRules.SuggestionDelay, token); }
        catch (OperationCanceledException) { return; }
        List<RecipientSuggestion> results;
        try { results = await ViewModel.RecipientSuggestionService.SuggestAsync(ViewModel.ComposingAccount?.Id, query); }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "Recipient suggestions failed.");
            results = [];
        }
        if (token.IsCancellationRequested) return;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (IsReleased || version != _suggestionVersion || box.TypedText != query || _suggestions is not { } popup) return;
            var items = ComposeRecipientRules.ExcludeExisting(results, collection)
                .Select(suggestion => new RecipientSuggestionItem(suggestion.Address, suggestion.DisplayName, suggestion.IsList ? suggestion.SecondaryText : suggestion.SourceCaption,
                    query, suggestion.IsList, suggestion.CanSuppress, suggestion))
                .ToArray();
            Serilog.Log.Debug("Recipient suggestions v{Version} '{Query}': {Count}", version, query, items.Length);
            _suggestionBox = box;
            popup.Show(box.Entry, items);
        });
    }

    private bool HandleSuggestionKey(WinoRecipientTokenBox box, uint keyval, Gdk.ModifierType state)
    {
        if (_suggestions is not { IsOpen: true } popup || !ReferenceEquals(_suggestionBox, box)) return false;
        switch (keyval)
        {
            case KeyDown: return popup.MoveSelection(1);
            case KeyUp: return popup.MoveSelection(-1);
            case KeyReturn or KeyPadEnter or KeyTab:
                if (ComposeRecipientRules.IsCompleteAddress(box.TypedText) && popup.SelectedIndex < 0) return false;
                popup.Choose(Math.Max(0, popup.SelectedIndex));
                return true;
            case KeyEscape:
                CloseSuggestions();
                return true;
            default: return false;
        }
    }

    private async Task CommitSuggestionAsync(WinoRecipientTokenBox box, RecipientSuggestion suggestion)
    {
        var collection = CollectionFor(box);
        CloseSuggestions();
        box.Entry.SetText(string.Empty);
        foreach (var (address, name) in ComposeRecipientRules.AddressesFor(suggestion, collection))
        {
            var recipient = await ViewModel.GetAddressInformationAsync(address, collection);
            if (recipient is null) continue;
            if (string.IsNullOrWhiteSpace(recipient.Name) && name is not null) recipient.Name = name;
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                if (!IsReleased) ViewModel.TryAddRecipient(collection, recipient);
            });
        }
        ScheduleAutosave();
    }

    private async Task SuppressAsync(WinoRecipientTokenBox box, RecipientSuggestion suggestion)
    {
        await ViewModel.SuppressSuggestionAsync(suggestion);
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (!IsReleased) RecipientTextChanged(box, CollectionFor(box));
        });
    }

    private void CloseSuggestions()
    {
        _suggestionDelay?.Cancel();
        _suggestionDelay?.Dispose();
        _suggestionDelay = null;
        _suggestions?.Close();
    }
}
