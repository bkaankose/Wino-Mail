#if DEBUG
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.UIKit.Views.Onboarding;

/// <summary>
/// Debug bridge commands for the address-first setup page (the Mac twin): <c>acct-state</c>, <c>acct-email ADDRESS</c>,
/// <c>acct-name NAME</c>, <c>acct-color first|none</c>, <c>acct-primary</c>, <c>acct-back</c>, <c>acct-local</c>,
/// <c>acct-choice INDEX</c> and <c>acct-provider KIND</c>. They only type an address or a name and press the page's
/// buttons; no credential is ever filled.
/// </summary>
public sealed partial class AccountSetupPageViewController
{
    private void RegisterDebugCommands()
    {
        UIKitDebugBridge.Register("acct-state", _ => Dispatch(DescribeState));
        UIKitDebugBridge.Register("acct-email", args => Dispatch(() => { ViewModel.EmailAddress = string.Join(' ', args); return DescribeState(); }));
        UIKitDebugBridge.Register("acct-name", args => Dispatch(() => { ViewModel.AccountName = string.Join(' ', args); return DescribeState(); }));
        UIKitDebugBridge.Register("acct-color", args => Dispatch(() =>
        {
            var none = args.Length > 0 && args[0].Equals("none", StringComparison.OrdinalIgnoreCase);
            ViewModel.SelectedColor = none ? null : ViewModel.AvailableColors.FirstOrDefault();
            return "color " + (ViewModel.SelectedColor?.Hex ?? "none");
        }));
        UIKitDebugBridge.Register("acct-primary", _ => RunCommand(() => ViewModel.PrimaryCommand.CanExecute(null) ? ViewModel.PrimaryCommand.ExecuteAsync(null) : null));
        UIKitDebugBridge.Register("acct-back", _ => RunCommand(() => ViewModel.BackCommand.CanExecute(null) ? ViewModel.BackCommand.ExecuteAsync(null) : null));
        UIKitDebugBridge.Register("acct-local", _ => RunCommand(() => ViewModel.StartLocalOnlyCommand.ExecuteAsync(null)));
        UIKitDebugBridge.Register("acct-choice", args => Dispatch(() =>
        {
            var index = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 0;
            if (index < 0 || index >= ViewModel.StepChoices.Count) return $"only {ViewModel.StepChoices.Count} choices";
            ViewModel.SelectedChoice = ViewModel.StepChoices[index];
            return DescribeState();
        }));
        UIKitDebugBridge.Register("acct-provider", async args =>
        {
            var kind = args.Length > 0 && Enum.TryParse<AccountSetupProviderChoiceKind>(args[0], true, out var parsed) ? parsed : AccountSetupProviderChoiceKind.Imap;
            var choice = ViewModel.ProviderChoices.FirstOrDefault(item => item.Kind == kind);
            if (choice is null) return "no provider " + kind;
            return await RunCommand(() => ViewModel.ChangeProviderCommand.ExecuteAsync(choice));
        });
    }

    private string DescribeState()
    {
        var vm = ViewModel;
        return $"step={vm.Step} phase={vm.Phase} route={vm.Route} title='{vm.StepTitle}' known='{vm.KnownText}' primary={(vm.IsPrimaryVisible ? vm.PrimaryText : "hidden")} " +
               $"back={vm.IsBackVisible} social={(vm.IsFooterSocialSignInVisible ? vm.NativeSignInButtonText : "hidden")} choices={vm.StepChoices.Count} rows={vm.AppRows.Count} " +
               $"shelf='{vm.ProviderShelfCaption}' error='{vm.IdentityError}{vm.AccountNameError}{vm.FeaturesError}' failure={(vm.IsFailureOpen ? vm.FailureTitle : "none")}";
    }

    /// <summary>Starts a command on the main thread and reports the state once it finished; null means it was disabled.</summary>
    private async Task<string> RunCommand(Func<Task?> start)
    {
        Task? running = null;
        await Dispatcher.ExecuteOnUIThread(() => running = start());
        if (running is null) return "disabled; " + await Dispatch(DescribeState);
        await running;
        return await Dispatch(DescribeState);
    }

    private async Task<string> Dispatch(Func<string> action)
    {
        string result = string.Empty;
        await Dispatcher.ExecuteOnUIThread(() => result = action());
        return result;
    }
}
#endif
