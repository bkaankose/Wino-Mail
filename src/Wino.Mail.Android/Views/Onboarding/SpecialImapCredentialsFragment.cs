using Android.Text;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;
using Wino.Presentation.Android.Bindings;

namespace Wino.Mail.Android.Views.Onboarding;

/// <summary>
/// iCloud and Yahoo sign-in (canvas Material pass board ImapSetup; Windows SpecialImapCredentialsPage): the provider's
/// setup hint, display name, address and app-specific password with the provider's help link, and the calendar choice
/// when the account uses its calendar. The server settings come from the shared provider catalogue, so this page has no
/// Advanced section. Sign in hands over to account setup, which validates the credentials.
/// </summary>
public sealed class SpecialImapCredentialsFragment : WinoFragment<SpecialImapCredentialsPageViewModel>
{
    protected override int LayoutResource => Resource.Layout.onboarding_page;

    protected override void OnBind(View view, SpecialImapCredentialsPageViewModel viewModel, IDispatcher dispatcher)
    {
        var binder = new ViewBinder(Bindings, dispatcher, ReportError);
        WinoInsets.ApplyWithKeyboard(view.FindViewById(Resource.Id.onboarding_insets) ?? view);
        var navigation = MainApplication.Services?.GetService<AndroidNavigationService>();
        if (OnboardingViews.Toolbar(view, binder, () => navigation?.TryHandleBack()) is { } toolbar)
            binder.Bind(viewModel, nameof(viewModel.ProviderName), vm => vm.ProviderName, name => toolbar.SetHeading(name));

        if (view.FindViewById<LinearLayout>(Resource.Id.onboarding_form) is { Context: { } context } root)
        {
            var form = new OnboardingForm<SpecialImapCredentialsPageViewModel>(context, root, binder, viewModel);
            form.Help(Translator.ProviderSelection_SpecialImap_Subtitle, topDp: 8);

            var hint = form.InfoBar(WinoInfoBarSeverity.Informational);
            binder.BindAny(viewModel, vm => (vm.SetupHint, vm.IsSetupHintVisible, vm.IsSetupHintBlocking), state =>
            {
                hint.Message = state.SetupHint;
                hint.Severity = state.IsSetupHintBlocking ? WinoInfoBarSeverity.Warning : WinoInfoBarSeverity.Informational;
                hint.IsOpen = state.IsSetupHintVisible;
            });

            form.Field(Translator.ProviderSelection_DisplayNameHeader, nameof(viewModel.DisplayName), vm => vm.DisplayName,
                (vm, value) => vm.DisplayName = value, InputTypes.ClassText | InputTypes.TextFlagCapWords).PlaceholderText = Translator.ProviderSelection_DisplayNamePlaceholder;
            form.Field(Translator.ProviderSelection_EmailHeader, nameof(viewModel.EmailAddress), vm => vm.EmailAddress,
                (vm, value) => vm.EmailAddress = value, InputTypes.ClassText | InputTypes.TextVariationEmailAddress).PlaceholderText = Translator.ProviderSelection_EmailPlaceholder;
            form.Group(() =>
            {
                var password = form.Field(Translator.ProviderSelection_AppPasswordHeader, nameof(viewModel.AppSpecificPassword), vm => vm.AppSpecificPassword,
                    (vm, value) => vm.AppSpecificPassword = value, InputTypes.ClassText | InputTypes.TextVariationPassword);
                binder.Bind(viewModel, nameof(viewModel.PasswordHeader), vm => vm.PasswordHeader, header => password.Hint = header);
                var link = form.Link(viewModel.HelpLinkText, viewModel.OpenAppPasswordHelpCommand);
                binder.Bind(viewModel, nameof(viewModel.HelpLinkText), vm => vm.HelpLinkText, text => link.Text = text);
                binder.Visible(link, viewModel, nameof(viewModel.IsHelpLinkVisible), vm => vm.IsHelpLinkVisible);
            }, nameof(viewModel.RequiresAppSpecificPassword), vm => vm.RequiresAppSpecificPassword);

            // The calendar choice, when the account uses its calendar (Disabled, CalDAV, on this device).
            form.Group(() =>
            {
                string[] modes = [Translator.ProviderSelection_CalendarMode_DisabledTitle, Translator.ProviderSelection_CalendarMode_CalDavTitle, Translator.ProviderSelection_CalendarMode_LocalTitle];
                form.Dropdown(Translator.ProviderSelection_CalendarModeHeader, modes, nameof(viewModel.SelectedCalendarModeIndex),
                    vm => vm.SelectedCalendarModeIndex, (vm, index) => vm.SelectedCalendarModeIndex = index);
                form.Help(property: nameof(viewModel.SelectedCalendarModeIndex), read: vm => vm.SelectedCalendarModeIndex switch
                {
                    1 => vm.CalendarModeCalDavDescription,
                    2 => Translator.ProviderSelection_CalendarMode_LocalDescription,
                    _ => Translator.ProviderSelection_CalendarMode_DisabledDescription
                });
            }, nameof(viewModel.IsCalendarModeSelectionVisible), vm => vm.IsCalendarModeSelectionVisible);
        }

        OnboardingViews.Show(view.FindViewById(Resource.Id.onboarding_busy), false);
        if (view.FindViewById<MaterialButton>(Resource.Id.onboarding_secondary) is { } cancel)
        {
            cancel.Text = Translator.Buttons_Cancel;
            binder.Click(cancel, () => navigation?.TryHandleBack());
        }
        if (view.FindViewById<MaterialButton>(Resource.Id.onboarding_primary) is { } signIn)
        {
            signIn.Text = Translator.Buttons_SignIn;
            binder.Click(signIn, () => { if (viewModel.ProceedCommand.CanExecute(null)) viewModel.ProceedCommand.Execute(null); });
            binder.Bind(viewModel, nameof(viewModel.CanProceed), vm => vm.CanProceed, enabled => signIn.Enabled = enabled);
        }
    }
}
