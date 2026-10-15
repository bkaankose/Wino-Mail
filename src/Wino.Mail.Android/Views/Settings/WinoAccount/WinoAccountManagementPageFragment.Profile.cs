using System.Windows.Input;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Android.Common;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Settings.WinoAccount;

/// <summary>
/// Signed-in state: the profile card (photo with its options, name with rename, email, save status, Change password and
/// Sign out), then the add-ons and the management sections from the other partials.
/// </summary>
public sealed partial class WinoAccountManagementPageFragment
{
    private View SignedInPanel()
    {
        var panel = Panel();
        AddTo(panel, ProfileCard());
        AddTo(panel, SectionHeader(Translator.WinoAccount_Management_AddOnsSectionHeader,
            LinkButton(Translator.WinoAccount_Management_RefreshPurchases, VM.RefreshPurchasesCommand)));
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
    private View ProfileCard()
    {
        var vm = VM;
        var context = Ctx;
        int photoSize = WinoTokens.Px(context, Resource.Dimension.wino_winoaccount_photo);

        // Photo with the busy veil and the pencil badge; the whole picture opens the photo options.
        var picture = new WinoContactPicture(context) { Size = WinoTokens.Dp(context, Resource.Dimension.wino_winoaccount_photo) };
        var veil = new FrameLayout(context)
        {
            Background = IntelligenceViews.Oval(context, WinoTokens.Color(context, Resource.Color.wino_winoaccount_photo_veil)),
            Visibility = ViewStates.Gone
        };
        var veilSpinner = IntelligenceViews.Spinner(context, Resource.Dimension.wino_glyph_l);
        veilSpinner.Visibility = ViewStates.Visible;
        veil.AddView(veilSpinner, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));
        var pencil = new WinoIconView(context) { Icon = WinoIconGlyph.Edit, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_xs) };
        pencil.SetTextColor(WinoColors.PrimaryText(context));
        pencil.Background = IntelligenceViews.Oval(context, WinoTokens.Color(context, Resource.Color.wino_winoaccount_photo_badge_fill), WinoColors.CardStroke(context));
        int badgeSize = WinoTokens.Px(context, Resource.Dimension.wino_winoaccount_photo_badge);
        var photo = new FrameLayout(context)
        {
            Clickable = true,
            Focusable = true,
            ContentDescription = Translator.ContactAction_PhotoOptions
        };
        photo.AddView(picture, new FrameLayout.LayoutParams(photoSize, photoSize));
        photo.AddView(veil, new FrameLayout.LayoutParams(photoSize, photoSize));
        photo.AddView(pencil, new FrameLayout.LayoutParams(badgeSize, badgeSize, GravityFlags.End | GravityFlags.Bottom));
        Bind.Views.Click(photo, () => ShowPhotoOptions(photo));

        void UpdatePicture()
        {
            picture.SetIdentity(vm.AccountDisplayName, vm.AccountEmail, WinoColors.Accent(context));
            picture.Image = LoadPicture(vm.ProfilePhotoDraft, vm.AccountAvatarPath);
        }
        Bind.Bind(vm, nameof(vm.AccountDisplayName), s => s.AccountDisplayName, _ => UpdatePicture());
        Bind.Bind(vm, nameof(vm.AccountAvatarPath), s => s.AccountAvatarPath, _ => UpdatePicture());
        Bind.Bind(vm, nameof(vm.ProfilePhotoDraft), s => s.ProfilePhotoDraft, _ => UpdatePicture());
        Bind.Bind(vm, nameof(vm.IsProfileBusy), s => s.IsProfileBusy, busy =>
        {
            veil.Visibility = busy ? ViewStates.Visible : ViewStates.Gone;
            photo.Enabled = !busy;
        });

        // Name and rename; the name truncates so the pencil stays next to it.
        var name = Bind.Label(vm, nameof(vm.AccountDisplayName), s => s.AccountDisplayName, WinoTextStyle.Title);
        name.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        name.SetSingleLine(true);
        var rename = Bind.Views.Command(Bind.IconButton(WinoIconGlyph.Edit, Translator.WinoAccount_Profile_EditName), vm.RenameProfileCommand);
        var nameRow = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        nameRow.SetGravity(GravityFlags.CenterVertical);
        nameRow.AddView(name, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { Weight = 1 });
        nameRow.AddView(rename);

