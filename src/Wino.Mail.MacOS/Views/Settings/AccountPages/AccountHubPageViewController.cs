using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels.AccountSettings;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;

namespace Wino.Mail.MacOS.Views.Settings.AccountPages;

/// <summary>
/// The account page (Windows AccountHubPage): who the account is, the four apps it can provide with a switch and a way
/// into each, then sign-in and servers, troubleshooting and removal. The window title follows the account name.
/// </summary>
public sealed class AccountHubPageViewController(AccountHubPageViewModel viewModel, IPictureStorageService pictures, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<AccountHubPageViewModel>(viewModel, dispatcher, logger), ISettingsPageTitleSource
{
    private readonly WinoAccountIconView _picture = new(64);
    private readonly WinoSettingsCard _identityName = new(Translator.AccountHub_NameHeader, Translator.AccountHub_NameDescription, WinoIconGlyph.Person);
    private readonly WinoSettingsCard _identityPicture = new(Translator.AccountDetailsPage_ProfilePictureTitle, null, WinoIconGlyph.Person);
    private readonly WinoSettingsCard _identityColor = new(Translator.AccountDetailsPage_ColorPicker_Title, Translator.AccountDetailsPage_ColorPicker_Description, WinoIconGlyph.Color);

    public string? PageTitle => ViewModel.Account?.Name;

    public event EventHandler? PageTitleChanged;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Bind.Bind(vm, nameof(vm.Account), s => s.Account, account =>
        {
            _picture.Account = account is null ? null : MailAccountIconInfoFactory.Create(account, pictures);
            PageTitleChanged?.Invoke(this, EventArgs.Empty);
        });

        var attention = InfoBar(WinoInfoBarSeverity.Warning, null, Translator.AccountHub_AttentionMessage);
        attention.IsClosable = false;
        attention.ActionTitle = Translator.AccountHub_FixSignIn;
        attention.ActionInvoked += (_, _) => { if (vm.OpenSignInCommand.CanExecute(null)) vm.OpenSignInCommand.Execute(null); };
        Bind.Visible(attention, vm, nameof(vm.NeedsAttention), s => s.NeedsAttention);
        Add(attention);

        Add(BuildHeader());
        Add(BuildIdentityEditor());
        AddGroup(Translator.AccountHub_AppsHeader, BuildAppRows());
        AddGroup(Translator.AccountHub_AccountHeader, BuildAccountRows());
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
#if DEBUG
        // hub-app Mail|Calendar|Contacts|Tasks on|off: flips the app switch on the hub row, as a click does.
        MacDebugBridge.Register("hub-app", async args =>
        {
            if (args.Length < 2 || !Enum.TryParse<WinoApplicationMode>(args[0], true, out var app)) return "usage: hub-app Mail|Calendar|Contacts|Tasks on|off";
            var item = ViewModel.Apps.FirstOrDefault(candidate => candidate.App == app);
            if (item is null) return "no app row " + args[0];
            var isOn = args[1].Equals("on", StringComparison.OrdinalIgnoreCase);
            await Dispatcher.ExecuteOnUIThread(() => item.IsOn = isOn);
            return $"{app}={(isOn ? "on" : "off")}";
        });
        // hub-signin: the Sign-in and servers row of the hub.
        MacDebugBridge.Register("hub-signin", async _ =>
        {
            var ran = false;
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                ran = ViewModel.OpenSignInCommand.CanExecute(null);
                if (ran) ViewModel.OpenSignInCommand.Execute(null);
            });
            return ran ? "opened sign-in and servers" : "disabled";
        });
