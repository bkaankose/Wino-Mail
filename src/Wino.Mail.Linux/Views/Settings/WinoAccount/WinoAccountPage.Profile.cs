using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings.WinoAccount;

/// <summary>
/// Signed-in state: the profile card (photo with its options, name with rename, e-mail, save status, Change password and
/// Sign out), then the add-ons and the management sections from the other partials.
/// </summary>
internal sealed partial class WinoAccountPage
{
    private readonly HashSet<WinoSettingsGroup> _signedInGroups = [];

    private IEnumerable<WinoSettingsGroup> SignedInGroups()
    {
        var profile = SignedIn(Page.AddGroup());
        profile.HidesWhenEmpty = false;
        profile.Root.Add(ProfileCard());
        yield return profile;

        var addOns = SignedIn(Page.AddGroup(Translator.WinoAccount_Management_AddOnsSectionHeader));
        var refresh = Settings.Button(ViewModel.RefreshPurchasesText, ViewModel.RefreshPurchasesCommand);
        refresh.AddCssClass("flat");
        WinoA11y.Rename(refresh, ViewModel.RefreshPurchasesText);
        addOns.HeaderSuffix = refresh;
        foreach (var row in AddOnRows()) addOns.Add(row);
        yield return addOns;

        var management = SignedIn(Page.AddGroup(Translator.WinoAccount_Management_ManagementSectionHeader));
        foreach (var row in ManagementRows()) management.Add(row);
        yield return management;
    }

    private WinoSettingsGroup SignedIn(WinoSettingsGroup group)
    {
        _signedInGroups.Add(group);
        return group;
    }