        var email = Bind.Label(vm, nameof(vm.AccountEmail), s => s.AccountEmail, WinoTextStyle.Description);
        email.Ellipsize = global::Android.Text.TextUtils.TruncateAt.Middle;
        email.SetSingleLine(true);

        // Save status: a check for "Saved", a warning for failures, "Saving..." while busy.
        var saved = new WinoIconView(context) { Icon = WinoIconGlyph.Checkmark, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_xs) };
        saved.SetTextColor(WinoColors.Success(context));
        var failed = new WinoIconView(context) { Icon = WinoIconGlyph.Warning, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_glyph_xs) };
        failed.SetTextColor(WinoColors.Critical(context));
        var statusText = Text(string.Empty, WinoTextStyle.Caption);
        // Windows LiveSetting="Polite": TalkBack announces a new result.
        statusText.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        var status = IntelligenceViews.HStack(context, Resource.Dimension.wino_space_xs, statusText, saved, failed, statusText);
        WinoTokens.Pad(status, 0, Resource.Dimension.wino_space_2xs, 0, 0);
        void UpdateStatus()
        {
            var message = vm.ProfileMessage ?? string.Empty;
            var busy = vm.IsProfileBusy;
            status.Visibility = !busy && string.IsNullOrWhiteSpace(message) ? ViewStates.Gone : ViewStates.Visible;
            saved.Visibility = busy || message != Translator.WinoAccount_Profile_Saved ? ViewStates.Gone : ViewStates.Visible;
            failed.Visibility = busy || string.IsNullOrWhiteSpace(message) || message == Translator.WinoAccount_Profile_Saved ? ViewStates.Gone : ViewStates.Visible;
            statusText.Text = busy ? Translator.WinoAccount_Profile_Saving : message;
        }
        Bind.Bind(vm, nameof(vm.ProfileMessage), s => s.ProfileMessage, _ => UpdateStatus());
        Bind.Bind(vm, nameof(vm.IsProfileBusy), s => s.IsProfileBusy, _ => UpdateStatus());

        var identity = Stack(Resource.Dimension.wino_space_3xs, nameRow, email, status);
        var top = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        top.SetGravity(GravityFlags.CenterVertical);
        top.AddView(photo, new LinearLayout.LayoutParams(photoSize, photoSize));
        top.AddView(identity, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_l) });

        var busyIndicator = BusySpinner(vm, nameof(vm.IsBusy), s => s.IsBusy);
        var changePassword = Bind.Button(Translator.WinoAccount_ChangePassword_Title, vm.ChangePasswordCommand);
        var signOut = Bind.Button(Translator.WinoAccount_Management_SignOutTitle, vm.SignOutCommand);
        var actions = IntelligenceViews.Flow(context, [changePassword, signOut, busyIndicator], Resource.Dimension.wino_space_s);

        var stack = Stack(Resource.Dimension.wino_space_l, top, actions);
        return Surface(stack, Resource.Dimension.wino_space_xl, Resource.Dimension.wino_radius_large);
    }

    /// <summary>
    /// Change photo and Remove photo (Windows photo flyout), enabled from their commands each time the menu opens.
    /// </summary>
    private void ShowPhotoOptions(View anchor)
    {
        var vm = VM;
        var menu = new PopupMenu(Ctx, anchor);
        var items = new List<ICommand> { vm.ChooseProfilePhotoCommand, vm.RemoveProfilePhotoCommand };
        string[] titles = [Translator.ContactAction_ChangePhoto, Translator.WinoAccount_Profile_RemovePhoto];
        for (int index = 0; index < items.Count; index++)
            menu.Menu.Add(0, index, index, titles[index])?.SetEnabled(items[index].CanExecute(null));
        menu.MenuItemClick += (_, args) =>
        {
            int index = args.Item?.ItemId ?? -1;
            if (index < 0 || index >= items.Count) return;
            try { if (items[index].CanExecute(null)) items[index].Execute(null); }
            catch (Exception exception) { ReportError(exception); }
        };
        menu.Show();
    }

    /// <summary>The draft being uploaded wins over the saved photo (Windows GetContactEditorPreviewPicture).</summary>
    private Bitmap? LoadPicture(byte[]? draft, string? path)
    {
        try
        {
            if (draft is { Length: > 0 }) return BitmapFactory.DecodeByteArray(draft, 0, draft.Length);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return BitmapFactory.DecodeFile(path);
        }
        catch (Exception exception) { ReportError(exception); }
        return null;
    }
}
