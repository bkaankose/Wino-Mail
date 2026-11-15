using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;

namespace Wino.Mail.MacOS.Views.Onboarding;

/// <summary>
/// Advanced setup for on-premises Exchange (Windows ExchangeSettingsPage): the server first (address, server or
/// Autodiscover, connection method), then the sign-in the server asks for, a password or the organization sign-in.
/// </summary>
public sealed class ExchangeSettingsPageViewController(ExchangeSettingsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<ExchangeSettingsPageViewModel>(viewModel, dispatcher, logger)
{
    private FormBinder<ExchangeSettingsPageViewModel> _form = null!;
    private NSButton _primary = null!;

    public override void LoadView()
    {
        var vm = ViewModel;
        _form = new FormBinder<ExchangeSettingsPageViewModel>(vm, Bindings, Dispatcher, ReportError);

        var icon = new WinoIconView(WinoIconGlyph.Exchange, 48);
        WinoLayout.Size(icon, 48, 48);
        var header = OnboardingPageView.Header(icon, NSTextField.CreateLabel(Translator.ExchangeSetup_Title),
            NSTextField.CreateLabel(Translator.ExchangeSetup_Description));

        var failure = new WinoInfoBar(WinoInfoBarSeverity.Error, Translator.AccountSetup_Failure_Title) { IsClosable = true };
        _form.Bind(nameof(vm.FailureMessage), s => s.FailureMessage, text => failure.Message = text);
        _form.Visible(failure, nameof(vm.IsFailureOpen), s => s.IsFailureOpen);
        EventHandler<WinoInfoBarClosedEventArgs> closeFailure = (_, _) => vm.HideFailureCommand.Execute(null);
        failure.Closed += closeFailure;
        Bindings.Own(new ActionDisposable(() => failure.Closed -= closeFailure));

        var content = WinoLayout.VStack(WinoStyle.Space4, failure, BuildServerStage(), BuildSignInStage());
        content.Alignment = NSLayoutAttribute.Leading;
        foreach (var view in content.ArrangedSubviews) view.WidthAnchor.ConstraintEqualTo(content.WidthAnchor).Active = true;

        var spinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, ControlSize = NSControlSize.Small, Indeterminate = true, TranslatesAutoresizingMaskIntoConstraints = false };
        var busyText = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText);
        busyText.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var busy = WinoLayout.HStack(WinoStyle.Space2, spinner, busyText);
        _form.Label(busyText, nameof(vm.BusyText), s => s.BusyText);
        _form.Bind(nameof(vm.IsBusy), s => s.IsBusy, isBusy =>
        {
            busy.Hidden = !isBusy;
            if (isBusy) spinner.StartAnimation(null); else spinner.StopAnimation(null);
        });

        var back = _form.Command(Translator.Buttons_Back, vm.BackCommand);
        _primary = _form.Command(Translator.ProviderSelection_ContinueButton, vm.PrimaryCommand);
        _primary.WidthAnchor.ConstraintGreaterThanOrEqualTo(110).Active = true;
        _form.Bind(nameof(vm.PrimaryText), s => s.PrimaryText, text => _primary.Title = text);
        _form.Visible(_primary, nameof(vm.IsPrimaryVisible), s => s.IsPrimaryVisible);

        View = new OnboardingPageView(header, content, busy, [back], _primary, 560);
    }

    private NSView BuildServerStage()
    {
        var vm = ViewModel;
        var grid = new FormGrid();
        grid.Row(Translator.ImapSetup_YourName, _form.Text(nameof(vm.DisplayName), s => s.DisplayName, (s, v) => s.DisplayName = v,
            Translator.IMAPSetupDialog_DisplayNamePlaceholder, Translator.ImapSetup_YourName));
        grid.Row((NSTextField?)null, FormGrid.Help(Translator.ImapSetup_YourNameDescription));
        var address = _form.Text(nameof(vm.EmailAddress), s => s.EmailAddress, (s, v) => s.EmailAddress = v,
            Translator.AccountSetup_EmailPlaceholder, Translator.AccountSetup_EmailHeader);
        address.ContentType = NSTextContentType.EmailAddress;
        grid.Row(Translator.AccountSetup_EmailHeader, address);
        grid.Row(Translator.ExchangeSetup_ServerHeader, _form.Text(nameof(vm.ServerAddress), s => s.ServerAddress, (s, v) => s.ServerAddress = v,
            Translator.ExchangeSetup_ServerPlaceholder, Translator.ExchangeSetup_ServerHeader));
        grid.Row(Translator.ExchangeSetup_TransportHeader, _form.PopUp(vm.TransportOptions.Select(option => option.Title),
            nameof(vm.SelectedTransport), s => s.SelectedTransport is { } selected ? s.TransportOptions.IndexOf(selected) : 0,
            (s, index) => { if (index >= 0 && index < s.TransportOptions.Count) s.SelectedTransport = s.TransportOptions[index]; },
            Translator.ExchangeSetup_TransportHeader));
        grid.Row((NSTextField?)null, FormGrid.Help(Translator.ExchangeSetup_TransportDescription));

        var group = OnboardingPageView.Group(null, grid);
        _form.Visible(group, nameof(vm.IsServerStageVisible), s => s.IsServerStageVisible);
        return group;
    }

    private NSView BuildSignInStage()
    {
        var vm = ViewModel;
        var summary = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText);
        summary.LineBreakMode = NSLineBreakMode.TruncatingMiddle;
        summary.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        _form.Label(summary, nameof(vm.ServerSummary), s => s.ServerSummary);
        var summaryRow = WinoLayout.HStack(WinoStyle.Space2, summary, _form.Link(Translator.ImapSetup_ChangeAddress, vm.ChangeServerCommand), WinoLayout.Spacer());
        summaryRow.Alignment = NSLayoutAttribute.FirstBaseline;

        var description = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        _form.Label(description, nameof(vm.SignInDescription), s => s.SignInDescription);

        var grid = new FormGrid();
        var user = _form.Text(nameof(vm.UserName), s => s.UserName, (s, v) => s.UserName = v, vm.UserNamePlaceholder, Translator.ExchangeSetup_UserNameHeader);
        _form.Bind(nameof(vm.UserNamePlaceholder), s => s.UserNamePlaceholder, text => user.PlaceholderString = text ?? string.Empty);
        grid.Row(Translator.ExchangeSetup_UserNameHeader, user);
        grid.Row((NSTextField?)null, FormGrid.Help(Translator.ExchangeSetup_UserNameDescription));
        grid.Row(Translator.ProviderSelection_PasswordHeader_Account, _form.Secure(nameof(vm.Password), s => s.Password, (s, v) => s.Password = v,
            Translator.ProviderSelection_PasswordHeader_Account));
        _form.Visible(grid, nameof(vm.IsPasswordStageVisible), s => s.IsPasswordStageVisible);

        var organization = _form.Command(Translator.ImapSetup_SignInWithOrganization, vm.SignInWithOrganizationCommand);
        organization.ControlSize = NSControlSize.Large;
        organization.Image = WinoIcons.Image(WinoIconGlyph.Exchange, 16);
        organization.ImagePosition = NSCellImagePosition.ImageLeading;
        var organizationRow = WinoLayout.HStack(0, WinoLayout.Spacer(), organization, WinoLayout.Spacer());
        _form.Visible(organizationRow, nameof(vm.IsModernAuthStageVisible), s => s.IsModernAuthStageVisible);

        var column = WinoLayout.VStack(WinoStyle.Space3, summaryRow, description, grid, organizationRow);
        column.Alignment = NSLayoutAttribute.Leading;
        foreach (var view in column.ArrangedSubviews) view.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
        var group = OnboardingPageView.Group(null, column);
        _form.Visible(group, nameof(vm.IsSignInStage), s => s.IsSignInStage);
        return group;
    }

    public override void ViewDidAppear()
    {
        base.ViewDidAppear();
        OnboardingPageView.ChainKeyViews(_form.KeyViews, _primary);
    }
}
