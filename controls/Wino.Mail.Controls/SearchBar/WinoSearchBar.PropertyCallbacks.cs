using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.Core.SearchBar;

namespace Wino.Mail.Controls.SearchBar;

public sealed partial class WinoSearchBar
{
    partial void OnModeChanged(SearchBarMode newValue) => OnVisualPropertyChanged(this, ModeProperty);
    partial void OnTextChanged(string newValue) => OnInputPropertyChanged(this, TextProperty);
    partial void OnPlaceholderTextChanged(string newValue) => OnInputPropertyChanged(this, PlaceholderTextProperty);
    partial void OnItemsSourceChanged(object? newValue) => OnInputPropertyChanged(this, ItemsSourceProperty);
    partial void OnItemTemplateChanged(DataTemplate? newValue) => OnInputPropertyChanged(this, ItemTemplateProperty);
    partial void OnQueryIconChanged(IconElement? newValue) => OnVisualPropertyChanged(this, QueryIconProperty);
    partial void OnSearchHistoryItemsSourceChanged(IEnumerable<string>? newValue) => OnHistorySourcePropertyChanged(this, SearchHistoryItemsSourceProperty);
    partial void OnMaxHistorySuggestionCountChanged(int newValue) => OnHistorySourcePropertyChanged(this, MaxHistorySuggestionCountProperty);
    partial void OnIsSemanticSearchAvailableChanged(bool newValue) => OnVisualPropertyChanged(this, IsSemanticSearchAvailableProperty);
    partial void OnIsSemanticSearchEnabledChanged(bool newValue) => OnVisualPropertyChanged(this, IsSemanticSearchEnabledProperty);
    partial void OnIsSemanticSearchBusyChanged(bool newValue) => OnVisualPropertyChanged(this, IsSemanticSearchBusyProperty);
    partial void OnSearchScopeChanged(SearchBarScope newValue) => OnVisualPropertyChanged(this, SearchScopeProperty);
    partial void OnSearchReachChanged(SearchBarReach newValue) => OnVisualPropertyChanged(this, SearchReachProperty);
    partial void OnSenderFilterChanged(string newValue) => OnVisualPropertyChanged(this, SenderFilterProperty);
    partial void OnDateRangeChanged(SearchBarDateRange newValue) => OnVisualPropertyChanged(this, DateRangeProperty);
    partial void OnHasAttachmentsChanged(bool newValue) => OnVisualPropertyChanged(this, HasAttachmentsProperty);
    partial void OnIsUnreadChanged(bool newValue) => OnVisualPropertyChanged(this, IsUnreadProperty);
    partial void OnIsFlaggedChanged(bool newValue) => OnVisualPropertyChanged(this, IsFlaggedProperty);
    partial void OnMeaningToggleTextChanged(string newValue) => OnVisualPropertyChanged(this, MeaningToggleTextProperty);
    partial void OnSemanticPlaceholderTextChanged(string newValue) => OnVisualPropertyChanged(this, SemanticPlaceholderTextProperty);
    partial void OnSemanticSearchExplanationTextChanged(string newValue) => OnVisualPropertyChanged(this, SemanticSearchExplanationTextProperty);
    partial void OnSemanticUnavailableReasonTextChanged(string newValue) => OnVisualPropertyChanged(this, SemanticUnavailableReasonTextProperty);
    partial void OnSenderSuggestionsChanged(IEnumerable<SearchBarContactSuggestion>? newValue) => OnVisualPropertyChanged(this, SenderSuggestionsProperty);
    partial void OnSelectedSenderContactChanged(SearchBarContactSuggestion? newValue) => OnVisualPropertyChanged(this, SelectedSenderContactProperty);
    partial void OnSenderSuggestionItemTemplateChanged(DataTemplate? newValue) => OnVisualPropertyChanged(this, SenderSuggestionItemTemplateProperty);
    partial void OnSenderPlaceholderTextChanged(string newValue) => OnVisualPropertyChanged(this, SenderPlaceholderTextProperty);
    partial void OnScopeOptionsSourceChanged(IEnumerable<SearchBarOptionItem>? newValue) => OnVisualPropertyChanged(this, ScopeOptionsSourceProperty);
    partial void OnReachOptionsSourceChanged(IEnumerable<SearchBarOptionItem>? newValue) => OnVisualPropertyChanged(this, ReachOptionsSourceProperty);
    partial void OnDateOptionsSourceChanged(IEnumerable<SearchBarOptionItem>? newValue) => OnVisualPropertyChanged(this, DateOptionsSourceProperty);
    partial void OnSearchButtonTextChanged(string newValue) => OnVisualPropertyChanged(this, SearchButtonTextProperty);
    partial void OnSearchOptionsTextChanged(string newValue) => OnVisualPropertyChanged(this, SearchOptionsTextProperty);
    partial void OnRecentSearchesTextChanged(string newValue) => OnVisualPropertyChanged(this, RecentSearchesTextProperty);
    partial void OnClearHistoryTextChanged(string newValue) => OnVisualPropertyChanged(this, ClearHistoryTextProperty);
    partial void OnClearTextChanged(string newValue) => OnVisualPropertyChanged(this, ClearTextProperty);
    partial void OnEmptyHistoryTextChanged(string newValue) => OnVisualPropertyChanged(this, EmptyHistoryTextProperty);
    partial void OnScopeLabelTextChanged(string newValue) => OnVisualPropertyChanged(this, ScopeLabelTextProperty);
    partial void OnReachLabelTextChanged(string newValue) => OnVisualPropertyChanged(this, ReachLabelTextProperty);
    partial void OnSenderLabelTextChanged(string newValue) => OnVisualPropertyChanged(this, SenderLabelTextProperty);
    partial void OnDateLabelTextChanged(string newValue) => OnVisualPropertyChanged(this, DateLabelTextProperty);
    partial void OnAttachmentsFilterTextChanged(string newValue) => OnVisualPropertyChanged(this, AttachmentsFilterTextProperty);
    partial void OnUnreadFilterTextChanged(string newValue) => OnVisualPropertyChanged(this, UnreadFilterTextProperty);
    partial void OnFlaggedFilterTextChanged(string newValue) => OnVisualPropertyChanged(this, FlaggedFilterTextProperty);
    partial void OnResetTextChanged(string newValue) => OnVisualPropertyChanged(this, ResetTextProperty);
    partial void OnRemoveSenderTextChanged(string newValue) => OnVisualPropertyChanged(this, RemoveSenderTextProperty);
}
