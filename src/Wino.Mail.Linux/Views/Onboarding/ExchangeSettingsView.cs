using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>
/// Advanced setup for on-premises Exchange (Windows ExchangeSettingsPage, macOS controller of the same name): the server
/// first (address, server or Autodiscover, connection method), then the sign-in the server asks for, a password or the
/// organisation sign-in through the embedded browser. The page re-reads the ViewModel once per main-loop turn.
/// </summary>
internal sealed class ExchangeSettingsView : WinoView<ExchangeSettingsPageViewModel>
{
    private readonly Adw.ToolbarView _root;
    private readonly Adw.HeaderBar _header;
    private readonly OnboardingForm<ExchangeSettingsPageViewModel> _form;
    private readonly Dictionary<string, Gtk.Editable> _fields = [];

    public ExchangeSettingsView(ExchangeSettingsPageViewModel viewModel, WinoViewServices services) : base(viewModel, services)
    {
        var vm = ViewModel;
        _form = new OnboardingForm<ExchangeSettingsPageViewModel>(vm, Bindings, Dispatcher, ReportError);

        var icon = new WinoIcon(WinoIconGlyph.Exchange, 48, WinoIconMode.Colorful);
        icon.Root.SetHalign(Gtk.Align.Center);
        var title = OnboardingUi.Title(Translator.ExchangeSetup_Title);
        title.AccessibleRole = Gtk.AccessibleRole.Heading;
        var description = OnboardingUi.Subtitle(Translator.ExchangeSetup_Description);

        var failure = new WinoInfoBar(WinoInfoBarSeverity.Error, Translator.AccountSetup_Failure_Title) { IsClosable = true };
        Bindings.Own(failure);
        failure.Closed += (_, _) => _form.Run(ViewModel.HideFailureCommand);
        _form.Apply(() =>
        {
            failure.Message = ViewModel.FailureMessage;
            if (failure.IsOpen != ViewModel.IsFailureOpen) failure.IsOpen = ViewModel.IsFailureOpen;
        });

        var content = OnboardingUi.Column(16, icon.Root, title, description, failure.Root, BuildServerStage(), BuildSignInStage());
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

        _header = Adw.HeaderBar.New();
        _header.SetShowTitle(false);
        _root = Adw.ToolbarView.New();
        _root.AddTopBar(_header);
        _root.SetContent(scroller);
        _root.AddBottomBar(BuildFooter());
#if DEBUG
        _debugCurrent = new WeakReference<ExchangeSettingsView>(this);
#endif
    }

    public override Gtk.Widget Root => _root;

