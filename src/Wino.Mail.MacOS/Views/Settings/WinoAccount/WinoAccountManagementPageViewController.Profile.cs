using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Signed-in state: the profile card (photo with its options, name with rename, email, save status,
/// Change password and Sign out), then the add-ons and the sync sections from the other partials.
/// </summary>
public sealed partial class WinoAccountManagementPageViewController
{
    private NSView SignedInPanel()
    {
        var panel = Panel();
        AddTo(panel, ProfileCard());
        AddTo(panel, SectionHeader(Translator.WinoAccount_Management_AddOnsSectionHeader,
            LinkButton(Translator.WinoAccount_Management_RefreshPurchases, ViewModel.RefreshPurchasesCommand)));
        foreach (var view in AddOnCards()) AddTo(panel, view);
        AddTo(panel, SectionHeader(Translator.WinoAccount_Management_ManagementSectionHeader));
        foreach (var view in SyncCards()) AddTo(panel, view);
        return panel;
    }

    /// <summary>
    /// Windows account header: the photo is renamed and replaced in place (the pencil on the picture
    /// opens Change photo / Remove photo), the name has its own rename button, and save results show
    /// under the email address.
    /// </summary>
    private NSView ProfileCard()
    {
        var vm = ViewModel;

        // Photo with the busy veil and the options button on its corner.
        var picture = new WinoContactPicture(72);
        WinoAccessibility.Label(picture, Translator.WinoAccount_Profile_Preview);
        var veil = new WinoSurfaceView { Fill = WinoStyle.Dynamic(WinoStyle.Hex(0xFFFFFF, 0.55), WinoStyle.Hex(0x000000, 0.45)), CornerRadius = 36, TranslatesAutoresizingMaskIntoConstraints = false };
        var veilSpinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, Indeterminate = true, IsDisplayedWhenStopped = false, ControlSize = NSControlSize.Regular, TranslatesAutoresizingMaskIntoConstraints = false };
        veil.AddSubview(veilSpinner);
        NSLayoutConstraint.ActivateConstraints([veilSpinner.CenterXAnchor.ConstraintEqualTo(veil.CenterXAnchor), veilSpinner.CenterYAnchor.ConstraintEqualTo(veil.CenterYAnchor)]);
        var options = PhotoOptionsButton();
        var photo = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        photo.AddSubview(picture);
        photo.AddSubview(veil);
        photo.AddSubview(options);
        WinoLayout.Size(photo, 72, 72);
        WinoLayout.Fill(picture, photo);
        WinoLayout.Fill(veil, photo);
        NSLayoutConstraint.ActivateConstraints([options.TrailingAnchor.ConstraintEqualTo(photo.TrailingAnchor), options.BottomAnchor.ConstraintEqualTo(photo.BottomAnchor)]);

        void UpdatePicture()
        {
            picture.SetIdentity(vm.AccountDisplayName, vm.AccountEmail, WinoStyle.Accent);
            picture.Image = LoadPicture(vm.ProfilePhotoDraft, vm.AccountAvatarPath);
        }
        Bind.Bind(vm, nameof(vm.AccountDisplayName), s => s.AccountDisplayName, _ => UpdatePicture());
        Bind.Bind(vm, nameof(vm.AccountAvatarPath), s => s.AccountAvatarPath, _ => UpdatePicture());
        Bind.Bind(vm, nameof(vm.ProfilePhotoDraft), s => s.ProfilePhotoDraft, _ => UpdatePicture());
        Bind.Bind(vm, nameof(vm.IsProfileBusy), s => s.IsProfileBusy, busy =>
        {
            veil.Hidden = !busy;
            if (busy) veilSpinner.StartAnimation(null); else veilSpinner.StopAnimation(null);
            options.Enabled = !busy;
        });

        // Name and rename; the name truncates so the pencil stays next to it.
        var name = Bind.Label(vm, nameof(vm.AccountDisplayName), s => s.AccountDisplayName, NSFont.SystemFontOfSize(20, NSFontWeight.Semibold), WinoStyle.PrimaryText);
        name.LineBreakMode = NSLineBreakMode.TruncatingTail;
        name.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);
        name.SetValueForKey(new NSString("WinoProfileName"), new NSString("accessibilityIdentifier"));
        var rename = Bind.Button(string.Empty, vm.RenameProfileCommand, icon: WinoIconGlyph.Edit);
        rename.Bordered = false;
        rename.ToolTip = Translator.WinoAccount_Profile_EditName;
        WinoAccessibility.Label(rename, Translator.WinoAccount_Profile_EditName);
        var nameRow = WinoLayout.HStack(4, name, rename);

        var email = Bind.Label(vm, nameof(vm.AccountEmail), s => s.AccountEmail, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
        email.Selectable = true;

        // Save status: a check for "Saved", a warning for failures, "Saving..." while busy.
        var saved = new WinoIconView(WinoIconGlyph.Checkmark, 12, WinoStyle.Success);
        var failed = new WinoIconView(WinoIconGlyph.Warning, 12, WinoStyle.Critical);
        var statusText = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText, 0);
        statusText.SetValueForKey(new NSString("WinoProfileStatus"), new NSString("accessibilityIdentifier"));
        var status = WinoLayout.HStack(6, saved, failed, statusText);
        status.EdgeInsets = new NSEdgeInsets(4, 0, 0, 0);
        void UpdateStatus()
        {
            var message = vm.ProfileMessage ?? string.Empty;
            var busy = vm.IsProfileBusy;
            var wasHidden = status.Hidden;
            status.Hidden = !busy && string.IsNullOrWhiteSpace(message);
            saved.Hidden = busy || message != Translator.WinoAccount_Profile_Saved;
            failed.Hidden = busy || string.IsNullOrWhiteSpace(message) || message == Translator.WinoAccount_Profile_Saved;
            statusText.StringValue = busy ? Translator.WinoAccount_Profile_Saving : message;
            // Windows LiveSetting="Polite": announce a new result.
            if (!status.Hidden && (!busy || wasHidden))
                NSAccessibility.PostNotification(statusText, new NSString("AXAnnouncementRequested"),
                    NSDictionary.FromObjectAndKey(new NSString(statusText.StringValue), NSAccessibilityNotificationUserInfoKeys.AnnouncementKey));
        }
        Bind.Bind(vm, nameof(vm.ProfileMessage), s => s.ProfileMessage, _ => UpdateStatus());
        Bind.Bind(vm, nameof(vm.IsProfileBusy), s => s.IsProfileBusy, _ => UpdateStatus());

        var identity = WinoLayout.VStack(2, nameRow, email, status);
        identity.Alignment = NSLayoutAttribute.Leading;
        nameRow.WidthAnchor.ConstraintLessThanOrEqualTo(identity.WidthAnchor).Active = true;
        identity.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);

        var busyIndicator = BusySpinner(vm, nameof(vm.IsBusy), s => s.IsBusy);
        var changePassword = Bind.Button(Translator.WinoAccount_ChangePassword_Title, vm.ChangePasswordCommand);
        changePassword.SetValueForKey(new NSString("WinoAccountChangePasswordButton"), new NSString("accessibilityIdentifier"));
        var signOut = Bind.Button(Translator.WinoAccount_Management_SignOutTitle, vm.SignOutCommand);
        signOut.SetValueForKey(new NSString("WinoAccountSignOutButton"), new NSString("accessibilityIdentifier"));
        var actions = WinoLayout.HStack(8, busyIndicator, changePassword, signOut);
        actions.SetCustomSpacing(16, busyIndicator);
        actions.SetHuggingPriority(751, NSLayoutConstraintOrientation.Horizontal);

        var row = WinoLayout.HStack(16, photo, identity, actions);
        row.Alignment = NSLayoutAttribute.CenterY;
        var card = Surface(row, 20, 8);
        card.AccessibilityIdentifier = "WinoAccountProfileCard";
        return card;
    }

    /// <summary>The round pencil on the photo: a menu with Change photo and Remove photo (enabled from their commands).</summary>
    private NSButton PhotoOptionsButton()
    {
        var vm = ViewModel;
        var button = new NSButton
        {
            Title = string.Empty,
            BezelStyle = NSBezelStyle.Circular,
            Image = WinoIcons.Image(WinoIconGlyph.Edit, 13, accessibilityDescription: Translator.ContactAction_PhotoOptions),
            ImagePosition = NSCellImagePosition.ImageOnly,
            ToolTip = Translator.ContactAction_PhotoOptions,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Size(button, 28, 28);
        WinoAccessibility.Label(button, Translator.ContactAction_PhotoOptions);
        button.SetValueForKey(new NSString("WinoProfilePhotoOptions"), new NSString("accessibilityIdentifier"));
        Bind.OnActivated(button, () =>
        {
            var menu = new NSMenu { AutoEnablesItems = false };
            NSMenuItem Item(string title, WinoIconGlyph glyph, System.Windows.Input.ICommand command)
            {
                var item = new NSMenuItem(title, (_, _) => { if (command.CanExecute(null)) command.Execute(null); })
                {
                    Image = WinoIcons.Image(glyph, 14),
                    Enabled = command.CanExecute(null)
                };
                return item;
            }
            menu.AddItem(Item(Translator.ContactAction_ChangePhoto, WinoIconGlyph.Camera, vm.ChooseProfilePhotoCommand));
            menu.AddItem(Item(Translator.WinoAccount_Profile_RemovePhoto, WinoIconGlyph.Delete, vm.RemoveProfilePhotoCommand));
            menu.PopUpMenu(null, new CGPoint(0, button.IsFlipped ? button.Bounds.Height + 4 : -4), button);
        });
        return button;
    }

    /// <summary>The draft being uploaded wins over the saved photo (Windows GetContactEditorPreviewPicture).</summary>
    private NSImage? LoadPicture(byte[]? draft, string? path)
    {
        try
        {
            if (draft is { Length: > 0 }) return new NSImage(NSData.FromArray(draft));
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return new NSImage(path);
        }
        catch (Exception exception) { ReportError(exception); }
        return null;
    }
}
