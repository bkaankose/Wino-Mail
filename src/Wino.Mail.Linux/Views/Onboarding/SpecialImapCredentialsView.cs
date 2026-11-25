using System.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Messaging.Client.Navigation;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>
/// Sign-in for a catalog provider such as iCloud or Yahoo (Windows SpecialImapCredentialsPage, macOS controller of the
/// same name): the provider's setup hint with its help link, then name, address, region and the app password.
/// Continue stays insensitive until the shared ViewModel's validation passes.
/// </summary>
internal sealed class SpecialImapCredentialsView : WinoView<SpecialImapCredentialsPageViewModel>
{
    private readonly Adw.ToolbarView _root;
    private readonly Adw.HeaderBar _header;
    private readonly OnboardingForm<SpecialImapCredentialsPageViewModel> _form;
    private readonly Adw.EntryRow _displayName;

    public SpecialImapCredentialsView(SpecialImapCredentialsPageViewModel viewModel, WinoViewServices services) : base(viewModel, services)
    {
        var vm = ViewModel;
        _form = new OnboardingForm<SpecialImapCredentialsPageViewModel>(vm, Bindings, Dispatcher, ReportError);
        if (vm.ProceedCommand is INotifyPropertyChanged proceed) _form.Track(proceed);

        var icon = new WinoIcon(WinoIconGlyph.IMAP, 48, WinoIconMode.Colorful);
        icon.Root.SetHalign(Gtk.Align.Center);
        _form.Apply(() => icon.SetIcon(ViewModel.WizardContext.SelectedProvider is { } provider
            ? MailProviderGlyphs.GetGlyph(provider.Type, provider.SpecialImapProvider) : WinoIconGlyph.IMAP));
        var title = _form.Text(OnboardingUi.Title(), s => s.ProviderName);
        title.AccessibleRole = Gtk.AccessibleRole.Heading;
        var subtitle = OnboardingUi.Subtitle(Translator.ProviderSelection_SpecialImap_Subtitle);

        var hint = new WinoInfoBar(WinoInfoBarSeverity.Informational);
        Bindings.Own(hint);
        hint.ActionInvoked += _ => _form.Run(ViewModel.OpenAppPasswordHelpCommand);
        _form.Apply(() =>
        {
            hint.Message = ViewModel.SetupHint;
            hint.Severity = ViewModel.IsSetupHintBlocking ? WinoInfoBarSeverity.Warning : WinoInfoBarSeverity.Informational;
            hint.ActionTitle = ViewModel.IsHelpLinkVisible ? ViewModel.HelpLinkText : null;
        });
        _form.When(hint.Root, s => s.IsSetupHintVisible);

        var group = OnboardingUi.Group();
        _displayName = _form.Entry(Translator.ProviderSelection_DisplayNameHeader, nameof(vm.DisplayName), s => s.DisplayName, (s, v) => s.DisplayName = v);
        group.Add(_displayName);
        group.Add(_form.Entry(Translator.ProviderSelection_EmailHeader, nameof(vm.EmailAddress), s => s.EmailAddress, (s, v) => s.EmailAddress = v, Gtk.InputPurpose.Email));

        var login = _form.Entry(string.Empty, nameof(vm.LoginAddress), s => s.LoginAddress, (s, v) => s.LoginAddress = v, Gtk.InputPurpose.Email);
        _form.Apply(() =>
        {
            if (login.GetTitle() != ViewModel.LoginAddressHeader) login.SetTitle(ViewModel.LoginAddressHeader);
            WinoA11y.Rename(login, ViewModel.LoginAddressHeader);
            WinoA11y.Description(login, ViewModel.LoginAddressDescription);
        });
        group.Add(_form.When(login, s => s.IsCustomDomain));

        var region = _form.Combo(Translator.ProviderSelection_RegionHeader, s => (s.Regions ?? []).Select(item => item.DisplayName).ToArray(), nameof(vm.SelectedRegion),
            s => s.Regions?.IndexOf(s.SelectedRegion) ?? -1,
            (s, index) => { if (s.Regions is { } regions && index >= 0 && index < regions.Count) s.SelectedRegion = regions[index]; });
        _form.Apply(() =>
        {
            if (region.GetSubtitle() != ViewModel.RegionDescription) region.SetSubtitle(ViewModel.RegionDescription);
        });
        group.Add(_form.When(region, s => s.IsRegionSelectionVisible));

        var password = _form.Password(Translator.ProviderSelection_AppPasswordHeader, nameof(vm.AppSpecificPassword), s => s.AppSpecificPassword, (s, v) => s.AppSpecificPassword = v);
        _form.Apply(() =>
        {
            if (password.GetTitle() != ViewModel.PasswordHeader) password.SetTitle(ViewModel.PasswordHeader);
            WinoA11y.Rename(password, ViewModel.PasswordHeader);
        });
        group.Add(_form.When(password, s => s.RequiresAppSpecificPassword));

        var customDomain = new WinoInfoBar(WinoInfoBarSeverity.Informational);
        Bindings.Own(customDomain);
        _form.Apply(() => customDomain.Message = ViewModel.CustomDomainMessage);
        _form.When(customDomain.Root, s => s.IsCustomDomain);

        var content = OnboardingUi.Column(16, icon.Root, title, subtitle, hint.Root, group, customDomain.Root);
        content.SetMarginTop(24);
        content.SetMarginBottom(24);
        content.SetMarginStart(16);
        content.SetMarginEnd(16);
        var clamp = Adw.Clamp.New();
        clamp.SetMaximumSize(560);
        clamp.SetChild(content);
        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroller.SetVexpand(true);
        scroller.SetChild(clamp);

        var back = OnboardingUi.Button(Translator.Buttons_Back);
        Bindings.Own(GtkSignals.OnClicked(back, () => Messenger.Send(new BackBreadcrumNavigationRequested(NavigationTransitionEffect.FromLeft))));
        var proceedButton = OnboardingUi.Button(Translator.ProviderSelection_ContinueButton, suggested: true);
        Bindings.Own(GtkSignals.OnClicked(proceedButton, () => _form.Run(ViewModel.ProceedCommand)));
        _form.Enabled(proceedButton, s => s.CanProceed && !s.ProceedCommand.IsRunning);
        var spacer = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        spacer.SetHexpand(true);
        var footer = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        footer.AddCssClass("toolbar");
        footer.SetMarginTop(6);
        footer.SetMarginBottom(6);
        footer.SetMarginStart(12);
        footer.SetMarginEnd(12);
        footer.Append(spacer);
        footer.Append(back);
        footer.Append(proceedButton);

        _header = Adw.HeaderBar.New();
        _header.SetShowTitle(false);
        _root = Adw.ToolbarView.New();
        _root.AddTopBar(_header);
        _root.SetContent(scroller);
        _root.AddBottomBar(footer);
    }

    public override Gtk.Widget Root => _root;

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        _header.SetVisible(!OnboardingUi.HasWindowHeader(_root));
        ViewModel.OnNavigatedTo(mode, parameter);
        _form.Refresh();
        MainLoop.Post(() =>
        {
            if (!IsReleased) _displayName.GrabFocus();
        });
        return Task.CompletedTask;
    }
}