    private Gtk.Widget ProfileCard()
    {
        var vm = ViewModel;
        var picture = new WinoContactPicture(72);
        WinoA11y.Label(picture.Root, Translator.WinoAccount_Profile_Preview);
        var busy = Adw.Spinner.New();
        busy.SetSizeRequest(32, 32);
        busy.SetHalign(Gtk.Align.Center);
        busy.SetValign(Gtk.Align.Center);
        var options = WinoSettingsRows.GlyphButton(WinoIconGlyph.Edit, Translator.ContactAction_PhotoOptions);
        options.RemoveCssClass("flat");
        options.AddCssClass("circular");
        options.SetHalign(Gtk.Align.End);
        options.SetValign(Gtk.Align.End);
        options.SetTooltipText(Translator.ContactAction_PhotoOptions);
        Settings.Own(GtkSignals.OnClicked(options, () => ContextMenu.Show(options, WinoMenus.Create()
            .Item(Translator.ContactAction_ChangePhoto, () => Execute(vm.ChooseProfilePhotoCommand), vm.ChooseProfilePhotoCommand.CanExecute(null), icon: WinoIconGlyph.Camera)
            .Item(Translator.WinoAccount_Profile_RemovePhoto, () => Execute(vm.RemoveProfilePhotoCommand), vm.RemoveProfilePhotoCommand.CanExecute(null), icon: WinoIconGlyph.Delete, destructive: true)
            .Build())));
        var photo = Gtk.Overlay.New();
        photo.SetChild(picture.Root);
        photo.AddOverlay(busy);
        photo.AddOverlay(options);
        photo.SetValign(Gtk.Align.Center);
        photo.SetSizeRequest(72, 72);

        void UpdatePicture()
        {
            picture.SetIdentity(vm.AccountDisplayName, vm.AccountEmail);
            picture.Picture = vm.ProfilePhotoDraft is { Length: > 0 } draft ? Textures.FromBytes(draft) : Textures.FromFile(vm.AccountAvatarPath);
        }
        Settings.Bind(vm, nameof(vm.AccountDisplayName), static s => s.AccountDisplayName, _ => UpdatePicture());
        Settings.Bind(vm, nameof(vm.AccountAvatarPath), static s => s.AccountAvatarPath, _ => UpdatePicture());
        Settings.Bind(vm, nameof(vm.ProfilePhotoDraft), static s => s.ProfilePhotoDraft, _ => UpdatePicture());

        var name = Gtk.Label.New(string.Empty);
        name.SetXalign(0);
        name.SetEllipsize(Pango.EllipsizeMode.End);
        name.AddCssClass("title-3");
        Settings.Bind(vm, nameof(vm.AccountDisplayName), static s => s.AccountDisplayName, text => name.SetText(text ?? string.Empty));
        var rename = Settings.Button(string.Empty, vm.RenameProfileCommand, icon: WinoIconGlyph.Edit);
        rename.AddCssClass("flat");
        rename.SetTooltipText(Translator.WinoAccount_Profile_EditName);
        WinoA11y.Rename(rename, Translator.WinoAccount_Profile_EditName);
        rename.SetValign(Gtk.Align.Center);
        var nameRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        nameRow.Append(name);
        nameRow.Append(rename);

        var email = Gtk.Label.New(string.Empty);
        email.SetXalign(0);
        email.SetSelectable(true);
        email.SetEllipsize(Pango.EllipsizeMode.Middle);
        email.AddCssClass("dim-label");
        Settings.Bind(vm, nameof(vm.AccountEmail), static s => s.AccountEmail, text => email.SetText(text ?? string.Empty));

        var saved = new WinoIcon(WinoIconGlyph.Checkmark, 12).Root;
        saved.AddCssClass("success");
        var failed = new WinoIcon(WinoIconGlyph.Warning, 12).Root;
        failed.AddCssClass("error");
        var statusText = Gtk.Label.New(string.Empty);
        statusText.AddCssClass("caption");
        statusText.AddCssClass("dim-label");
        statusText.SetWrap(true);
        statusText.SetXalign(0);
        var status = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        status.Append(saved);
        status.Append(failed);
        status.Append(statusText);
        void UpdateStatus()
        {
            var message = vm.ProfileMessage ?? string.Empty;
            var working = vm.IsProfileBusy;
            status.SetVisible(working || !string.IsNullOrWhiteSpace(message));
            saved.SetVisible(!working && message == Translator.WinoAccount_Profile_Saved);
            failed.SetVisible(!working && !string.IsNullOrWhiteSpace(message) && message != Translator.WinoAccount_Profile_Saved);
            statusText.SetText(working ? Translator.WinoAccount_Profile_Saving : message);
            busy.SetVisible(working);
            options.SetSensitive(!working);
        }
        Settings.Bind(vm, nameof(vm.ProfileMessage), static s => s.ProfileMessage, _ => UpdateStatus());
        Settings.Bind(vm, nameof(vm.IsProfileBusy), static s => s.IsProfileBusy, _ => UpdateStatus());

        var identity = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        identity.SetHexpand(true);
        identity.SetValign(Gtk.Align.Center);
        identity.Append(nameRow);
        identity.Append(email);
        identity.Append(status);

        var actions = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        var actionBusy = Settings.Visible(Adw.Spinner.New(), vm, nameof(vm.IsBusy), static s => s.IsBusy);
        actions.Append(actionBusy);
        actions.Append(Named(Settings.Button(Translator.WinoAccount_ChangePassword_Title, vm.ChangePasswordCommand), Translator.WinoAccount_ChangePassword_Title));
        actions.Append(Named(Settings.Button(Translator.WinoAccount_Management_SignOutTitle, vm.SignOutCommand), Translator.WinoAccount_Management_SignOutTitle));
        actions.SetMarginTop(8);

        var top = Gtk.Box.New(Gtk.Orientation.Horizontal, 16);
        top.Append(photo);
        top.Append(identity);
        var card = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        card.AddCssClass("card");
        top.SetMarginTop(20);
        top.SetMarginStart(20);
        top.SetMarginEnd(20);
        actions.SetMarginStart(108);
        actions.SetMarginEnd(20);
        actions.SetMarginBottom(16);
        card.Append(top);
        card.Append(actions);
        return card;
    }

    private static Gtk.Button Named(Gtk.Button button, string name)
    {
        WinoA11y.Rename(button, name);
        return button;
    }

    private static void Execute(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }
}
