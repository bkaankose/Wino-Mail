using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Wino.Mail.WinUI.Models;

namespace Wino.Mail.WinUI.Interfaces;

public interface ITitleBarSearchHost
{
    string SearchText { get; set; }
    string SearchPlaceholderText { get; }
    ObservableCollection<TitleBarSearchSuggestion> SearchSuggestions { get; }

    Task OnTitleBarSearchTextChangedAsync();
    void OnTitleBarSearchSuggestionChosen(TitleBarSearchSuggestion suggestion);
    Task OnTitleBarSearchSubmittedAsync(string queryText, TitleBarSearchSuggestion? chosenSuggestion);
}
