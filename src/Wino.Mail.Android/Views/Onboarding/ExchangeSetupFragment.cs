using Android.Text;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;
using Wino.Presentation.Android.Bindings;

namespace Wino.Mail.Android.Views.Onboarding;

/// <summary>
/// Advanced setup for on-premises Exchange (Windows ExchangeSettingsPage, iOS and Mac twins), opened from the server
/// page's "Using Exchange Server?" link: the server first (address, server or Autodiscover, connection method), then
/// the sign-in the server asks for. Android has no embedded browser for the organization sign-in, so a server that
/// offers it shows the shared notice above the password instead.
/// </summary>
public sealed class ExchangeSetupFragment : WinoFragment<ExchangeSettingsPageViewModel>
{
    protected override int LayoutResource => Resource.Layout.onboarding_page;

    protected override void OnBind(View view, ExchangeSettingsPageViewModel viewModel, IDispatcher dispatcher)
    {
#if DEBUG
        global::Wino.Mail.Android.Views.Debug.DebugOnboarding.Track(viewModel);
#endif
        var binder = new ViewBinder(Bindings, dispatcher, ReportError);
        WinoInsets.ApplyWithKeyboard(view.FindViewById(Resource.Id.onboarding_insets) ?? view);
        OnboardingViews.Toolbar(view, binder, () => Run(viewModel.BackCommand))?.SetHeading(Translator.ExchangeSetup_Title);
        OnboardingViews.InterceptBack(this, binder, viewModel, nameof(viewModel.IsBusy), _ => true, () => Run(viewModel.BackCommand));

        if (view.FindViewById<LinearLayout>(Resource.Id.onboarding_form) is not { Context: { } context } root) return;
        var form = new OnboardingForm<ExchangeSettingsPageViewModel>(context, root, binder, viewModel);
        form.Help(Translator.ExchangeSetup_Description);

        var failure = form.InfoBar(WinoInfoBarSeverity.Error, Translator.AccountSetup_Failure_Title);
        failure.IsClosable = true;
        binder.BindAny(viewModel, vm => (vm.IsFailureOpen, vm.FailureMessage), state =>
        {
            failure.Message = state.FailureMessage;
            failure.IsOpen = state.IsFailureOpen;
        });
        EventHandler<WinoInfoBarClosedEventArgs> closed = (_, _) => Run(viewModel.HideFailureCommand);
        failure.Closed += closed;
        binder.Scope.Own(new Wino.Presentation.Android.Bindings.ActionDisposable(() => failure.Closed -= closed));

        form.GroupWhen(vm => vm.IsServerStageVisible, () => BuildServerStage(form, viewModel));
        form.GroupWhen(vm => vm.IsSignInStage, () => BuildSignInStage(form, viewModel));

        if (view.FindViewById(Resource.Id.onboarding_busy) is { } busy)
            binder.Visible(busy, viewModel, nameof(viewModel.IsBusy), vm => vm.IsBusy);
        if (view.FindViewById<TextView>(Resource.Id.onboarding_busy_text) is { } busyText)
            binder.BindAny(viewModel, vm => vm.IsBusy ? vm.BusyText : string.Empty, text => OnboardingViews.Caption(busyText, text));
        if (view.FindViewById<MaterialButton>(Resource.Id.onboarding_secondary) is { } back)
        {
            back.Text = Translator.Buttons_Back;
            binder.Command(back, viewModel.BackCommand);
        }
        if (view.FindViewById<MaterialButton>(Resource.Id.onboarding_primary) is { } primary)
        {
            binder.Bind(viewModel, nameof(viewModel.PrimaryText), vm => vm.PrimaryText, text => primary.Text = text);
            binder.Command(primary, viewModel.PrimaryCommand);
            binder.BindAny(viewModel, vm => vm.IsPrimaryVisible, visible => OnboardingViews.Show(primary, visible));
        }
        if (view.FindViewById<AndroidX.Core.Widget.NestedScrollView>(Resource.Id.onboarding_scroll) is { } scroll)
            binder.Bind(viewModel, nameof(viewModel.IsSignInStage), vm => vm.IsSignInStage, _ => scroll.ScrollTo(0, 0));
    }

