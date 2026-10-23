using System.Collections.Generic;
using System.Threading.Tasks;
using MailCal.Mail.Controls.Core.SearchBar;

namespace MailCal.Mail.WinUI.Interfaces;

public interface IMailTitleBarSearchHost : ITitleBarSearchHost
{
    Task OnMailSearchSubmittedAsync(SearchBarSubmittedEventArgs args);
}
