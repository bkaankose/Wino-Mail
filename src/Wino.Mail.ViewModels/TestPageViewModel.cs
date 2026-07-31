using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

public partial class TestPageViewModel : MailBaseViewModel
{
    [ObservableProperty]
    public partial string Subject { get; set; }

    [ObservableProperty]
    public partial string Preview { get; set; }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        if (parameters is MailItemViewModel mail)
        {
            RefreshMailItemAsync(mail);
        }
    }

    public Task RefreshMailItemAsync(MailItemViewModel mailItemViewModel)
    {
        Subject = mailItemViewModel.Subject;
        Preview = mailItemViewModel.PreviewText;

        return Task.FromResult(true);
    }
}
