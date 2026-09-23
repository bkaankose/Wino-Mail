using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Mail.Controls.Core.SearchBar;

namespace Wino.Mail.WinUI.Interfaces;

public interface IMailTitleBarSearchHost : ITitleBarSearchHost
{
    Task OnMailSearchSubmittedAsync(SearchBarSubmittedEventArgs args);
}