    public override bool HasPendingWork => base.HasPendingWork || ViewModel.IsBusy;

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        _header.SetVisible(!OnboardingUi.HasWindowHeader(_root));
        ViewModel.OnNavigatedTo(mode, parameter);
        _form.Refresh();
        return Task.CompletedTask;
    }

    private T Field<T>(string key, T editable) where T : Gtk.Editable
    {
        _fields[key] = editable;
        return editable;
    }

    private Gtk.Widget BuildServerStage()
    {
        var vm = ViewModel;
        var group = OnboardingUi.Group();
        var name = Field("name", _form.Entry(Translator.ImapSetup_YourName, nameof(vm.DisplayName), s => s.DisplayName, (s, v) => s.DisplayName = v));
        WinoA11y.Description(name, Translator.ImapSetup_YourNameDescription);
        group.Add(name);
        group.Add(Field("address", _form.Entry(Translator.AccountSetup_EmailHeader, nameof(vm.EmailAddress), s => s.EmailAddress, (s, v) => s.EmailAddress = v, Gtk.InputPurpose.Email)));
        group.Add(Field("server", _form.Entry(Translator.ExchangeSetup_ServerHeader, nameof(vm.ServerAddress), s => s.ServerAddress, (s, v) => s.ServerAddress = v, Gtk.InputPurpose.Url)));
        var transport = _form.Combo(Translator.ExchangeSetup_TransportHeader, s => s.TransportOptions.Select(option => option.Title).ToArray(), nameof(vm.SelectedTransport),
            s => s.SelectedTransport is { } selected ? s.TransportOptions.IndexOf(selected) : 0,
            (s, index) => { if (index >= 0 && index < s.TransportOptions.Count) s.SelectedTransport = s.TransportOptions[index]; });
        transport.SetSubtitle(Translator.ExchangeSetup_TransportDescription);
        group.Add(transport);
        foreach (var key in new[] { "name", "address", "server" })
            if (_fields[key] is Adw.EntryRow row) _form.OnEnter(row, vm.PrimaryCommand);
        return _form.When(group, s => s.IsServerStageVisible);
    }

    private Gtk.Widget BuildSignInStage()
    {
        var vm = ViewModel;
        var summary = _form.Text(OnboardingUi.Body(), s => s.ServerSummary);
        summary.AddCssClass("dim-label");
        summary.SetEllipsize(Pango.EllipsizeMode.Middle);
        summary.SetWrap(false);
        summary.SetHexpand(true);
        var summaryRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        summaryRow.Append(summary);
        summaryRow.Append(_form.Link(Translator.ImapSetup_ChangeAddress, vm.ChangeServerCommand));
        var description = _form.Optional(OnboardingUi.Caption(), s => s.SignInDescription);

        var group = OnboardingUi.Group(null, Translator.ExchangeSetup_UserNameDescription);
        var user = Field("user", _form.Entry(Translator.ExchangeSetup_UserNameHeader, nameof(vm.UserName), s => s.UserName, (s, v) => s.UserName = v));
        group.Add(user);
        var password = Field("password", _form.Password(Translator.ProviderSelection_PasswordHeader_Account, nameof(vm.Password), s => s.Password, (s, v) => s.Password = v));
        group.Add(password);
        _form.OnEnter(user, vm.PrimaryCommand);
        _form.OnEnter(password, vm.PrimaryCommand);
        _form.When(group, s => s.IsPasswordStageVisible);

        var unavailable = new WinoInfoBar(WinoInfoBarSeverity.Informational, null, Translator.ExchangeSetup_ModernAuthUnavailable);
        Bindings.Own(unavailable);
        _form.When(unavailable.Root, s => s.IsModernAuthUnavailableNoticeVisible);

        var organization = Gtk.Button.New();
        organization.SetChild(WinoIcons.ButtonContent(WinoIconGlyph.Exchange, Translator.ImapSetup_SignInWithOrganization));
        organization.AddCssClass("suggested-action");
        organization.AddCssClass("pill");
        WinoA11y.Rename(organization, Translator.ImapSetup_SignInWithOrganization);
        _form.Command(organization, vm.SignInWithOrganizationCommand);
        var organizationRow = _form.When(OnboardingUi.Centered(organization), s => s.IsModernAuthStageVisible);

        return _form.When(OnboardingUi.Column(12, summaryRow, description, group, unavailable.Root, organizationRow), s => s.IsSignInStage);
    }

    private Gtk.Widget BuildFooter()
    {
        var vm = ViewModel;
        var spinner = OnboardingUi.Spinner();
        var busyText = _form.Text(OnboardingUi.Body(), s => s.BusyText);
        busyText.AddCssClass("dim-label");
        busyText.SetWrap(false);
        busyText.SetEllipsize(Pango.EllipsizeMode.End);
        var busy = _form.When(Gtk.Box.New(Gtk.Orientation.Horizontal, 8), s => s.IsBusy);
        busy.Append(spinner);
        busy.Append(busyText);
        busy.SetHexpand(true);
        var spacer = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        spacer.SetHexpand(true);
        var back = _form.Button(Translator.Buttons_Back, vm.BackCommand);
        var primary = _form.Button(Translator.ProviderSelection_ContinueButton, vm.PrimaryCommand, suggested: true);
        primary.SetSizeRequest(110, -1);
        _form.When(primary, s => s.IsPrimaryVisible);
        _form.Apply(() =>
        {
            if (primary.GetLabel() == ViewModel.PrimaryText) return;
            primary.SetLabel(ViewModel.PrimaryText);
            WinoA11y.Rename(primary, ViewModel.PrimaryText);
        });
        var footer = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        footer.AddCssClass("toolbar");
        footer.SetMarginTop(6);
        footer.SetMarginBottom(6);
        footer.SetMarginStart(12);
        footer.SetMarginEnd(12);
        footer.Append(busy);
        footer.Append(spacer);
        footer.Append(back);
        footer.Append(primary);
        return footer;
    }

#if DEBUG
    private static WeakReference<ExchangeSettingsView>? _debugCurrent;

    public static ExchangeSettingsView? DebugCurrent => _debugCurrent is not null && _debugCurrent.TryGetTarget(out var view) && !view.IsReleased ? view : null;

    public ExchangeSettingsPageViewModel DebugViewModel => ViewModel;

    public bool DebugType(string field, string text)
    {
        if (!_fields.TryGetValue(field, out var editable)) return false;
        editable.SetText(text);
        return true;
    }

    public string DebugDescribe()
    {
        var vm = ViewModel;
        return $"server={vm.IsServerStageVisible} signIn={vm.IsSignInStage} password={vm.IsPasswordStageVisible} modern={vm.IsModernAuthStageVisible} " +
               $"unavailable={vm.IsModernAuthUnavailableNoticeVisible} busy={vm.IsBusy} primary={(vm.IsPrimaryVisible ? vm.PrimaryText : "hidden")} " +
               $"failure={(vm.IsFailureOpen ? vm.FailureMessage : "none")} transport={vm.SelectedTransport?.Title} summary='{vm.ServerSummary}'\n";
    }
#endif
}
