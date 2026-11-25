#if DEBUG
using Wino.Mail.Linux.Views.Settings.Accounts;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Sign-in and Troubleshooting: <c>['signin-state', path]</c>, <c>['signin', 'again']</c>,
/// <c>['signin', 'password', user, '@lab:…']</c> (Exchange password; the value is read from the file, never logged),
/// <c>['trouble-state', path]</c>, <c>['trouble', 'log-on'|'log-off'|'export'|'upload']</c>.
/// </summary>
internal static partial class AccountPagesDebugActions
{
    private static bool RunSignIn(string[] parts)
    {
        switch (parts)
        {
            case ["signin-state", var path]:
                DebugActions.WriteText(path, (AccountSignInPage.DebugCurrent ?? throw new InvalidOperationException("The sign-in page is not open.")).DebugDescribe());
                return true;
            case ["signin", "again"]:
                Execute(SignIn().SignInAgainCommand, null);
                return true;
            case ["signin", "password", var user, var password]:
            {
                var vm = SignIn();
                vm.ExchangeUserName = user;
                vm.ExchangePassword = AccountSetupDebugActions.LabValue(password);
                Execute(vm.UpdateExchangePasswordCommand, null);
                return true;
            }
            case ["trouble-state", var path]:
                DebugActions.WriteText(path, (AccountTroubleshootingPage.DebugCurrent ?? throw new InvalidOperationException("The troubleshooting page is not open.")).DebugDescribe());
                return true;
            case ["trouble", var verb]:
            {
                var vm = (AccountTroubleshootingPage.DebugCurrent ?? throw new InvalidOperationException("The troubleshooting page is not open.")).DebugViewModel;
                switch (verb)
                {
                    case "log-on": vm.IsProtocolLogEnabled = true; break;
                    case "log-off": vm.IsProtocolLogEnabled = false; break;
                    case "export": Execute(vm.ExportProtocolLogsCommand, null); break;
                    case "upload": Execute(vm.UploadProtocolLogsCommand, null); break;
                    default: return false;
                }
                return true;
            }
            default:
                return false;
        }
    }

    private static Wino.Mail.ViewModels.AccountSettings.AccountSignInPageViewModel SignIn()
        => (AccountSignInPage.DebugCurrent ?? throw new InvalidOperationException("The sign-in page is not open.")).DebugViewModel;
}
#endif
