using Wino.Core.Domain.Interfaces;
using Wino.Core.ViewModels;

namespace Wino.Mail.ViewModels;

public partial class IdlePageViewModel : CoreBaseViewModel
{
    public IdlePageViewModel(IMailDialogService dialogService)  { }
}