    private static void BuildServerStage(OnboardingForm<ExchangeSettingsPageViewModel> form, ExchangeSettingsPageViewModel viewModel)
    {
        form.Field(Translator.ImapSetup_YourName, nameof(viewModel.DisplayName), vm => vm.DisplayName, (vm, value) => vm.DisplayName = value,
            InputTypes.ClassText | InputTypes.TextFlagCapWords, Translator.ImapSetup_YourNameDescription).PlaceholderText = Translator.IMAPSetupDialog_DisplayNamePlaceholder;
        form.Field(Translator.AccountSetup_EmailHeader, nameof(viewModel.EmailAddress), vm => vm.EmailAddress, (vm, value) => vm.EmailAddress = value,
            InputTypes.ClassText | InputTypes.TextVariationEmailAddress).PlaceholderText = Translator.AccountSetup_EmailPlaceholder;
        form.Field(Translator.ExchangeSetup_ServerHeader, nameof(viewModel.ServerAddress), vm => vm.ServerAddress, (vm, value) => vm.ServerAddress = value,
            InputTypes.ClassText | InputTypes.TextVariationUri).PlaceholderText = Translator.ExchangeSetup_ServerPlaceholder;
        var transports = viewModel.TransportOptions.Select(option => option.Title).ToList();
        form.Dropdown(Translator.ExchangeSetup_TransportHeader, transports, nameof(viewModel.SelectedTransport),
            vm => vm.SelectedTransport is { } selected ? vm.TransportOptions.IndexOf(selected) : 0,
            (vm, index) =>
            {
                if (index >= 0 && index < vm.TransportOptions.Count) vm.SelectedTransport = vm.TransportOptions[index];
            });
        form.Help(Translator.ExchangeSetup_TransportDescription);
    }

    private static void BuildSignInStage(OnboardingForm<ExchangeSettingsPageViewModel> form, ExchangeSettingsPageViewModel viewModel)
    {
        form.Row(() =>
        {
            var summary = form.Body(nameof(viewModel.ServerSummary), vm => vm.ServerSummary);
            summary.SetMaxLines(1);
            summary.Ellipsize = TextUtils.TruncateAt.Middle;
            if (summary.LayoutParameters is LinearLayout.LayoutParams parameters)
            {
                parameters.Width = 0;
                parameters.Weight = 1;
                parameters.Gravity = GravityFlags.CenterVertical;
                parameters.TopMargin = 0;
            }
            var change = form.Link(Translator.ImapSetup_ChangeAddress, viewModel.ChangeServerCommand);
            if (change.LayoutParameters is LinearLayout.LayoutParams changeParameters) changeParameters.Gravity = GravityFlags.CenterVertical;
        });
        form.Help(property: nameof(viewModel.SignInDescription), read: vm => vm.SignInDescription);

        var unavailable = form.InfoBar(WinoInfoBarSeverity.Informational);
        unavailable.Message = Translator.ExchangeSetup_ModernAuthUnavailable;
        form.Binder.BindAny(viewModel, vm => vm.IsModernAuthUnavailableNoticeVisible, open => unavailable.IsOpen = open);

        form.GroupWhen(vm => vm.IsPasswordStageVisible, () =>
        {
            var user = form.Field(Translator.ExchangeSetup_UserNameHeader, nameof(viewModel.UserName), vm => vm.UserName, (vm, value) => vm.UserName = value,
                InputTypes.ClassText | InputTypes.TextVariationEmailAddress, Translator.ExchangeSetup_UserNameDescription);
            form.Binder.Bind(viewModel, nameof(viewModel.UserNamePlaceholder), vm => vm.UserNamePlaceholder, text => user.PlaceholderText = text);
            form.Field(Translator.ProviderSelection_PasswordHeader_Account, nameof(viewModel.Password), vm => vm.Password, (vm, value) => vm.Password = value,
                InputTypes.ClassText | InputTypes.TextVariationPassword);
        });

        // Shown only where an embedded browser exists; the shared ViewModel keeps this stage closed on Android.
        form.ShowWhen(form.Button(Translator.ImapSetup_SignInWithOrganization, viewModel.SignInWithOrganizationCommand, primary: true),
            vm => vm.IsModernAuthStageVisible);
    }

    private void Run(System.Windows.Input.ICommand command)
    {
        try { if (command.CanExecute(null)) command.Execute(null); }
        catch (Exception exception) { ReportError(exception); }
    }
}