#endif
        return ViewModel.InitializeAsync(mode, parameter!);
    }

    private NSView BuildHeader()
    {
        var vm = ViewModel;
        var name = Bind.Label(vm, nameof(vm.AccountName), s => s.AccountName, WinoStyle.Heading, WinoStyle.PrimaryText);
        name.LineBreakMode = NSLineBreakMode.TruncatingTail;
        var address = Bind.Label(vm, nameof(vm.HeaderDescription), s => s.HeaderDescription, WinoStyle.Description, WinoStyle.SecondaryText);
        address.LineBreakMode = NSLineBreakMode.TruncatingTail;
        var edit = Bind.Button(Translator.AccountHub_EditIdentity, vm.ToggleIdentityEditorCommand);
        edit.BezelStyle = NSBezelStyle.Inline;

        var details = WinoLayout.VStack(2, name, address, edit);
        details.Alignment = NSLayoutAttribute.Leading;

        var sync = StatusBlock(WinoIconGlyph.Sync,
            Bind.Label(vm, nameof(vm.SyncStatusTitle), s => s.SyncStatusTitle, WinoStyle.BodyStrong, WinoStyle.PrimaryText),
            Bind.Label(vm, nameof(vm.SyncStatusDetail), s => s.SyncStatusDetail, WinoStyle.Caption, WinoStyle.SecondaryText));
        var signIn = StatusBlock(WinoIconGlyph.LockClosed,
            Bind.Label(vm, nameof(vm.SignInStatusTitle), s => s.SignInStatusTitle, WinoStyle.BodyStrong, WinoStyle.PrimaryText),
            Bind.Label(vm, nameof(vm.SignInStatusDetail), s => s.SignInStatusDetail, WinoStyle.Caption, WinoStyle.SecondaryText));
        Bind.Visible(signIn, vm, nameof(vm.IsSignInStatusVisible), s => s.IsSignInStatusVisible);
        var statuses = WinoLayout.HStack(24, sync, signIn);
        statuses.Alignment = NSLayoutAttribute.CenterY;

        var header = WinoLayout.HStack(16, _picture, details, WinoLayout.Spacer(), statuses);
        header.Alignment = NSLayoutAttribute.CenterY;
        return header;
    }

    private static NSStackView StatusBlock(WinoIconGlyph glyph, NSTextField title, NSTextField detail)
    {
        var icon = new WinoIconView(glyph, 16, WinoStyle.Accent);
        WinoLayout.Size(icon, 16, 16);
        var text = WinoLayout.VStack(1, title, detail);
        text.Alignment = NSLayoutAttribute.Leading;
        var block = WinoLayout.HStack(8, icon, text);
        block.Alignment = NSLayoutAttribute.CenterY;
        return block;
    }

    /// <summary>Name, picture and colour, opened from the header's Edit identity link.</summary>
    private WinoSettingsGroup BuildIdentityEditor()
    {
        var vm = ViewModel;
        _identityName.Content = Bind.TextField(vm, nameof(vm.AccountName), s => s.AccountName, (s, v) => s.AccountName = v, Translator.AccountSettingsDialog_AccountNamePlaceholder);

        var spinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, ControlSize = NSControlSize.Small, Indeterminate = true, TranslatesAutoresizingMaskIntoConstraints = false };
        Bind.Bind(vm, nameof(vm.IsProfilePictureBusy), s => s.IsProfilePictureBusy, busy =>
        {
            spinner.Hidden = !busy;
            if (busy) spinner.StartAnimation(null); else spinner.StopAnimation(null);
        });
        var refresh = Bind.Button(Translator.AccountDetailsPage_ProfilePictureRefresh, vm.RefreshProfilePictureCommand);
        Bind.Visible(refresh, vm, nameof(vm.CanRefreshProfilePicture), s => s.CanRefreshProfilePicture);
        _identityPicture.Content = Row(
            spinner,
            Bind.Button(Translator.AccountDetailsPage_ProfilePictureChange, vm.ChangeProfilePictureCommand),
            Bind.Button(Translator.AccountDetailsPage_ProfilePictureRemove, vm.RemoveProfilePictureCommand),
            refresh);
        Bind.Bind(vm, nameof(vm.ProfilePictureDescription), s => s.ProfilePictureDescription, text => _identityPicture.Description = text);

        var swatches = new WinoColorSwatchPicker();
        swatches.WidthAnchor.ConstraintEqualTo(260).Active = true;
        Bind.Bind(vm, nameof(vm.AvailableColors), s => s.AvailableColors, colors => swatches.Colors = colors?.Select(color => color.Hex).ToList() ?? []);
        Bind.Bind(vm, nameof(vm.SelectedColor), s => s.SelectedColor, color => swatches.SelectedHex = color?.Hex);
        EventHandler<string> picked = (_, hex) =>
        {
            var match = vm.AvailableColors.FirstOrDefault(color => string.Equals(color.Hex, hex, StringComparison.OrdinalIgnoreCase));
            if (match is not null) vm.SelectedColor = match;
        };
        swatches.SelectionChanged += picked;
        Bindings.Own(new ActionDisposable(() => swatches.SelectionChanged -= picked));
        _identityColor.Content = Row(swatches, Bind.Button(Translator.Buttons_Reset, vm.ResetColorCommand));

        var editor = Group(null, _identityName, _identityPicture, _identityColor);
        Bind.Visible(editor, vm, nameof(vm.IsIdentityEditorOpen), s => s.IsIdentityEditorOpen);
        return editor;
    }

    private NSView[] BuildAppRows()
    {
        var rows = new List<NSView> { Caption(Translator.AccountHub_AppsDescription) };
        foreach (var item in ViewModel.Apps)
            rows.Add(AppRow(item));
        return rows.ToArray();
    }

    private WinoSettingsCard AppRow(AccountAppItemViewModel item)
    {
        var spinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, ControlSize = NSControlSize.Small, Indeterminate = true, TranslatesAutoresizingMaskIntoConstraints = false };
        Bind.Bind(item, nameof(item.IsBusy), s => s.IsBusy, busy =>
        {
            spinner.Hidden = !busy;
            if (busy) spinner.StartAnimation(null); else spinner.StopAnimation(null);
        });
        var toggle = Bind.Switch(item, nameof(item.IsOn), s => s.IsOn, (s, v) => s.IsOn = v, item.UseTitle);
        Bind.Enabled(toggle, item, nameof(item.CanToggle), s => s.CanToggle);
        WinoAccessibility.Help(toggle, item.ToggleHint);

        var card = CommandCard(item.Title, item.Summary, item.Icon, ViewModel.OpenAppCommand, () => item.App);
        card.Content = Row(spinner, toggle);
        Bind.Bind(item, nameof(item.Summary), s => s.Summary, text => card.Description = text);
        Bind.Visible(card, item, nameof(item.IsOffered), s => s.IsOffered);
        WinoAccessibility.Label(card, item.OpenAutomationName);
        return card;
    }

    private NSView[] BuildAccountRows()
    {
        var vm = ViewModel;

        var signIn = CommandCard(Translator.AccountHub_SignInAndServersTitle, null, WinoIconGlyph.Key, vm.OpenSignInCommand);
        signIn.Content = Bind.Label(vm, nameof(vm.SignInStatusTitle), s => s.SignInStatusTitle, WinoStyle.Description, WinoStyle.SecondaryText);
        Bind.Bind(vm, nameof(vm.SignInRowDescription), s => s.SignInRowDescription, text => signIn.Description = text);
        Bind.Visible(signIn, vm, nameof(vm.IsSignInRowVisible), s => s.IsSignInRowVisible);

        var troubleshooting = CommandCard(Translator.AccountHub_TroubleshootingTitle, Translator.AccountHub_TroubleshootingDescription, WinoIconGlyph.Wrench,
            vm.OpenTroubleshootingCommand);
        Bind.Visible(troubleshooting, vm, nameof(vm.IsTroubleshootingVisible), s => s.IsTroubleshootingVisible);

        var remove = Bind.Button(Translator.Buttons_Remove, vm.RemoveAccountCommand);
        remove.BezelColor = NSColor.SystemRed;
        remove.ContentTintColor = NSColor.White;
        var removeCard = Card(Translator.AccountHub_RemoveTitle, vm.RemoveDescription, WinoIconGlyph.Delete, remove);
        Bind.Bind(vm, nameof(vm.RemoveDescription), s => s.RemoveDescription, text => removeCard.Description = text);

        return [signIn, troubleshooting, removeCard];
    }
}
