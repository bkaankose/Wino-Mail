using CommunityToolkit.Mvvm.Messaging;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Messaging.Client.Navigation;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Onboarding;

/// <summary>
/// Sign-in for a catalog provider such as iCloud or Yahoo (Windows SpecialImapCredentialsPage, Mac twin): the provider's
/// setup hint with its help link, then name, address, region and the app password. Continue stays disabled until the
/// shared ViewModel's validation passes.
/// </summary>
public sealed class SpecialImapCredentialsPageViewController(SpecialImapCredentialsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<SpecialImapCredentialsPageViewModel>(viewModel, dispatcher, logger)
{
    public override void LoadView()
    {
        var vm = ViewModel;
        var form = new FormBinder<SpecialImapCredentialsPageViewModel>(vm, Bindings, Dispatcher, ReportError);

        var icon = new WinoIconView(pointSize: 44);
        WinoLayout.Size(icon, 44, 44);
        form.Bind(nameof(vm.ProviderName), s => s.WizardContext.SelectedProvider, provider =>
            icon.Icon = provider is null ? WinoIconGlyph.IMAP : Views.ProviderSelectionPage.Glyph(provider.Type, provider.SpecialImapProvider));
        var title = new UILabel();
        form.Label(title, nameof(vm.ProviderName), s => s.ProviderName);
        var header = OnboardingPageView.Header(icon, title, new UILabel { Text = Translator.ProviderSelection_SpecialImap_Subtitle });

        // What the provider needs before these credentials work, with the provider's own help page.
        var hint = new WinoInfoBar(WinoInfoBarSeverity.Informational);
        form.Bind(nameof(vm.SetupHint), s => s.SetupHint, text => hint.Message = text);
        form.Bind(nameof(vm.IsSetupHintBlocking), s => s.IsSetupHintBlocking,
            blocking => hint.Severity = blocking ? WinoInfoBarSeverity.Warning : WinoInfoBarSeverity.Informational);
        form.Visible(hint, nameof(vm.IsSetupHintVisible), s => s.IsSetupHintVisible);
        form.Bind(nameof(vm.IsHelpLinkVisible), s => s.IsHelpLinkVisible ? s.HelpLinkText : null, text => hint.ActionTitle = text);
        EventHandler openHelp = (_, _) => _ = RunAsync(() => vm.OpenAppPasswordHelpCommand.ExecuteAsync(null));
        hint.ActionInvoked += openHelp;
        Bindings.Own(new ActionDisposable(() => hint.ActionInvoked -= openHelp));

        var grid = new FormGrid();
        var displayName = form.Text(nameof(vm.DisplayName), s => s.DisplayName, (s, v) => s.DisplayName = v,
            Translator.ProviderSelection_DisplayNamePlaceholder, Translator.ProviderSelection_DisplayNameHeader);
        displayName.AutocapitalizationType = UITextAutocapitalizationType.Words;
        displayName.TextContentType = UITextContentType.Name;
        grid.Row(Translator.ProviderSelection_DisplayNameHeader, displayName);
        var email = form.Text(nameof(vm.EmailAddress), s => s.EmailAddress, (s, v) => s.EmailAddress = v,
            Translator.ProviderSelection_EmailPlaceholder, Translator.ProviderSelection_EmailHeader);
        email.KeyboardType = UIKeyboardType.EmailAddress;
        email.TextContentType = UITextContentType.EmailAddress;
        grid.Row(Translator.ProviderSelection_EmailHeader, email);

        // Region, only for providers whose servers depend on the data centre (Zoho and others).
        var region = new WinoPopUpButton(Translator.ProviderSelection_RegionHeader);
        form.Bind(nameof(vm.Regions), s => s.Regions, regions =>
            region.SetItems((regions ?? []).Select(item => item.DisplayName).ToArray(), Math.Max(0, (regions ?? []).IndexOf(vm.SelectedRegion))));
        form.Bind(nameof(vm.SelectedRegion), s => s.Regions.IndexOf(s.SelectedRegion), index =>
            region.SetItems(vm.Regions.Select(item => item.DisplayName).ToArray(), index));
        region.Chosen = index => { if (index >= 0 && index < vm.Regions.Count) vm.SelectedRegion = vm.Regions[index]; };
        var regionRow = grid.Row(Translator.ProviderSelection_RegionHeader, region);
        var regionHelp = FormGrid.Help();
        form.Label(regionHelp, nameof(vm.RegionDescription), s => s.RegionDescription);
        var regionHelpRow = grid.Row((string?)null, regionHelp);
        form.Visible(regionRow, nameof(vm.IsRegionSelectionVisible), s => s.IsRegionSelectionVisible);
        form.Visible(regionHelpRow, nameof(vm.IsRegionSelectionVisible), s => s.IsRegionSelectionVisible);

        // App password, bridge password or the account password, as the provider expects.
        var passwordLabel = FormGrid.FieldLabel(Translator.ProviderSelection_AppPasswordHeader);
        form.Label(passwordLabel, nameof(vm.PasswordHeader), s => s.PasswordHeader);
        var password = form.Secure(nameof(vm.AppSpecificPassword), s => s.AppSpecificPassword, (s, v) => s.AppSpecificPassword = v);
        form.Bind(nameof(vm.PasswordHeader), s => s.PasswordHeader, text => password.AccessibilityLabel = text);
        var passwordRow = grid.Row(passwordLabel, password);
        form.Visible(passwordRow, nameof(vm.RequiresAppSpecificPassword), s => s.RequiresAppSpecificPassword);

        var content = WinoLayout.VStack(WinoStyle.Space4, hint, OnboardingPageView.Group(null, grid));
        content.Alignment = UIStackViewAlignment.Fill;

        var back = SettingsBinder.CreateButton(Translator.Buttons_Back);
        EventHandler goBack = (_, _) => WeakReferenceMessenger.Default.Send(new BackBreadcrumNavigationRequested(NavigationTransitionEffect.FromLeft));
        back.PrimaryActionTriggered += goBack;
        Bindings.Own(new ActionDisposable(() => back.PrimaryActionTriggered -= goBack));
        var proceedButton = SettingsBinder.CreateButton(Translator.ProviderSelection_ContinueButton, primary: true);
        var proceed = Bindings.Own(new CommandBinding(vm.ProceedCommand, () => null, _ => UpdateContinue(proceedButton), Dispatcher, ReportError));
        form.Bind(nameof(vm.CanProceed), s => s.CanProceed, _ => UpdateContinue(proceedButton));
        EventHandler continued = (_, _) => proceed.Execute();
        proceedButton.PrimaryActionTriggered += continued;
        Bindings.Own(new ActionDisposable(() => proceedButton.PrimaryActionTriggered -= continued));

        View = new OnboardingPageView(header, content, null, [back], proceedButton, 560);
    }

    private void UpdateContinue(UIButton button) => button.Enabled = ViewModel.CanProceed && !ViewModel.ProceedCommand.IsRunning;

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { ReportError(exception); }
    }
}
