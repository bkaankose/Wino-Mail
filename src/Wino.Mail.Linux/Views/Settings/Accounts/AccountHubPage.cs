#if DEBUG
using System.Text;
#endif
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.AccountSettings;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings.Accounts;

/// <summary>
/// The account page (Windows AccountHubPage, macOS AccountHubPageViewController): who the account is with its sync and
/// sign-in status, the identity editor behind Edit identity, the four apps with a switch and a way into each app's page,
/// then sign-in and servers, troubleshooting and removal. The page title follows the account name.
/// </summary>
internal sealed class AccountHubPage(AccountHubPageViewModel viewModel, WinoViewServices services, IPictureStorageService pictures)
    : SettingsPageView<AccountHubPageViewModel>(viewModel, services), ISettingsPageTitleSource
{
    private readonly List<(string Kind, Adw.PreferencesRow Row)> _describedRows = [];
    private WinoColorSwatches? _swatches;
    private WinoSettingsGroup? _identity;

#if DEBUG
    internal static AccountHubPage? DebugCurrent { get; private set; }
#endif

    public string? PageTitle => ViewModel.Account?.Name;

    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;
#if DEBUG
        DebugCurrent = this;
        Settings.Own(new ActionDisposable(() =>
        {
            if (ReferenceEquals(DebugCurrent, this)) DebugCurrent = null;
        }));
#endif
        Settings.Bind(vm, nameof(vm.Account), static s => s.Account, _ => PageTitleChanged?.Invoke(this, EventArgs.Empty));

        var attention = new WinoInfoBar(WinoInfoBarSeverity.Warning, null, Translator.AccountHub_AttentionMessage)
        {
            IsClosable = false,
            ActionTitle = Translator.AccountHub_FixSignIn
        };
        Settings.Own(attention);
        attention.ActionInvoked += _ =>
        {
            if (vm.OpenSignInCommand.CanExecute(null)) vm.OpenSignInCommand.Execute(null);
        };
        Settings.Bind(vm, nameof(vm.NeedsAttention), static s => s.NeedsAttention, open => attention.IsOpen = open);
        var bars = Page.AddGroup();
        bars.HidesWhenEmpty = false;
        bars.Root.Add(attention.Root);
        Settings.Bind(vm, nameof(vm.NeedsAttention), static s => s.NeedsAttention, bars.Root.SetVisible);

        var header = Page.AddGroup();
        header.HidesWhenEmpty = false;
        header.Root.Add(BuildHeader());

        _identity = BuildIdentityEditor();
        BuildApps();
        BuildAccountRows();
    }

    protected override Task NavigateAsync(NavigationMode mode, object? parameter) => ViewModel.InitializeAsync(mode, parameter);

    protected override Task DeactivateAsync()
    {
        _swatches?.Release();
        _swatches = null;
        return base.DeactivateAsync();
    }

    private Gtk.Widget BuildHeader()
    {
        var vm = ViewModel;
        var picture = new WinoAccountIcon(64) { AccessibleName = Translator.AccountDetailsPage_ProfilePictureTitle };
        picture.Root.SetValign(Gtk.Align.Center);
        Settings.Bind(vm, nameof(vm.Account), static s => s.Account,
            account => picture.Account = account is null ? null : MailAccountIconInfoFactory.Create(account, pictures));

        var name = Label("title-2");
        name.SetEllipsize(Pango.EllipsizeMode.End);
        Settings.Bind(vm, nameof(vm.AccountName), static s => s.AccountName, text => name.SetText(text ?? string.Empty));
        var address = Label("dim-label");
        address.SetEllipsize(Pango.EllipsizeMode.End);
        Settings.Bind(vm, nameof(vm.HeaderDescription), static s => s.HeaderDescription, text => address.SetText(text ?? string.Empty));
        var edit = Named(Translator.AccountHub_EditIdentity, vm.ToggleIdentityEditorCommand);
        edit.AddCssClass("flat");
        edit.AddCssClass("link");
        edit.AddCssClass("wino-inline-link");
        edit.SetHalign(Gtk.Align.Start);

        var details = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        details.SetValign(Gtk.Align.Center);
        details.Append(name);
        details.Append(address);
        details.Append(edit);

        var identity = Gtk.Box.New(Gtk.Orientation.Horizontal, 16);
        identity.Append(picture.Root);
        identity.Append(details);

        var sync = StatusBlock(WinoIconGlyph.Sync, nameof(vm.SyncStatusTitle), static s => s.SyncStatusTitle,
            nameof(vm.SyncStatusDetail), static s => s.SyncStatusDetail);
        var signIn = StatusBlock(WinoIconGlyph.LockClosed, nameof(vm.SignInStatusTitle), static s => s.SignInStatusTitle,
            nameof(vm.SignInStatusDetail), static s => s.SignInStatusDetail);
        Settings.Visible(signIn, vm, nameof(vm.IsSignInStatusVisible), static s => s.IsSignInStatusVisible);
        var statuses = Adw.WrapBox.New();
        statuses.SetChildSpacing(24);
        statuses.SetLineSpacing(8);
        statuses.SetValign(Gtk.Align.Center);
        statuses.Append(sync);
        statuses.Append(signIn);

        var header = Adw.WrapBox.New();
        header.SetChildSpacing(24);
        header.SetLineSpacing(12);
        header.SetMarginBottom(6);
        header.Append(identity);
        header.Append(statuses);
        return header;
    }

    private Gtk.Box StatusBlock(WinoIconGlyph glyph, string titleProperty, Func<AccountHubPageViewModel, string?> title,
        string detailProperty, Func<AccountHubPageViewModel, string?> detail)
    {
        var icon = new WinoIcon(glyph, 16).Root;
        icon.AddCssClass("accent");
        icon.SetValign(Gtk.Align.Center);
        var titleLabel = Label("heading");
        Settings.Bind(ViewModel, titleProperty, title, text => titleLabel.SetText(text ?? string.Empty));
        var detailLabel = Label("caption");
        detailLabel.AddCssClass("dim-label");
        Settings.Bind(ViewModel, detailProperty, detail, text => detailLabel.SetText(text ?? string.Empty));
        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 1);
        text.Append(titleLabel);
        text.Append(detailLabel);
        var block = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        block.Append(icon);
        block.Append(text);
        return block;
    }

    /// <summary>Name, picture and colour, opened from the header's Edit identity link.</summary>
    private WinoSettingsGroup BuildIdentityEditor()
    {
        var vm = ViewModel;
        var name = WinoSettingsRows.Action(Translator.AccountHub_NameHeader, Translator.AccountHub_NameDescription, WinoIconGlyph.Person);
        WinoSettingsRows.Suffix(name, Settings.TextSuffix(vm, Translator.AccountHub_NameHeader, Translator.AccountSettingsDialog_AccountNamePlaceholder,
            nameof(vm.AccountName), static s => s.AccountName, static (s, v) => s.AccountName = v));

        var picture = WinoSettingsRows.Action(Translator.AccountDetailsPage_ProfilePictureTitle, null, WinoIconGlyph.Person);
        Settings.Subtitle(picture, vm, nameof(vm.ProfilePictureDescription), static s => s.ProfilePictureDescription);
        var busy = WinoA11y.Label(Adw.Spinner.New(), Translator.AccountDetailsPage_ProfilePictureBusy);
        Settings.Visible(WinoSettingsRows.Suffix(picture, busy), vm, nameof(vm.IsProfilePictureBusy), static s => s.IsProfilePictureBusy);
        WinoSettingsRows.Suffix(picture, Named(Translator.AccountDetailsPage_ProfilePictureChange, vm.ChangeProfilePictureCommand));
        WinoSettingsRows.Suffix(picture, Named(Translator.AccountDetailsPage_ProfilePictureRemove, vm.RemoveProfilePictureCommand));
        Settings.Visible(WinoSettingsRows.Suffix(picture, Named(Translator.AccountDetailsPage_ProfilePictureRefresh, vm.RefreshProfilePictureCommand)),
            vm, nameof(vm.CanRefreshProfilePicture), static s => s.CanRefreshProfilePicture);

        var color = WinoSettingsRows.Action(Translator.AccountDetailsPage_ColorPicker_Title, Translator.AccountDetailsPage_ColorPicker_Description, WinoIconGlyph.Color);
        var swatches = _swatches = new WinoColorSwatches(Translator.AccountDetailsPage_ColorPicker_Title)
        {
            Colors = vm.AvailableColors.Select(static c => c.Hex).ToList()
        };
        Settings.Bind(vm, nameof(vm.SelectedColor), static s => s.SelectedColor, selected => swatches.SelectedHex = selected?.Hex);
        swatches.SelectionChanged += hex =>
        {
            var match = vm.AvailableColors.FirstOrDefault(c => string.Equals(c.Hex, hex, StringComparison.OrdinalIgnoreCase));
            if (match is not null) vm.SelectedColor = match;
        };
        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        swatches.Root.SetHexpand(true);
        content.Append(swatches.Root);
        var reset = Named(Translator.Buttons_Reset, vm.ResetColorCommand);
        reset.SetHalign(Gtk.Align.End);
        content.Append(reset);

        var group = Page.AddGroup(null, null, name, picture, color, WinoSettingsRows.Custom(content, Translator.AccountDetailsPage_ColorPicker_Title));
        Settings.Visible(group.Root, vm, nameof(vm.IsIdentityEditorOpen), static s => s.IsIdentityEditorOpen);
        Describe("name", name);
        Describe("picture", picture);
        Describe("color", color);
        return group;
    }

    private void BuildApps()
    {
        var vm = ViewModel;
        var group = Page.AddGroup(Translator.AccountHub_AppsHeader, Translator.AccountHub_AppsDescription);
        foreach (var item in vm.Apps.ToList())
        {
            var row = WinoSettingsRows.Action(item.Title, item.Summary, item.Icon);
            Settings.Subtitle(row, item, nameof(item.Summary), static s => s.Summary);
            var spinner = WinoA11y.Label(Adw.Spinner.New(), Translator.ExchangeSetup_Checking);
            Settings.Visible(WinoSettingsRows.Suffix(row, spinner), item, nameof(item.IsBusy), static s => s.IsBusy);
            var toggle = Settings.SwitchSuffix(item, item.UseTitle, nameof(item.IsOn), static s => s.IsOn, static (s, v) => s.IsOn = v);
            toggle.SetTooltipText(item.UseTitle);
            Settings.Enabled(toggle, item, nameof(item.CanToggle), static s => s.CanToggle);
            Settings.Bind(item, nameof(item.ToggleHint), static s => s.ToggleHint, hint => WinoA11y.Description(toggle, hint));
            WinoSettingsRows.Suffix(row, toggle);
            var open = WinoSettingsRows.Suffix(row, WinoSettingsRows.ChevronButton(item.OpenAutomationName));
            row.SetActivatableWidget(open);
            var app = item.App;
            Settings.Own(GtkSignals.OnClicked(open, () =>
            {
                if (vm.OpenAppCommand.CanExecute(app)) vm.OpenAppCommand.Execute(app);
            }));
            Settings.Visible(row, item, nameof(item.IsOffered), static s => s.IsOffered);
            WinoA11y.Rename(row, item.OpenAutomationName);
            group.Add(row);
            Describe("app:" + item.App, row);
        }
    }

    private void BuildAccountRows()
    {
        var vm = ViewModel;
        var status = Settings.LabelSuffix(vm, nameof(vm.SignInStatusTitle), static s => s.SignInStatusTitle);
        var signIn = Settings.Command(Translator.AccountHub_SignInAndServersTitle, null, WinoIconGlyph.Key, vm.OpenSignInCommand, detail: status);
        Settings.Subtitle(signIn, vm, nameof(vm.SignInRowDescription), static s => s.SignInRowDescription);
        Settings.Visible(signIn, vm, nameof(vm.IsSignInRowVisible), static s => s.IsSignInRowVisible);

        var troubleshooting = Settings.Command(Translator.AccountHub_TroubleshootingTitle, Translator.AccountHub_TroubleshootingDescription,
            WinoIconGlyph.Wrench, vm.OpenTroubleshootingCommand);
        Settings.Visible(troubleshooting, vm, nameof(vm.IsTroubleshootingVisible), static s => s.IsTroubleshootingVisible);

        var remove = WinoSettingsRows.Action(Translator.AccountHub_RemoveTitle, vm.RemoveDescription, WinoIconGlyph.Delete);
        Settings.Subtitle(remove, vm, nameof(vm.RemoveDescription), static s => s.RemoveDescription);
        WinoSettingsRows.Suffix(remove, Named(Translator.Buttons_Remove, vm.RemoveAccountCommand, destructive: true));

        Page.AddGroup(Translator.AccountHub_AccountHeader, null, signIn, troubleshooting, remove);
        Describe("signin", signIn);
        Describe("troubleshooting", troubleshooting);
        Describe("remove", remove);
    }

    private Gtk.Button Named(string title, System.Windows.Input.ICommand command, bool destructive = false)
        => WinoA11y.Rename(Settings.Button(title, command, destructive: destructive), title);

    private static Gtk.Label Label(string cssClass)
    {
        var label = Gtk.Label.New(string.Empty);
        label.AddCssClass(cssClass);
        label.SetXalign(0);
        return label;
    }

    private void Describe(string kind, Adw.PreferencesRow row) => _describedRows.Add((kind, row));

