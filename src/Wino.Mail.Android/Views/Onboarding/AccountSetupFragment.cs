using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;
using Wino.Presentation.Android.Bindings;
using Wino.Presentation.Bindings;

namespace Wino.Mail.Android.Views.Onboarding;

/// <summary>
/// Account setup progress (Windows AccountSetupProgressPage, iOS AccountSetupProgressPage) in the Material look: the
/// title, the shared setup steps with their status, the success bar, and after a failure the shared message with Go back
/// and Try again. When setup succeeds the shared navigation coordinator shows the shell, as on Windows and iOS.
/// </summary>
public sealed class AccountSetupFragment : WinoFragment<AccountSetupProgressPageViewModel>
{
    protected override int LayoutResource => Resource.Layout.fragment_account_setup;

    protected override void OnBind(View view, AccountSetupProgressPageViewModel viewModel, IDispatcher dispatcher)
    {
        var binder = new ViewBinder(Bindings, dispatcher, ReportError);
        WinoInsets.Apply(view.FindViewById(Resource.Id.onboarding_insets) ?? view);

        // The page cannot be left while setup runs; after a failure back leaves it as usual.
        OnboardingViews.InterceptBack(this, binder, viewModel, nameof(viewModel.IsSetupFailed), vm => !vm.IsSetupFailed, static () => { });

        if (view.FindViewById<TextView>(Resource.Id.setup_title) is { } title) title.Text = Translator.AccountSetup_Title;
        BindSteps(view, binder, viewModel);

        if (view.FindViewById<WinoInfoBar>(Resource.Id.setup_success) is { } success)
        {
            success.IsClosable = false;
            success.Severity = WinoInfoBarSeverity.Success;
            success.Message = Translator.AccountSetup_SuccessMessage;
            binder.Visible(success, viewModel, nameof(viewModel.IsSetupComplete), vm => vm.IsSetupComplete);
        }
        if (view.FindViewById<WinoInfoBar>(Resource.Id.setup_failure) is { } failure)
        {
            failure.IsClosable = false;
            failure.Severity = WinoInfoBarSeverity.Error;
            binder.Bind(viewModel, nameof(viewModel.FailureMessage), vm => vm.FailureMessage, message => failure.Message = message);
            binder.Visible(failure, viewModel, nameof(viewModel.IsSetupFailed), vm => vm.IsSetupFailed);
        }

        if (view.FindViewById(Resource.Id.onboarding_actions) is { } actions)
            binder.Visible(actions, viewModel, nameof(viewModel.IsSetupFailed), vm => vm.IsSetupFailed);
        if (view.FindViewById<MaterialButton>(Resource.Id.onboarding_secondary) is { } back)
        {
            back.Text = Translator.AccountSetup_GoBackButton;
            binder.Command(back, viewModel.GoBackCommand);
        }
        if (view.FindViewById<MaterialButton>(Resource.Id.onboarding_primary) is { } retry)
        {
            retry.Text = Translator.AccountSetup_TryAgainButton;
            binder.Command(retry, viewModel.TryAgainCommand);
        }
    }

    /// <summary>One row per shared step: an empty circle while pending, a spinner while running, a check or a dismiss glyph.</summary>
    private static void BindSteps(View view, ViewBinder binder, AccountSetupProgressPageViewModel viewModel)
    {
        if (view.FindViewById<LinearLayout>(Resource.Id.setup_steps) is not { Context: { } context } list) return;
        var inflater = LayoutInflater.From(context) ?? throw new InvalidOperationException("No layout inflater.");
        BindingScope rows = new();
        binder.Scope.Own(new Wino.Presentation.Android.Bindings.ActionDisposable(() => rows.Dispose()));
        binder.Collection(viewModel.Steps, () =>
        {
            rows.Dispose();
            rows = new BindingScope();
            var rowBinder = binder.Child(rows);
            list.RemoveAllViews();
            foreach (var step in viewModel.Steps.ToList())
            {
                if (inflater.Inflate(Resource.Layout.onboarding_setup_step, list, false) is not { } row) continue;
                var spinner = row.FindViewById<CircularProgressIndicator>(Resource.Id.step_spinner);
                var icon = row.FindViewById<WinoIconView>(Resource.Id.step_icon);
                var error = row.FindViewById<TextView>(Resource.Id.step_error);
                if (row.FindViewById<TextView>(Resource.Id.step_title) is { } stepTitle) stepTitle.Text = step.Title;
                rowBinder.BindAny(step, s => (s.Status, s.ErrorMessage, s.AutomationName), state =>
                {
                    OnboardingViews.Show(spinner, state.Status == AccountSetupStepStatus.InProgress);
                    if (icon is not null)
                    {
                        icon.Icon = state.Status switch
                        {
                            AccountSetupStepStatus.Succeeded => WinoIconGlyph.CheckmarkCircleFilled,
                            AccountSetupStepStatus.Failed => WinoIconGlyph.DismissCircle,
                            AccountSetupStepStatus.Pending => WinoIconGlyph.Circle,
                            _ => WinoIconGlyph.None
                        };
                        icon.SetTextColor(state.Status switch
                        {
                            AccountSetupStepStatus.Succeeded => WinoColors.Success(context),
                            AccountSetupStepStatus.Failed => WinoColors.Critical(context),
                            _ => WinoColors.TertiaryText(context)
                        });
                    }
                    OnboardingViews.Caption(error, state.Status == AccountSetupStepStatus.Failed ? state.ErrorMessage : null);
                    row.ContentDescription = state.AutomationName;
                });
                list.AddView(row);
            }
        });
    }
}
