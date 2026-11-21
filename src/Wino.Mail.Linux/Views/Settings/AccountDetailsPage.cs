using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// Account details, a sub-page of Manage accounts (Windows AccountDetailsPage, macOS card order): a view switcher over
/// General, Mail, Calendar, People and To Do; a tab the account has no access to is hidden. The name, sender name and
/// protocol log are saved by Save, as on Windows and macOS; every other row is written at once by the ViewModel.
/// The navigation parameter is the account id or an <see cref="AccountDetailsNavigationContext"/>.
/// </summary>
internal sealed partial class AccountDetailsPage : WinoView<AccountDetailsPageViewModel>, ISettingsPageTitleSource
{
    private static readonly string[] TabNames = ["general", "mail", "calendar", "people", "todo"];

    private readonly IPictureStorageService _pictures;
    private readonly Adw.ToolbarView _root = Adw.ToolbarView.New();
    private readonly Adw.ViewStack _stack = Adw.ViewStack.New();
    private readonly WinoSettingsPage[] _tabs = [new(), new(), new(), new(), new()];
    private readonly Adw.ViewStackPage[] _tabPages = new Adw.ViewStackPage[5];
    private readonly SettingsBinder _settings;
    private readonly List<WinoColorSwatches> _swatches = [];
    private bool _selectingTab;

#if DEBUG
    internal static AccountDetailsPage? DebugCurrent { get; private set; }
#endif

    public AccountDetailsPage(AccountDetailsPageViewModel viewModel, WinoViewServices services, IPictureStorageService pictures)
        : base(viewModel, services)
    {
        _pictures = pictures;
        _settings = new SettingsBinder(Bindings, Dispatcher, ReportError);

        string[] titles = [Translator.AccountDetailsPage_TabGeneral, Translator.AccountDetailsPage_TabMail, Translator.AccountDetailsPage_TabCalendar,
            Translator.AccountDetailsPage_TabPeople, Translator.AccountDetailsPage_TabToDo];
        for (var index = 0; index < _tabs.Length; index++)
            _tabPages[index] = _stack.AddTitled(_tabs[index].Root, TabNames[index], titles[index]);

        // Labels only: the tabs have no GNOME icon, and a Wino glyph cannot be given to a view switcher.
        var switcher = Adw.InlineViewSwitcher.New();
        switcher.SetStack(_stack);
        switcher.SetDisplayMode(Adw.InlineViewSwitcherDisplayMode.Labels);
        switcher.SetHalign(Gtk.Align.Center);
        switcher.SetMarginTop(6);
        switcher.SetMarginBottom(6);
        _root.AddTopBar(switcher);
        _root.SetContent(_stack);
        _root.SetVexpand(true);
        _root.SetHexpand(true);
    }

    public override Gtk.Widget Root => _root;

    public string? PageTitle => ViewModel.Account?.Name;

    public event EventHandler? PageTitleChanged;

