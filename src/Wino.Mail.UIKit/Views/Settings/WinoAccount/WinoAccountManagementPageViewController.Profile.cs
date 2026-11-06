using System.Windows.Input;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Signed-in state: the profile card (photo with its options, name with rename, email, save status,
/// Change password and Sign out), then the add-ons and the sync sections from the other partials.
/// </summary>
public sealed partial class WinoAccountManagementPageViewController
{
    private UIView SignedInPanel()
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
    /// Windows account header: the photo is replaced in place (tapping the picture, which carries the pencil badge,
    /// opens Change photo / Remove photo), the name has its own rename button, and save results show under the email
    /// address. On phone width Change password and Sign out move under the identity.
    /// </summary>
    private UIView ProfileCard()
    {
        var vm = ViewModel;

        // Photo with the busy veil and the options button over it.
        var picture = new WinoContactPicture(72);
        WinoAccessibility.Label(picture, Translator.WinoAccount_Profile_Preview);
        var veil = new WinoSurfaceView { Fill = WinoStyle.Dynamic(WinoStyle.Hex(0xFFFFFF, 0.55), WinoStyle.Hex(0x000000, 0.45)), CornerRadius = 36, UserInteractionEnabled = false };
        var veilSpinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = true, TranslatesAutoresizingMaskIntoConstraints = false };
        veil.AddSubview(veilSpinner);
        var options = PhotoOptionsButton();
        var photo = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(photo, 72, 72);
        WinoLayout.Fill(picture, photo);
        WinoLayout.Fill(veil, photo);
        WinoLayout.Fill(options, photo);
        NSLayoutConstraint.ActivateConstraints([veilSpinner.CenterXAnchor.ConstraintEqualTo(veil.CenterXAnchor), veilSpinner.CenterYAnchor.ConstraintEqualTo(veil.CenterYAnchor)]);

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
            if (busy) veilSpinner.StartAnimating(); else veilSpinner.StopAnimating();
            options.Enabled = !busy;
        });

        // Name and rename; the name truncates so the pencil stays next to it.
        var name = Bind.Label(vm, nameof(vm.AccountDisplayName), s => s.AccountDisplayName, WinoStyle.Scaled(20, UIFontWeight.Semibold), WinoStyle.PrimaryText);
        name.LineBreakMode = UILineBreakMode.TailTruncation;
        WinoLayout.Priorities(name, UILayoutConstraintAxis.Horizontal, 750, 250);
        name.AccessibilityIdentifier = "WinoProfileName";
        var rename = new SettingsGlyphButton(WinoIconGlyph.Edit, Translator.WinoAccount_Profile_EditName);
        var renameBinding = Bindings.Own(new Presentation.Bindings.CommandBinding(vm.RenameProfileCommand, () => null, enabled => rename.Enabled = enabled, Dispatcher, ReportError));
        Bind.OnActivated(rename, renameBinding.Execute);
        var nameRow = WinoLayout.HStack(0, name, rename, WinoLayout.Spacer());

        var email = Bind.Label(vm, nameof(vm.AccountEmail), s => s.AccountEmail, WinoStyle.Description, WinoStyle.SecondaryText);
        email.LineBreakMode = UILineBreakMode.MiddleTruncation;

        // Save status: a check for "Saved", a warning for failures, "Saving..." while busy.
        var saved = new WinoIconView(WinoIconGlyph.Checkmark, 12, WinoStyle.Success);
        WinoLayout.Size(saved, 12, 12);
        var failed = new WinoIconView(WinoIconGlyph.Warning, 12, WinoStyle.Critical);
        WinoLayout.Size(failed, 12, 12);
        var statusText = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        statusText.AccessibilityIdentifier = "WinoProfileStatus";
        WinoLayout.Priorities(statusText, UILayoutConstraintAxis.Horizontal, 1, 250);
        var status = WinoLayout.HStack(6, saved, failed, statusText);
        WinoLayout.Insets(status, 4, 0, 0, 0);
        void UpdateStatus()
        {
            var message = vm.ProfileMessage ?? string.Empty;
            var busy = vm.IsProfileBusy;
            var wasHidden = status.Hidden;
            status.Hidden = !busy && string.IsNullOrWhiteSpace(message);
            saved.Hidden = busy || message != Translator.WinoAccount_Profile_Saved;
            failed.Hidden = busy || string.IsNullOrWhiteSpace(message) || message == Translator.WinoAccount_Profile_Saved;
            statusText.Text = busy ? Translator.WinoAccount_Profile_Saving : message;
            // Windows LiveSetting="Polite": announce a new result.
            if (!status.Hidden && (!busy || wasHidden) && !string.IsNullOrEmpty(statusText.Text))
                UIAccessibility.PostNotification(UIAccessibilityPostNotification.Announcement, new NSString(statusText.Text));
        }
        Bind.Bind(vm, nameof(vm.ProfileMessage), s => s.ProfileMessage, _ => UpdateStatus());
        Bind.Bind(vm, nameof(vm.IsProfileBusy), s => s.IsProfileBusy, _ => UpdateStatus());

        var identity = WinoLayout.VStack(2, nameRow, email, status);
        identity.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Priorities(identity, UILayoutConstraintAxis.Horizontal, 1, 250);
        var top = WinoLayout.HStack(16, photo, identity);

        var busyIndicator = BusySpinner(vm, nameof(vm.IsBusy), s => s.IsBusy);
        var changePassword = Bind.Button(Translator.WinoAccount_ChangePassword_Title, vm.ChangePasswordCommand);
        changePassword.AccessibilityIdentifier = "WinoAccountChangePasswordButton";
        var signOut = Bind.Button(Translator.WinoAccount_Management_SignOutTitle, vm.SignOutCommand);
        signOut.AccessibilityIdentifier = "WinoAccountSignOutButton";
        foreach (var button in new[] { changePassword, signOut }) button.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        var actions = WinoLayout.HStack(8, changePassword, signOut, busyIndicator, WinoLayout.Spacer());

        var stack = WinoLayout.VStack(16, top, actions);
        stack.Alignment = UIStackViewAlignment.Fill;
        var card = Surface(stack, 20, 8);
        card.AccessibilityIdentifier = "WinoAccountProfileCard";
        return card;
    }

    /// <summary>
    /// The photo button with the pencil badge on its corner: a menu with Change photo and Remove photo, enabled from
    /// their commands each time the menu opens. The whole 72pt picture is the touch target.
    /// </summary>
    private UIButton PhotoOptionsButton()
    {
        var vm = ViewModel;
        var button = new UIButton(UIButtonType.Custom) { ShowsMenuAsPrimaryAction = true, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoAccessibility.Label(button, Translator.ContactAction_PhotoOptions);
        button.AccessibilityIdentifier = "WinoProfilePhotoOptions";

        var badge = new WinoSurfaceView { Fill = WinoStyle.Dynamic(UIColor.White, WinoStyle.Hex(0x3A3A3C)), Stroke = WinoSettingsStyle.CardStroke, CornerRadius = 14, UserInteractionEnabled = false, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(badge, 28, 28);
        var pencil = new WinoIconView(WinoIconGlyph.Edit, 13, WinoStyle.PrimaryText) { UserInteractionEnabled = false, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(pencil, 13, 13);
        badge.AddSubview(pencil);
        button.AddSubview(badge);
        NSLayoutConstraint.ActivateConstraints(
        [
            pencil.CenterXAnchor.ConstraintEqualTo(badge.CenterXAnchor),
            pencil.CenterYAnchor.ConstraintEqualTo(badge.CenterYAnchor),
            badge.TrailingAnchor.ConstraintEqualTo(button.TrailingAnchor),
            badge.BottomAnchor.ConstraintEqualTo(button.BottomAnchor)
        ]);

        static UIAction Item(string title, WinoIconGlyph glyph, ICommand command, bool destructive = false)
        {
            var action = UIAction.Create(title, WinoIcons.Image(glyph, 16), null, _ => { if (command.CanExecute(null)) command.Execute(null); });
            if (!command.CanExecute(null)) action.Attributes |= UIMenuElementAttributes.Disabled;
            if (destructive) action.Attributes |= UIMenuElementAttributes.Destructive;
            return action;
        }
        button.Menu = UIMenu.Create(
        [
            UIDeferredMenuElement.CreateUncached(completion => completion(
            [
                Item(Translator.ContactAction_ChangePhoto, WinoIconGlyph.Camera, vm.ChooseProfilePhotoCommand),
                Item(Translator.WinoAccount_Profile_RemovePhoto, WinoIconGlyph.Delete, vm.RemoveProfilePhotoCommand, destructive: true)
            ]))
        ]);
        return button;
    }

    /// <summary>The draft being uploaded wins over the saved photo (Windows GetContactEditorPreviewPicture).</summary>
    private UIImage? LoadPicture(byte[]? draft, string? path)
    {
        try
        {
            if (draft is { Length: > 0 }) return UIImage.LoadFromData(NSData.FromArray(draft));
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return UIImage.FromFile(path);
        }
        catch (Exception exception) { ReportError(exception); }
        return null;
    }
}
