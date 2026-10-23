using System.Collections.ObjectModel;
using System.Threading.Tasks;
using MailCal.Mail.Controls.Core.SearchBar;
using MailCal.Mail.WinUI.Models;

namespace MailCal.Mail.WinUI.Interfaces;

public interface ITitleBarSearchHost
{
    SearchBarMode SearchMode { get; }
    string SearchText { get; set; }
    ObservableCollection<TitleBarSearchSuggestion> SearchSuggestions { get; }

    Task OnTitleBarSearchTextChangedAsync();
    void OnTitleBarSearchSuggestionChosen(TitleBarSearchSuggestion suggestion);
    Task OnTitleBarSearchSubmittedAsync(string queryText, TitleBarSearchSuggestion? chosenSuggestion);
}
