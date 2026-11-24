#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;

namespace Wino.Mail.ViewModels;

/// <summary>
/// "@" mentions in the message body. The composer finds the query and writes the person; this class searches people,
/// keeps the list a head shows under the "@", and adds the person to the recipient field the user's setting names.
/// </summary>
public partial class ComposePageViewModel
{
    private IComposeMentions? _mentions;
    private ComposeMentionQuery? _pendingMentionQuery;
    private int _mentionVersion;

    /// <summary>The composer's mention seam. A head sets it when its editor is ready and clears it when the editor goes away.</summary>
    public IComposeMentions? Mentions
    {
        get => _mentions;
        set
        {
            if (ReferenceEquals(_mentions, value)) return;

            if (_mentions is { } previous)
            {
                previous.MentionQueryChanged -= OnMentionQueryChanged;
                previous.MentionKeyPressed -= OnMentionKeyPressed;
                PreferencesService.PreferenceChanged -= OnMentionPreferenceChanged;
                CloseMentionList();
            }

            _mentions = value;
            _pendingMentionQuery = null;

            if (value is not null)
            {
                value.MentionQueryChanged += OnMentionQueryChanged;
                value.MentionKeyPressed += OnMentionKeyPressed;
                PreferencesService.PreferenceChanged += OnMentionPreferenceChanged;
                _ = ApplyMentionSettingsAsync();
            }
        }
    }

    /// <summary>The people for the open list. Empty while the list is closed.</summary>
    public ObservableCollection<RecipientSuggestion> MentionSuggestions { get; } = [];

    [ObservableProperty]
    public partial int SelectedMentionIndex { get; set; }

    /// <summary>The query the list is open for, with the place of its "@"; null while the list is closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMentionListOpen))]
    public partial ComposeMentionQuery? ActiveMentionQuery { get; set; }

    public bool IsMentionListOpen => ActiveMentionQuery is not null;

