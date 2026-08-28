using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.Core.SearchBar;

namespace Wino.Mail.Controls.SearchBar;

public sealed partial class WinoSearchBar
{
    partial void OnModeChanged(SearchBarMode newValue)
    {
        UpdateModeState();
        UpdateSemanticVisualStates();
    }

    partial void OnTextChanged(string newValue)
    {
        if (_autoSuggestBox is not null && !string.Equals(_autoSuggestBox.Text, newValue, StringComparison.Ordinal))
        {
            _autoSuggestBox.Text = newValue;
        }
    }

    partial void OnPlaceholderTextChanged(string newValue) => UpdatePlaceholder();
    partial void OnItemsSourceChanged(object? newValue) => UpdateSuggestions(useHistory: false);
    partial void OnItemTemplateChanged(DataTemplate? newValue) => UpdateSuggestions(useHistory: false);
    partial void OnQueryIconChanged(IconElement? newValue) => UpdateQueryIcon();
    partial void OnIsCompactChanged(bool newValue) => UpdateCompactLayout();
    partial void OnSearchHistoryItemsSourceChanged(IEnumerable<string>? newValue) => UpdateHistorySource();
    partial void OnMaxHistorySuggestionCountChanged(int newValue) => RefreshHistoryItems();
    partial void OnIsSemanticSearchAvailableChanged(bool newValue) => UpdateSemanticState();
    partial void OnIsSemanticSearchEnabledChanged(bool newValue) => UpdateSemanticState();
    partial void OnIsSemanticSearchBusyChanged(bool newValue) => UpdateSemanticState();
    partial void OnSemanticPlaceholderTextChanged(string newValue) => UpdatePlaceholder();
    partial void OnSemanticSearchExplanationTextChanged(string newValue) => UpdateSemanticVisualStates();
    partial void OnSemanticUnavailableReasonTextChanged(string newValue) => UpdateSemanticVisualStates();
    partial void OnClearTextChanged(string newValue) => UpdateClearButtonText();
}
