using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Onboarding;

/// <summary>
/// Advanced setup for on-premises Exchange (Windows ExchangeSettingsPage, Mac twin): the server first (address, server
/// or Autodiscover, connection method), then the sign-in the server asks for. iOS has no embedded browser for the
/// organization sign-in, so a server that offers it shows a notice above the password instead.
/// </summary>
public sealed class ExchangeSettingsPageViewController(ExchangeSettingsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<ExchangeSettingsPageViewModel>(viewModel, dispatcher, logger)
{
    public override void LoadView()
    {
        var vm = ViewModel;
        var form = new FormBinder<ExchangeSettingsPageViewModel>(vm, Bindings, Dispatcher, ReportError);

        var icon = new WinoIconView(WinoIconGlyph.Exchange, 44);
        WinoLayout.Size(icon, 44, 44);
        var header = OnboardingPageView.Header(icon, new UILabel { Text = Translator.ExchangeSetup_Title },
            new UILabel { Text = Translator.ExchangeSetup_Description });

        var failure = new WinoInfoBar(WinoInfoBarSeverity.Error, Translator.AccountSetup_Failure_Title) { IsClosable = true };
        form.Bind(nameof(vm.FailureMessage), s => s.FailureMessage, text => failure.Message = text);
        form.Visible(failure, nameof(vm.IsFailureOpen), s => s.IsFailureOpen);
        EventHandler<WinoInfoBarClosedEventArgs> closeFailure = (_, _) => vm.HideFailureCommand.Execute(null);
        failure.Closed += closeFailure;
        Bindings.Own(new ActionDisposable(() => failure.Closed -= closeFailure));

        var content = WinoLayout.VStack(WinoStyle.Space4, failure, BuildServerStage(form), BuildSignInStage(form));
        content.Alignment = UIStackViewAlignment.Fill;

        var spinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = true };
        var busyText = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText);
        var busy = WinoLayout.HStack(WinoStyle.Space2, spinner, busyText);
        form.Label(busyText, nameof(vm.BusyText), s => s.BusyText);
        form.Bind(nameof(vm.IsBusy), s => s.IsBusy, isBusy =>
        {
            busy.Hidden = !isBusy;
            if (isBusy) spinner.StartAnimating(); else spinner.StopAnimating();
        });

        var back = form.Command(Translator.Buttons_Back, vm.BackCommand);
        var primary = form.Command(Translator.ProviderSelection_ContinueButton, vm.PrimaryCommand, primary: true);
        form.Bind(nameof(vm.PrimaryText), s => s.PrimaryText, text =>
        {
            if (primary.Configuration is not { } configuration) return;
            configuration.Title = text ?? string.Empty;
            primary.Configuration = configuration;
        });
        form.Visible(primary, nameof(vm.IsPrimaryVisible), s => s.IsPrimaryVisible);

        View = new OnboardingPageView(header, content, busy, [back], primary, 560);
    }

    private static UIView BuildServerStage(FormBinder<ExchangeSettingsPageViewModel> form)
    {
        var vm = form.Source;
        var grid = new FormGrid();
        var name = form.Text(nameof(vm.DisplayName), s => s.DisplayName, (s, v) => s.DisplayName = v,
            Translator.IMAPSetupDialog_DisplayNamePlaceholder, Translator.ImapSetup_YourName);
        name.AutocapitalizationType = UITextAutocapitalizationType.Words;
        name.TextContentType = UITextContentType.Name;
        grid.Row(Translator.ImapSetup_YourName, name);
        grid.Row((string?)null, FormGrid.Help(Translator.ImapSetup_YourNameDescription));
        var address = form.Text(nameof(vm.EmailAddress), s => s.EmailAddress, (s, v) => s.EmailAddress = v,
            Translator.AccountSetup_EmailPlaceholder, Translator.AccountSetup_EmailHeader);
        address.KeyboardType = UIKeyboardType.EmailAddress;
        address.TextContentType = UITextContentType.EmailAddress;
        grid.Row(Translator.AccountSetup_EmailHeader, address);
        var server = form.Text(nameof(vm.ServerAddress), s => s.ServerAddress, (s, v) => s.ServerAddress = v,
            Translator.ExchangeSetup_ServerPlaceholder, Translator.ExchangeSetup_ServerHeader);
        server.KeyboardType = UIKeyboardType.Url;
        grid.Row(Translator.ExchangeSetup_ServerHeader, server);
        grid.Row(Translator.ExchangeSetup_TransportHeader, form.PopUp(vm.TransportOptions.Select(option => option.Title).ToArray(),
            nameof(vm.SelectedTransport), s => s.SelectedTransport is { } selected ? s.TransportOptions.IndexOf(selected) : 0,
            (s, index) => { if (index >= 0 && index < s.TransportOptions.Count) s.SelectedTransport = s.TransportOptions[index]; },
            Translator.ExchangeSetup_TransportHeader));
        grid.Row((string?)null, FormGrid.Help(Translator.ExchangeSetup_TransportDescription));

        var group = OnboardingPageView.Group(null, grid);
        form.Visible(group, nameof(vm.IsServerStageVisible), s => s.IsServerStageVisible);
        return group;
    }

    private static UIView BuildSignInStage(FormBinder<ExchangeSettingsPageViewModel> form)
    {
        var vm = form.Source;
        var summary = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText);
        summary.LineBreakMode = UILineBreakMode.MiddleTruncation;
        summary.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        form.Label(summary, nameof(vm.ServerSummary), s => s.ServerSummary);
        var change = form.Link(Translator.ImapSetup_ChangeAddress, vm.ChangeServerCommand);
        change.SetContentCompressionResistancePriority(750, UILayoutConstraintAxis.Horizontal);
        change.SetContentHuggingPriority(750, UILayoutConstraintAxis.Horizontal);
        var summaryRow = WinoLayout.HStack(WinoStyle.Space2, summary, change);
        summaryRow.Alignment = UIStackViewAlignment.FirstBaseline;

        var description = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        form.Label(description, nameof(vm.SignInDescription), s => s.SignInDescription);

        var unavailable = new WinoInfoBar(WinoInfoBarSeverity.Informational, null, Translator.ExchangeSetup_ModernAuthUnavailable);
        form.Visible(unavailable, nameof(vm.IsModernAuthUnavailableNoticeVisible), s => s.IsModernAuthUnavailableNoticeVisible);

        var grid = new FormGrid();
        var user = form.Text(nameof(vm.UserName), s => s.UserName, (s, v) => s.UserName = v, vm.UserNamePlaceholder, Translator.ExchangeSetup_UserNameHeader);
        user.TextContentType = UITextContentType.Username;
        form.Bind(nameof(vm.UserNamePlaceholder), s => s.UserNamePlaceholder, text => user.Placeholder = text ?? string.Empty);
        grid.Row(Translator.ExchangeSetup_UserNameHeader, user);
        grid.Row((string?)null, FormGrid.Help(Translator.ExchangeSetup_UserNameDescription));
        grid.Row(Translator.ProviderSelection_PasswordHeader_Account, form.Secure(nameof(vm.Password), s => s.Password, (s, v) => s.Password = v,
            Translator.ProviderSelection_PasswordHeader_Account));
        form.Visible(grid, nameof(vm.IsPasswordStageVisible), s => s.IsPasswordStageVisible);

        // Shown only where an embedded browser exists; the shared ViewModel keeps this stage closed on iOS.
        var organization = form.Command(Translator.ImapSetup_SignInWithOrganization, vm.SignInWithOrganizationCommand, primary: true);
        organization.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        form.Visible(organization, nameof(vm.IsModernAuthStageVisible), s => s.IsModernAuthStageVisible);

        var column = WinoLayout.VStack(WinoStyle.Space3, summaryRow, description, unavailable, grid, organization);
        column.Alignment = UIStackViewAlignment.Fill;
        var group = OnboardingPageView.Group(null, column);
        form.Visible(group, nameof(vm.IsSignInStage), s => s.IsSignInStage);
        return group;
    }

    protected override Task InitializeAsync(Wino.Core.Domain.Models.Navigation.NavigationMode mode, object? parameter)
    {
#if DEBUG
        // exchange-stage server|password|unavailable shows a stage without a server; unavailable is a modern-auth
        // server on a device without the embedded browser.
        UIKitDebugBridge.Register("exchange-stage", async args =>
        {
            var stage = args.Length > 0 ? args[0].ToLowerInvariant() : "password";
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                ViewModel.IsModernAuthUnavailableOnDevice = stage == "unavailable";
                ViewModel.IsSignInStage = stage != "server";
            });
            return "ok " + stage;
        });
#endif
        return base.InitializeAsync(mode, parameter);
    }
}
