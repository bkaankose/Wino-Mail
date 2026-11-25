using Wino.Core.Domain;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>
/// Account setup progress (Windows AccountSetupProgressPage, macOS controller of the same name): the title, the step list
/// with a status per step, the success bar, and on failure the error with Go back and Try again. The server form, the
/// catalog provider page and the Exchange page navigate here; while the setup runs there is no Back.
/// </summary>
internal sealed class AccountSetupProgressView : WinoView<AccountSetupProgressPageViewModel>
{
    private readonly Adw.ToolbarView _root;
    private readonly Adw.HeaderBar _header;
    private readonly AccountSetupStepList _steps = new();

    public AccountSetupProgressView(AccountSetupProgressPageViewModel viewModel, WinoViewServices services) : base(viewModel, services)
    {
        var vm = ViewModel;
        var title = OnboardingUi.Title(Translator.AccountSetup_Title);
        title.AccessibleRole = Gtk.AccessibleRole.Heading;
        Bindings.Own(_steps.Bind(vm.Steps, Dispatcher, ReportError));

        var success = new WinoInfoBar(WinoInfoBarSeverity.Success, null, Translator.AccountSetup_SuccessMessage);
        Bindings.Own(success);
        Bind.Bind(nameof(vm.IsSetupComplete), static s => s.IsSetupComplete, success.Root.SetVisible);

        var failure = new WinoInfoBar(WinoInfoBarSeverity.Error);
        Bindings.Own(failure);
        failure.Message = string.Empty;
        Bind.Bind(nameof(vm.FailureMessage), static s => s.FailureMessage, text => failure.Message = text ?? string.Empty);
        var back = OnboardingUi.Button(Translator.AccountSetup_GoBackButton);
        Bind.BindCommand(back, vm.GoBackCommand);
        var retry = OnboardingUi.Button(Translator.AccountSetup_TryAgainButton, suggested: true);
        Bind.BindCommand(retry, vm.TryAgainCommand);
        var failurePanel = OnboardingUi.Column(12, failure.Root, OnboardingUi.Centered(back, retry));
        Bind.Bind(nameof(vm.IsSetupFailed), static s => s.IsSetupFailed, failurePanel.SetVisible);

        var column = OnboardingUi.Column(24, title, _steps.Root, success.Root, failurePanel);
        column.SetMarginTop(32);
        column.SetMarginBottom(32);
        column.SetMarginStart(16);
        column.SetMarginEnd(16);
        column.SetValign(Gtk.Align.Center);
        var clamp = Adw.Clamp.New();
        clamp.SetMaximumSize(520);
        clamp.SetChild(column);
        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroller.SetVexpand(true);
        scroller.SetChild(clamp);
        _header = Adw.HeaderBar.New();
        _header.SetShowTitle(false);
        _root = Adw.ToolbarView.New();
        _root.AddTopBar(_header);
        _root.SetContent(scroller);
#if DEBUG
        _debugCurrent = new WeakReference<AccountSetupProgressView>(this);
#endif
    }

    public override Gtk.Widget Root => _root;

    public override bool HasPendingWork => base.HasPendingWork || ViewModel.TryAgainCommand.IsRunning;

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        _header.SetVisible(!OnboardingUi.HasWindowHeader(_root));
        return ViewModel.InitializeAsync(mode, parameter);
    }

#if DEBUG
    private static WeakReference<AccountSetupProgressView>? _debugCurrent;

    public static AccountSetupProgressView? DebugCurrent => _debugCurrent is not null && _debugCurrent.TryGetTarget(out var view) && !view.IsReleased ? view : null;

    public string DebugDescribe()
        => $"complete={ViewModel.IsSetupComplete} failed={ViewModel.IsSetupFailed} failure='{ViewModel.FailureMessage}'\nsteps={_steps.Describe()}\n";
#endif
}
