using System.Collections.Generic;
using MailCal.Mail.Controls.Core.SearchBar;

namespace MailCal.Mail.WinUI.Interfaces;

public interface ISearchHistoryService
{
    IReadOnlyList<string> GetHistory(SearchBarMode mode);

    void Record(SearchBarMode mode, string query);

    void Clear(SearchBarMode mode);
}
