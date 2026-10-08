using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Messaging.Client.Navigation;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Onboarding;

/// <summary>
/// Sign-in for a catalog provider such as iCloud or Yahoo (Windows SpecialImapCredentialsPage):
/// the provider's setup hint with its help link, then name, address, region and the app password.
/// Continue stays disabled until the shared ViewModel's validation passes.
/// </summary>
public sealed class SpecialImapCredentialsPageViewController(SpecialImapCredentialsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<SpecialImapCredentialsPageViewModel>(viewModel, dispatcher, logger)
{
    private FormBinder<SpecialImapCredentialsPageViewModel> _form = null!;
    private NSButton _continue = null!;
    private NSTextField _displayName = null!;

    public override void LoadView()
    {
        var vm = ViewModel;
        _form = new FormBinder<SpecialImapCredentialsPageViewModel>(vm, Bindings, Dispatcher, ReportError);

        var icon = new WinoIconView(pointSize: 48);
        WinoLayout.Size(icon, 48, 48);
        _form.Bind(nameof(vm.ProviderName), s => s.WizardContext.SelectedProvider, provider =>
            icon.Icon = provider is null ? WinoIconGlyph.IMAP : ProviderCard.Glyph(provider.Type, provider.SpecialImapProvider));
        var title = NSTextField.CreateLabel(string.Empty);
        _form.Label(title, nameof(vm.ProviderName), s => s.ProviderName);
        var header = OnboardingPageView.Header(icon, title, NSTextField.CreateLabel(Translator.ProviderSelection_SpecialImap_Subtitle));

        // What the provider needs before these credentials work, with the provider's own help page.
        var hint = new WinoInfoBar(WinoInfoBarSeverity.Informational);
        _form.Bind(nameof(vm.SetupHint), s => s.SetupHint, text => hint.Message = text);
        _form.Bind(nameof(vm.IsSetupHintBlocking), s => s.IsSetupHintBlocking,
            blocking => hint.Severity = blocking ? WinoInfoBarSeverity.Warning : WinoInfoBarSeverity.Informational);
        _form.Visible(hint, nameof(vm.IsSetupHintVisible), s => s.IsSetupHintVisible);
        _form.Bind(nameof(vm.IsHelpLinkVisible), s => s.IsHelpLinkVisible ? s.HelpLinkText : null, text => hint.ActionTitle = text);
        EventHandler openHelp = (_, _) => _ = RunAsync(() => vm.OpenAppPasswordHelpCommand.ExecuteAsync(null));
        hint.ActionInvoked += openHelp;
        Bindings.Own(new ActionDisposable(() => hint.ActionInvoked -= openHelp));

        var grid = new FormGrid();
        _displayName = _form.Text(nameof(vm.DisplayName), s => s.DisplayName, (s, v) => s.DisplayName = v,
            Translator.ProviderSelection_DisplayNamePlaceholder, Translator.ProviderSelection_DisplayNameHeader);
        grid.Row(Translator.ProviderSelection_DisplayNameHeader, _displayName);
        var email = _form.Text(nameof(vm.EmailAddress), s => s.EmailAddress, (s, v) => s.EmailAddress = v,
            Translator.ProviderSelection_EmailPlaceholder, Translator.ProviderSelection_EmailHeader);
        email.ContentType = NSTextContentType.EmailAddress;
        grid.Row(Translator.ProviderSelection_EmailHeader, email);

        // Region, only for providers whose servers depend on the data centre (Zoho and others).
        var region = WinoAccessibility.Label(new NSPopUpButton { TranslatesAutoresizingMaskIntoConstraints = false }, Translator.ProviderSelection_RegionHeader);
        _form.KeyViews.Add(region);
        _form.Bind(nameof(vm.Regions), s => s.Regions, regions =>
        {
            region.RemoveAllItems();
            region.AddItems((regions ?? []).Select(item => item.DisplayName).ToArray());
            region.SelectItem(Math.Max(0, (regions ?? []).IndexOf(vm.SelectedRegion)));
        });
        _form.Bind(nameof(vm.SelectedRegion), s => s.Regions.IndexOf(s.SelectedRegion), index => { if (index >= 0) region.SelectItem(index); });
        EventHandler regionChanged = (_, _) =>
        {
            var index = (int)region.IndexOfSelectedItem;
            if (index >= 0 && index < vm.Regions.Count) vm.SelectedRegion = vm.Regions[index];
        };
        region.Activated += regionChanged;
        Bindings.Own(new ActionDisposable(() => region.Activated -= regionChanged));
        var regionRow = grid.Row(Translator.ProviderSelection_RegionHeader, region);
        var regionHelp = FormGrid.Help();
        _form.Label(regionHelp, nameof(vm.RegionDescription), s => s.RegionDescription);
        var regionHelpRow = grid.Row((NSTextField?)null, regionHelp);
        _form.Visible(regionRow, nameof(vm.IsRegionSelectionVisible), s => s.IsRegionSelectionVisible);
        _form.Visible(regionHelpRow, nameof(vm.IsRegionSelectionVisible), s => s.IsRegionSelectionVisible);

        // App password, bridge password or the account password, as the provider expects.
        var passwordLabel = FormGrid.FieldLabel(Translator.ProviderSelection_AppPasswordHeader);
        _form.Label(passwordLabel, nameof(vm.PasswordHeader), s => s.PasswordHeader);
        var password = _form.Secure(nameof(vm.AppSpecificPassword), s => s.AppSpecificPassword, (s, v) => s.AppSpecificPassword = v);
        _form.Bind(nameof(vm.PasswordHeader), s => s.PasswordHeader, text => password.AccessibilityLabel = text);
        var passwordRow = grid.Row(passwordLabel, password);
        _form.Visible(passwordRow, nameof(vm.RequiresAppSpecificPassword), s => s.RequiresAppSpecificPassword);

        var content = WinoLayout.VStack(WinoStyle.Space4, hint, OnboardingPageView.Group(null, grid));
        content.Alignment = NSLayoutAttribute.Leading;
        foreach (var view in content.ArrangedSubviews) view.WidthAnchor.ConstraintEqualTo(content.WidthAnchor).Active = true;

        var back = new NSButton { Title = Translator.Buttons_Back, BezelStyle = NSBezelStyle.Rounded };
        back.Activated += (_, _) => WeakReferenceMessenger.Default.Send(new BackBreadcrumNavigationRequested(NavigationTransitionEffect.FromLeft));
        _continue = new NSButton { Title = Translator.ProviderSelection_ContinueButton, BezelStyle = NSBezelStyle.Rounded };
        var proceed = Bindings.Own(new CommandBinding(vm.ProceedCommand, () => null, _ => UpdateContinue(), Dispatcher, ReportError));
        _form.Bind(nameof(vm.CanProceed), s => s.CanProceed, _ => UpdateContinue());
        EventHandler continued = (_, _) => proceed.Execute();
        _continue.Activated += continued;
        Bindings.Own(new ActionDisposable(() => _continue.Activated -= continued));

        View = new OnboardingPageView(header, content, new NSView { TranslatesAutoresizingMaskIntoConstraints = false }, [back], _continue, 560);
    }

    private void UpdateContinue() => _continue.Enabled = ViewModel.CanProceed && !ViewModel.ProceedCommand.IsRunning;

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { ReportError(exception); }
    }

    public override void ViewDidAppear()
    {
        base.ViewDidAppear();
        OnboardingPageView.ChainKeyViews(_form.KeyViews, _continue);
        View.Window?.MakeFirstResponder(_displayName);
    }
}