    /// <summary>
    /// The composer's settings follow the preferences, also when they change while a draft is open.
    /// </summary>
    private async Task ApplyMentionSettingsAsync()
    {
        if (_mentions is not { } mentions) return;

        try
        {
            await mentions.SetMentionSettingsAsync(
                PreferencesService.IsComposerMentionSuggestionEnabled,
                PreferencesService.ComposerMentionFormat,
                PreferencesService.ComposerMentionRecipient);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// People (never contact lists) for an "@" query typed in the message body, including contacts that have no
    /// email address. Empty below the suggestion minimum.
    /// </summary>
    public async Task<List<RecipientSuggestion>> SuggestMentionsAsync(string query)
    {
        if (!ComposeRecipientRules.ShouldSuggest(query))
            return [];

        var suggestions = await RecipientSuggestionService
            .SuggestAsync(ComposingAccount?.Id, query.Trim(), includeLists: false, includeWithoutAddress: true)
            .ConfigureAwait(false);

        return suggestions?.Where(suggestion => suggestion is { IsList: false }).ToList() ?? [];
    }

    /// <summary>
    /// Adds a mentioned person to the given field unless the person has no address or the address is already in To,
    /// Cc or Bcc. Removing the mention later keeps the recipient. Returns false when nothing was added.
    /// </summary>
    public bool AddMentionedRecipient(AccountContact? recipient, ComposerMentionRecipient field)
    {
        if (recipient is null || field == ComposerMentionRecipient.None || string.IsNullOrWhiteSpace(recipient.Address)
            || ContainsAddress(ToItems, recipient.Address) || ContainsAddress(CCItems, recipient.Address)
            || ContainsAddress(BCCItems, recipient.Address))
            return false;

        if (field == ComposerMentionRecipient.To)
            return TryAddRecipient(ToItems, recipient);

        if (!TryAddRecipient(field == ComposerMentionRecipient.Cc ? CCItems : BCCItems, recipient))
            return false;

        IsCCBCCVisible = true;
        return true;
    }

    /// <summary>
    /// Writes the person into the message in place of the query and adds them to the recipient field of the user's
    /// setting. Returns false when the query is gone and nothing was written.
    /// </summary>
    public async Task<bool> AcceptMentionAsync(RecipientSuggestion? suggestion)
    {
        var mentions = _mentions;
        CloseMentionList();

        if (suggestion is null || mentions is null)
            return false;

        var address = string.IsNullOrWhiteSpace(suggestion.Address) ? null : suggestion.Address.Trim();
        var name = string.IsNullOrWhiteSpace(suggestion.DisplayName) ? address : suggestion.DisplayName.Trim();
        if (name is null)
            return false;

        try
        {
            if (await mentions.InsertMentionAsync(name, address) is not { } field)
                return false;

            AddMentionedRecipient(suggestion, field);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Closes the list and leaves the typed "@query" as it is.</summary>
    public void CloseMentionList()
    {
        _mentionVersion++;

        if (ActiveMentionQuery is null) return;

        ActiveMentionQuery = null;
        MentionSuggestions.Clear();
        _ = SetMentionListOpenAsync(false);
    }

    /// <summary>A person the user removed from the suggestions leaves the open list too.</summary>
    public void RemoveMentionSuggestion(RecipientSuggestion suggestion)
    {
        if (!MentionSuggestions.Remove(suggestion)) return;

        if (MentionSuggestions.Count == 0)
            CloseMentionList();
        else
            SelectedMentionIndex = 0;
    }

    private void OnMentionPreferenceChanged(object? sender, string preferenceName)
    {
        if (preferenceName is nameof(IPreferencesService.IsComposerMentionSuggestionEnabled)
            or nameof(IPreferencesService.ComposerMentionFormat)
            or nameof(IPreferencesService.ComposerMentionRecipient))
        {
            _ = ExecuteUIThreadAsync(ApplyMentionSettingsAsync);
        }
    }

    private void OnMentionQueryChanged(object? sender, ComposeMentionQuery? query)
    {
        _pendingMentionQuery = query;

        if (query is null)
        {
            CloseMentionList();
            return;
        }

        _ = ResolveMentionSuggestionsAsync(query);
    }

    private async Task ResolveMentionSuggestionsAsync(ComposeMentionQuery query)
    {
        var version = ++_mentionVersion;

        List<RecipientSuggestion> suggestions;
        try
        {
            suggestions = await SuggestMentionsAsync(query.Text).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Could not search people for a mention.");
            return;
        }

        await ExecuteUIThread(() =>
        {
            // A slower earlier query never replaces the result of a newer one.
            if (version != _mentionVersion || !ReferenceEquals(_pendingMentionQuery, query))
                return;

            if (suggestions.Count == 0)
            {
                CloseMentionList();
                return;
            }

            MentionSuggestions.Clear();
            foreach (var suggestion in suggestions)
                MentionSuggestions.Add(suggestion);

            SelectedMentionIndex = 0;

            var wasOpen = IsMentionListOpen;
            ActiveMentionQuery = query;

            // A head that cannot show the list closes it while it learns of the query.
            if (!wasOpen && IsMentionListOpen)
                _ = SetMentionListOpenAsync(true);
        }).ConfigureAwait(false);
    }

    private async void OnMentionKeyPressed(object? sender, ComposeMentionKey key)
    {
        if (!IsMentionListOpen) return;

        switch (key)
        {
            case ComposeMentionKey.Up:
                MoveMentionSelection(-1);
                break;
            case ComposeMentionKey.Down:
                MoveMentionSelection(1);
                break;
            case ComposeMentionKey.Accept:
                var index = SelectedMentionIndex;
                await AcceptMentionAsync(index >= 0 && index < MentionSuggestions.Count ? MentionSuggestions[index] : null);
                break;
            default:
                CloseMentionList();
                break;
        }
    }

    private void MoveMentionSelection(int step)
    {
        var count = MentionSuggestions.Count;
        if (count == 0) return;

        SelectedMentionIndex = SelectedMentionIndex < 0 ? 0 : (SelectedMentionIndex + step + count) % count;
    }

    private async Task SetMentionListOpenAsync(bool isOpen)
    {
        if (_mentions is not { } mentions) return;

        try
        {
            await mentions.SetMentionListOpenAsync(isOpen);
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
