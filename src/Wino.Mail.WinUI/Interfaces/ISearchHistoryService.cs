using System.Collections.Generic;
using Wino.Mail.Controls.Core.SearchBar;

namespace Wino.Mail.WinUI.Interfaces;

public interface ISearchHistoryService
{
    IReadOnlyList<string> GetHistory(SearchBarMode mode);

    void Record(SearchBarMode mode, string query);

    void Clear(SearchBarMode mode);
}
