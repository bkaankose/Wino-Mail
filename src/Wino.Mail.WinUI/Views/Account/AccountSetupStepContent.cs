using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;

namespace Wino.Views;

/// <summary>
/// The content of the setup page's step host. A new instance per step makes the host apply the step's template again
/// even when the same step is shown again.
/// </summary>
public sealed partial class AccountSetupStepContent
{
    public AccountSetupStepContent(AccountSetupPageViewModel viewModel, AccountSetupStep step)
    {
        ViewModel = viewModel;
        Step = step;
    }

    public AccountSetupPageViewModel ViewModel { get; }
    public AccountSetupStep Step { get; }
}
