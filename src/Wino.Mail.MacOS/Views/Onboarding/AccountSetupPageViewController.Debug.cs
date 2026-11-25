#if DEBUG
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.MacOS.Views.Onboarding;

/// <summary>
/// Debug bridge commands for the address-first setup page: <c>acct-state</c>, <c>acct-email ADDRESS</c>,
/// <c>acct-name NAME</c>, <c>acct-color first|none</c>, <c>acct-primary</c>, <c>acct-back</c>, <c>acct-local</c>,
/// <c>acct-choice INDEX</c>, <c>acct-provider KIND</c>, <c>acct-advanced</c> and <c>acct-picker</c>. They only type an address or a name and
/// press the page's buttons; no credential is ever filled.
/// </summary>
public sealed partial class AccountSetupPageViewController
{
    private void RegisterDebugCommands()
    {
        MacDebugBridge.Register("acct-state", _ => Dispatch(DescribeState));
        MacDebugBridge.Register("acct-known", _ => Dispatch(() =>
            $"text={_knownText?.Frame} detaches={_knownText?.DetachesHiddenViews} views={string.Join("|", _knownText?.ArrangedSubviews.Select(v => $"{v.GetType().Name}:{v.Frame}:hidden={v.Hidden}") ?? [])} chips='{_knownChips?.StringValue}'"));
        MacDebugBridge.Register("acct-email", args => Dispatch(() => { ViewModel.EmailAddress = string.Join(' ', args); return DescribeState(); }));
        MacDebugBridge.Register("acct-name", args => Dispatch(() => { ViewModel.AccountName = string.Join(' ', args); return DescribeState(); }));
        MacDebugBridge.Register("acct-color", args => Dispatch(() =>
        {
            var none = args.Length > 0 && args[0].Equals("none", StringComparison.OrdinalIgnoreCase);
            ViewModel.SelectedColor = none ? null : ViewModel.AvailableColors.FirstOrDefault();
            return "color " + (ViewModel.SelectedColor?.Hex ?? "none");
        }));
        MacDebugBridge.Register("acct-primary", async _ =>
        {
            if (!ViewModel.PrimaryCommand.CanExecute(null)) return "primary disabled; " + DescribeState();
            await ViewModel.PrimaryCommand.ExecuteAsync(null);
            return DescribeState();
        });
        MacDebugBridge.Register("acct-back", async _ =>
        {
            if (!ViewModel.BackCommand.CanExecute(null)) return "back disabled; " + DescribeState();
            await ViewModel.BackCommand.ExecuteAsync(null);
            return DescribeState();
        });
        MacDebugBridge.Register("acct-advanced", async _ =>
        {
            if (!ViewModel.OpenAdvancedSetupCommand.CanExecute(null)) return "advanced disabled; " + DescribeState();
            await ViewModel.OpenAdvancedSetupCommand.ExecuteAsync(null);
            return DescribeState();
        });
        MacDebugBridge.Register("acct-local", async _ =>
        {
            await ViewModel.StartLocalOnlyCommand.ExecuteAsync(null);
            return DescribeState();
        });
        MacDebugBridge.Register("acct-choice", args => Dispatch(() =>
        {
            var index = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 0;
            if (index < 0 || index >= ViewModel.StepChoices.Count) return $"only {ViewModel.StepChoices.Count} choices";
            ViewModel.SelectedChoice = ViewModel.StepChoices[index];
            return DescribeState();
        }));
        MacDebugBridge.Register("acct-provider", async args =>
        {
            var kind = args.Length > 0 && Enum.TryParse<AccountSetupProviderChoiceKind>(args[0], true, out var parsed) ? parsed : AccountSetupProviderChoiceKind.Imap;
            var choice = ViewModel.ProviderChoices.FirstOrDefault(item => item.Kind == kind);
            if (choice is null) return "no provider " + kind;
            await ViewModel.ChangeProviderCommand.ExecuteAsync(choice);
            return DescribeState();
        });
        MacDebugBridge.Register("acct-picker", _ => Dispatch(() => { ShowProviderPicker(_changeButton.Hidden ? _primary : _changeButton); return "ok"; }));
    }

    private string DescribeState()
    {
        var vm = ViewModel;
        return $"step={vm.Step} phase={vm.Phase} route={vm.Route} title='{vm.StepTitle}' known='{vm.KnownText}' primary={(vm.IsPrimaryVisible ? vm.PrimaryText : "hidden")} " +
               $"back={vm.IsBackVisible} social={(vm.IsFooterSocialSignInVisible ? vm.NativeSignInButtonText : "hidden")} choices={vm.StepChoices.Count} rows={vm.AppRows.Count} " +
               $"shelf='{vm.ProviderShelfCaption}' error='{vm.IdentityError}{vm.AccountNameError}{vm.FeaturesError}' failure={(vm.IsFailureOpen ? vm.FailureTitle : "none")}";
    }

    private async Task<string> Dispatch(Func<string> action)
    {
        string result = string.Empty;
        await Dispatcher.ExecuteOnUIThread(() => result = action());
        return result;
    }
}
#endif
