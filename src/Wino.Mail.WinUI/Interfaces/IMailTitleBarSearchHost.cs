using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Mail.Controls.Core.SearchBar;

namespace Wino.Mail.WinUI.Interfaces;

public sealed record SemanticSearchAvailability(bool IsAvailable, string UnavailableReason);

public interface IMailTitleBarSearchHost : ITitleBarSearchHost
{
    event System.EventHandler<bool> SemanticSearchBusyChanged;

    bool IsSemanticSearchAvailable { get; }

    bool IsSemanticSearchBusy { get; }

    string SemanticUnavailableReasonText { get; }

    IReadOnlyList<SearchBarOptionItem> ScopeOptions { get; }

    IReadOnlyList<SearchBarContactSuggestion> SenderSuggestions { get; }

    Task RequestSenderSuggestionsAsync(string query);

    Task<SemanticSearchAvailability> GetSemanticSearchAvailabilityAsync(SearchBarFilterSnapshot filters);

    Task OnMailSearchSubmittedAsync(SearchBarSubmittedEventArgs args);
}