#if DEBUG
    internal string DebugDescribe()
    {
        var vm = ViewModel;
        var text = new StringBuilder();
        text.AppendLine($"page=AccountHub title={PageTitle} header={vm.HeaderDescription} attention={vm.NeedsAttention} identity={vm.IsIdentityEditorOpen}"
                        + $" identityVisible={_identity?.Root.GetVisible()} sync={vm.SyncStatusTitle}|{vm.SyncStatusDetail} signIn={vm.SignInStatusTitle}|{vm.SignInStatusDetail}"
                        + $" color={vm.SelectedColor?.Hex} name={vm.AccountName}");
        foreach (var (kind, row) in _describedRows)
        {
            var subtitle = row is Adw.ActionRow action ? action.GetSubtitle() : string.Empty;
            text.AppendLine($"row: {kind} | {row.GetTitle()} | {subtitle} | visible={row.GetVisible()} | sensitive={row.GetSensitive()}");
        }
        text.AppendLine("apps=" + string.Join(",", vm.Apps.Select(static a => $"{a.App}:{(a.IsOn ? "on" : "off")}:{(a.IsOffered ? "offered" : "hidden")}:{(a.CanToggle ? "toggle" : "fixed")}")));
        return text.ToString();
    }

    internal AccountHubPageViewModel DebugViewModel => ViewModel;

    internal bool DebugPickColor(int index)
    {
        var vm = ViewModel;
        if (index < 0 || index >= vm.AvailableColors.Count) return false;
        vm.SelectedColor = vm.AvailableColors[index];
        return true;
    }
#endif
}