    public override bool HasPendingWork => base.HasPendingWork || ViewModel.HasPendingPreferenceWrites;

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        BuildPage();
        return ViewModel.InitializeAsync(mode, parameter);
    }

    protected override async Task DeactivateAsync()
    {
#if DEBUG
        if (ReferenceEquals(DebugCurrent, this)) DebugCurrent = null;
#endif
        foreach (var swatches in _swatches) swatches.Release();
        _swatches.Clear();
        foreach (var swatches in _calendarSwatches) swatches.Release();
        _calendarSwatches.Clear();
        _calendarRows?.Scope.Dispose();
        _folderRows?.Scope.Dispose();
        await base.DeactivateAsync();
        await ViewModel.DrainPreferenceWritesAsync();
    }

    private void BuildPage()
    {
        var vm = ViewModel;
        _settings.Bind(vm, nameof(vm.Account), static s => s.Account, _ =>
        {
            PageTitleChanged?.Invoke(this, EventArgs.Empty);
            _tabPages[1].SetVisible(vm.HasMailAccess);
            _tabPages[2].SetVisible(vm.HasCalendarAccess);
            _tabPages[3].SetVisible(vm.HasContactAccess);
            _tabPages[4].SetVisible(vm.HasTaskAccess);
            SelectTab(vm.SelectedTabIndex);
        });
        _settings.Bind(vm, nameof(vm.SelectedTabIndex), static s => s.SelectedTabIndex, SelectTab);
        _settings.Own(GtkSignals.OnNotify(_stack, "visible-child-name", () =>
        {
            if (_selectingTab) return;
            var index = Array.IndexOf(TabNames, _stack.GetVisibleChildName());
            if (index >= 0 && index != vm.SelectedTabIndex) vm.SelectedTabIndex = index;
        }));

        BuildGeneral(_tabs[0]);
        BuildMail(_tabs[1]);
        BuildCalendar(_tabs[2]);
        BuildPeople(_tabs[3]);
        BuildToDo(_tabs[4]);
#if DEBUG
        DebugCurrent = this;
#endif
    }

    private void SelectTab(int index)
    {
        if (index < 0 || index >= TabNames.Length || !_tabPages[index].GetVisible()) index = 0;
        if (_stack.GetVisibleChildName() == TabNames[index]) return;
        _selectingTab = true;
        try { _stack.SetVisibleChildName(TabNames[index]); }
        finally { _selectingTab = false; }
    }

    private void BuildGeneral(WinoSettingsPage page)
    {
        var vm = ViewModel;

        var picture = WinoSettingsRows.Action(Translator.AccountDetailsPage_ProfilePictureTitle);
        _settings.Subtitle(picture, vm, nameof(vm.ProfilePictureDescription), static s => s.ProfilePictureDescription);
        var icon = new WinoAccountIcon(40);
        picture.AddPrefix(icon.Root);
        _settings.Bind(vm, nameof(vm.Account), static s => s.Account, account =>
        {
            icon.Account = account is null ? null : MailAccountIconInfoFactory.Create(account, _pictures);
            icon.AccessibleName = account?.Name;
        });
        var busy = WinoSettingsRows.Suffix(picture, Adw.Spinner.New());
        _settings.Visible(busy, vm, nameof(vm.IsProfilePictureBusy), static s => s.IsProfilePictureBusy);
        WinoSettingsRows.Suffix(picture, _settings.Visible(Named(Translator.AccountDetailsPage_ProfilePictureRefresh, vm.RefreshProfilePictureCommand),
            vm, nameof(vm.Account), static s => s.CanRefreshProfilePicture));
        WinoSettingsRows.Suffix(picture, _settings.Visible(Named(Translator.AccountDetailsPage_ProfilePictureRemove, vm.RemoveProfilePictureCommand),
            vm, nameof(vm.Account), static s => s.HasProfilePicture));
        WinoSettingsRows.Suffix(picture, Named(Translator.AccountDetailsPage_ProfilePictureChange, vm.ChangeProfilePictureCommand));

        var name = _settings.Text(vm, Translator.AccountEditDialog_Message, nameof(vm.AccountName), static s => s.AccountName, static (s, v) => s.AccountName = v);
        WinoSettingsRows.SetGlyph(name, WinoIconGlyph.Person);

        page.AddGroup(null, null, picture, name);

        BuildCapabilities(page);
        BuildColor(page);
        BuildExchange(page);

        var server = _settings.Command(vm.ServerSettingsTitle, vm.ServerSettingsDescription, WinoIconGlyph.GlobeDesktop, vm.EditImapCalDavSettingsCommand);
        _settings.Bind(vm, nameof(vm.ServerSettingsTitle), static s => s.ServerSettingsTitle, text => server.SetTitle(text ?? string.Empty));
        _settings.Subtitle(server, vm, nameof(vm.ServerSettingsDescription), static s => s.ServerSettingsDescription);
        var protocolLog = _settings.Switch(vm, Translator.ProtocolLog_EnableTitle, Translator.ProtocolLog_EnableDescription, WinoIconGlyph.Pulse,
            nameof(vm.IsProtocolLogEnabled), static s => s.IsProtocolLogEnabled, static (s, v) => s.IsProtocolLogEnabled = v);
        var protocolExport = WinoSettingsRows.Action(Translator.ProtocolLog_ExportTitle, Translator.ProtocolLog_ExportDescription, WinoIconGlyph.ArrowDownload);
        WinoSettingsRows.Suffix(protocolExport, Named(Translator.ProtocolLog_ExportAction, vm.ExportProtocolLogsCommand));
        WinoSettingsRows.Suffix(protocolExport, Named(Translator.ProtocolLog_UploadAction, vm.UploadProtocolLogsCommand));
        var serverGroup = page.AddGroup(null, null, server, protocolLog, protocolExport);
        BindVisible(serverGroup.Root, nameof(vm.IsImapServer), static s => s.IsImapServer);

        var save = WinoSettingsRows.Action(Translator.SettingsEditAccountDetails_Title, null, WinoIconGlyph.Save);
        var saveButton = WinoSettingsRows.Suffix(save, Named(Translator.Buttons_Save, vm.SaveChangesCommand, suggested: true));
        save.SetActivatableWidget(saveButton);
        page.AddGroup(null, null, save);

        var delete = WinoSettingsRows.Action(Translator.SettingsDeleteAccount_Title, vm.DeleteAccountDescription, WinoIconGlyph.Delete);
        _settings.Subtitle(delete, vm, nameof(vm.DeleteAccountDescription), static s => s.DeleteAccountDescription);
        WinoSettingsRows.Suffix(delete, Named(Translator.Buttons_Delete, vm.DeleteAccountCommand, destructive: true));
        page.AddGroup(null, null, delete);
    }

    private void BuildCapabilities(WinoSettingsPage page)
    {
        var vm = ViewModel;
        var apply = WinoSettingsRows.Action(string.Empty);
        var notice = Gtk.Label.New(Translator.AccountDetailsPage_CapabilityReauthenticationMessage);
        notice.AddCssClass("warning");
        notice.SetWrap(true);
        notice.SetXalign(0);
        notice.SetHexpand(true);
        _settings.Visible(notice, vm, nameof(vm.IsCapabilityReauthenticationNoticeVisible), static s => s.IsCapabilityReauthenticationNoticeVisible);
        apply.AddPrefix(notice);
        var spinner = WinoSettingsRows.Suffix(apply, Adw.Spinner.New());
        _settings.Visible(spinner, vm, nameof(vm.IsApplyingCapabilities), static s => s.IsApplyingCapabilities);
        WinoSettingsRows.Suffix(apply, Named(Translator.Buttons_Apply, vm.ApplyCapabilitiesCommand, suggested: true));

        var capabilities = WinoSettingsRows.Expander(Translator.AccountDetailsPage_CapabilityTitle, Translator.AccountDetailsPage_CapabilityDescription, WinoIconGlyph.Calendar, false,
            _settings.Switch(vm, Translator.ProviderSelection_UseForMail, null, WinoIconGlyph.Mail,
                nameof(vm.IsMailCapabilitySelected), static s => s.IsMailCapabilitySelected, static (s, v) => s.IsMailCapabilitySelected = v),
            _settings.Switch(vm, Translator.ProviderSelection_UseForCalendar, null, WinoIconGlyph.Calendar,
                nameof(vm.IsCalendarCapabilitySelected), static s => s.IsCalendarCapabilitySelected, static (s, v) => s.IsCalendarCapabilitySelected = v),
            _settings.Switch(vm, Translator.ProviderSelection_UseForContacts, null, WinoIconGlyph.People,
                nameof(vm.IsContactsCapabilitySelected), static s => s.IsContactsCapabilitySelected, static (s, v) => s.IsContactsCapabilitySelected = v),
            _settings.Switch(vm, Translator.ProviderSelection_UseForTasks, null, WinoIconGlyph.Checkmark,
                nameof(vm.IsTasksCapabilitySelected), static s => s.IsTasksCapabilitySelected, static (s, v) => s.IsTasksCapabilitySelected = v),
            apply);
        var group = page.AddGroup(null, null, capabilities);
        BindVisible(group.Root, nameof(vm.IsCapabilityEditable), static s => s.IsCapabilityEditable);
    }

    private void BuildColor(WinoSettingsPage page)
    {
        var vm = ViewModel;
        var swatches = new WinoColorSwatches(Translator.AccountDetailsPage_ColorPicker_Title);
        _swatches.Add(swatches);
        _settings.Bind(vm, nameof(vm.AvailableColors), static s => s.AvailableColors,
            colors => swatches.Colors = colors?.Select(static color => color.Hex).ToList() ?? []);
        _settings.Bind(vm, nameof(vm.SelectedColor), static s => s.SelectedColor, color => swatches.SelectedHex = color?.Hex);
        swatches.SelectionChanged += hex =>
        {
            var match = vm.AvailableColors?.FirstOrDefault(color => string.Equals(color.Hex, hex, StringComparison.OrdinalIgnoreCase));
            if (match is not null) vm.SelectedColor = match;
        };
        var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        swatches.Root.SetHexpand(true);
        content.Append(swatches.Root);
        content.Append(Named(Translator.Buttons_Reset, vm.ResetColorCommand));
        page.AddGroup(Translator.AccountDetailsPage_ColorPicker_Title, Translator.AccountDetailsPage_ColorPicker_Description,
            WinoSettingsRows.Custom(content, Translator.AccountDetailsPage_ColorPicker_Title));
    }

    private void BuildExchange(WinoSettingsPage page)
    {
        var vm = ViewModel;
        var transport = _settings.Choice<AccountDetailsPageViewModel, ExchangeTransportOption>(vm, Translator.ExchangeSetup_TransportHeader,
            Translator.ExchangeSetup_TransportDescription, WinoIconGlyph.None, static s => s.ExchangeTransportOptions, static option => option.Title,
            nameof(vm.SelectedExchangeTransport), static s => s.SelectedExchangeTransport, static (s, v) => s.SelectedExchangeTransport = v);
        _settings.Enabled(transport, vm, nameof(vm.IsExchangeBusy), static s => !s.IsExchangeBusy);

        var userName = _settings.Text(vm, Translator.ExchangeSetup_UserNameHeader, nameof(vm.ExchangeUserName), static s => s.ExchangeUserName, static (s, v) => s.ExchangeUserName = v);
        var password = WinoSettingsRows.Password(Translator.ProviderSelection_PasswordHeader_Account);
        _settings.Bind(vm, nameof(vm.ExchangePassword), static s => s.ExchangePassword, value =>
        {
            var text = value ?? string.Empty;
            if (password.GetText() != text) password.SetText(text);
        });
        _settings.Own(GtkSignals.OnChanged(password, () => vm.ExchangePassword = password.GetText()));
        var update = WinoSettingsRows.Action(Translator.AccountDetailsPage_ExchangePasswordUpdate, Translator.AccountDetailsPage_ExchangePasswordDescription, WinoIconGlyph.Key);
        WinoSettingsRows.Suffix(update, Named(Translator.AccountDetailsPage_ExchangePasswordUpdate, vm.UpdateExchangePasswordCommand, suggested: true));
        foreach (var row in (Gtk.Widget[])[userName, password, update])
            BindVisible(row, nameof(vm.IsExchangePasswordAuth), static s => s.IsExchangePasswordAuth);

        var signIn = WinoSettingsRows.Action(Translator.AccountDetailsPage_ExchangeSignInTitle, vm.ExchangeSignInDescription, WinoIconGlyph.LockClosed);
        _settings.Subtitle(signIn, vm, nameof(vm.ExchangeSignInDescription), static s => s.ExchangeSignInDescription);
        WinoSettingsRows.Suffix(signIn, Named(Translator.ImapSetup_OAuthSignInAgain, vm.SignInExchangeAgainCommand));
        BindVisible(signIn, nameof(vm.IsExchangeModernAuth), static s => s.IsExchangeModernAuth);

        var checking = WinoSettingsRows.Action(Translator.ExchangeSetup_Checking);
        WinoSettingsRows.Suffix(checking, Adw.Spinner.New());
        _settings.Visible(checking, vm, nameof(vm.IsExchangeBusy), static s => s.IsExchangeBusy);

        var expander = WinoSettingsRows.Expander(Translator.AccountDetailsPage_ExchangeTitle, vm.ExchangeServerDescription, WinoIconGlyph.GlobeDesktop, false,
            transport, userName, password, update, signIn, checking);
        _settings.Subtitle(expander, vm, nameof(vm.ExchangeServerDescription), static s => s.ExchangeServerDescription);
        _settings.Bind(vm, nameof(vm.IsExchangeSettingsExpanded), static s => s.IsExchangeSettingsExpanded, expanded =>
        {
            if (expander.GetExpanded() != expanded) expander.SetExpanded(expanded);
        });
        _settings.Own(GtkSignals.OnNotify(expander, "expanded", () =>
        {
            if (vm.IsExchangeSettingsExpanded != expander.GetExpanded()) vm.IsExchangeSettingsExpanded = expander.GetExpanded();
        }));
        var group = page.AddGroup(null, null, expander);
        BindVisible(group.Root, nameof(vm.IsExchangeAccount), static s => s.IsExchangeAccount);
    }

    private Gtk.Button Named(string title, System.Windows.Input.ICommand command, Func<object?>? parameter = null, bool suggested = false, bool destructive = false)
        => WinoA11y.Rename(_settings.Button(title, command, parameter, suggested, destructive), title);

    /// <summary>Visibility of a value the ViewModel raises with <c>Account</c> as well as under its own name.</summary>
    private void BindVisible(Gtk.Widget widget, string property, Func<AccountDetailsPageViewModel, bool> read)
    {
        _settings.Visible(widget, ViewModel, property, read);
        _settings.Visible(widget, ViewModel, nameof(ViewModel.Account), read);
    }
}
