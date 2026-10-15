using System.ComponentModel;
using Android.Content;
using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Widget;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Chip;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Android.Views.Shell;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;

namespace Wino.Mail.Android.Views.Contacts;

/// <summary>
/// The contact card (Windows ContactsPage detail pane; Mac and iOS contact cards; tablet board ContactsTablet) over the
/// shared <see cref="ContactsPageViewModel.SelectedContact"/>. Not a routed page: <see cref="ContactsFragment"/> places it
/// through <see cref="AndroidNavigationService.ShowDetailAsync"/>, so it covers the shell on compact widths (with Back)
/// and sits in the reader pane otherwise. It shows the picture, name, job, source and category chips, Send mail and Edit,
/// the Windows field sections (Email, Phone, Address, Personal; empty ones left out), Remove from this list on a list
/// filter, and Edit, Favourite and Delete in the top app bar. It closes when the selection empties.
/// </summary>
public sealed class ContactCardFragment : WinoFragment
{
    private const int MenuEdit = 1;
    private const int MenuFavorite = 2;
    private const int MenuDelete = 3;

    private readonly Dictionary<Guid, Bitmap?> _pictures = new();
    private ContactsPageViewModel? _viewModel;
    private AndroidNavigationService? _navigation;
    private IPictureStorageService? _pictureStorage;
    private AccountContactViewModel? _contact;
    private MaterialToolbar? _toolbar;
    private bool _compact;

    protected override int LayoutResource => Resource.Layout.fragment_contact_card;

    protected override void OnBind(View view)
    {
        var services = MainApplication.Services ?? throw new InvalidOperationException("The application services are not ready.");
        if (Entry is not { } entry) return;
        var context = view.RequireContext();
        _viewModel = services.GetRequiredService<ContactsPageViewModel>();
        _navigation = services.GetService<AndroidNavigationService>();
        _pictureStorage = services.GetService<IPictureStorageService>();
        _compact = services.GetRequiredService<IWindowSizeClassService>().WidthClass == WindowWidthClass.Compact;
        var dispatcher = entry.Dispatcher;

        if (view.FindViewById(Resource.Id.contact_card_content)?.Parent is WinoMaxWidthLayout column)
            column.MaxWidthDp = WinoTokens.Dp(context, Resource.Dimension.wino_people_card_max_width);
        if (view.FindViewById<WinoContactPicture>(Resource.Id.contact_card_avatar) is { } avatar)
            avatar.Size = WinoTokens.Dp(context, Resource.Dimension.wino_people_card_avatar);

        BuildToolbar(view, context);
        if (view.FindViewById<MaterialButton>(Resource.Id.contact_card_send) is { } send)
        {
            send.Text = Translator.ContactAction_SendMail;
            send.Icon = WinoIcons.Drawable(context, WinoIconGlyph.Mail, WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph_small), WinoColors.AccentText(context));
            send.Click += (_, _) => { if (_contact is { } contact && _viewModel.ComposeToContactCommand.CanExecute(contact)) _viewModel.ComposeToContactCommand.Execute(contact); };
        }
        if (view.FindViewById<MaterialButton>(Resource.Id.contact_card_edit) is { } edit)
        {
            edit.Text = Translator.Buttons_Edit;
            edit.Icon = WinoIcons.Drawable(context, WinoIconGlyph.Edit, WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph_small), WinoColors.AccentText(context));
            WinoMaterialAccent.Tint(edit, filled: false);
            edit.Click += (_, _) => Edit();
        }
        if (view.FindViewById<MaterialButton>(Resource.Id.contact_card_remove_from_list) is { } remove)
        {
            remove.Text = Translator.ContactAction_RemoveFromList;
            WinoMaterialAccent.Tint(remove, filled: false);
            remove.Click += (_, _) => { if (_contact is { } contact) PeopleViews.Observe(_viewModel.RemoveFromCurrentListCommand.ExecuteAsync(contact), ReportError); };
        }

        Bindings.Own(new PropertyBinding<ContactsPageViewModel, AccountContactViewModel?>(_viewModel, nameof(ContactsPageViewModel.SelectedContact),
            static vm => vm.SelectedContact, Show, dispatcher, ReportError));
        Bindings.Own(new PropertyBinding<ContactsPageViewModel, bool>(_viewModel, nameof(ContactsPageViewModel.SelectedFilter),
            static vm => vm.SelectedFilter?.IsList == true, _ => { if (_contact is { } contact) Apply(contact); }, dispatcher, ReportError));
        Bindings.Own(new ActionDisposable(() => Follow(null)));
    }

    private void BuildToolbar(View view, Context context)
    {
        _toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.contact_card_toolbar);
        if (_toolbar is null) return;
        if (_compact)
        {
            // Full screen over the shell: pad clear of the status bar and offer Back.
            WinoInsets.Apply(view.FindViewById(Resource.Id.contact_card_root) ?? view, top: true, bottom: true);
            _toolbar.SetNavigationIcon(Resource.Drawable.wino_ic_arrow_back);
            _toolbar.NavigationContentDescription = Translator.Buttons_Back;
            _toolbar.NavigationClick += (_, _) => Activity?.OnBackPressedDispatcher.OnBackPressed();
        }
        double glyph = WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph);
        var menu = _toolbar.Menu;
        menu.Clear();
        menu.Add(0, MenuEdit, 0, Translator.ContactAction_Edit)?.SetIcon(WinoIcons.Drawable(context, WinoIconGlyph.Edit, glyph, WinoColors.SecondaryText(context)))?.SetShowAsAction(ShowAsAction.IfRoom);
        menu.Add(0, MenuFavorite, 1, Translator.ContactAction_Favorite)?.SetShowAsAction(ShowAsAction.IfRoom);
        menu.Add(0, MenuDelete, 2, Translator.ContactAction_Delete)?.SetIcon(WinoIcons.Drawable(context, WinoIconGlyph.Delete, glyph, WinoColors.SecondaryText(context)))?.SetShowAsAction(ShowAsAction.IfRoom);
        _toolbar.MenuItemClick += (_, args) =>
        {
            if (_contact is not { } contact || _viewModel is not { } viewModel) return;
            switch (args.Item?.ItemId)
            {
                case MenuEdit: Edit(); break;
                case MenuFavorite: PeopleViews.Observe(viewModel.ToggleFavoriteCommand.ExecuteAsync(contact), ReportError); break;
                case MenuDelete: PeopleViews.Observe(viewModel.DeleteContactCommand.ExecuteAsync(contact), ReportError); break;
            }
        };
    }

    private void Edit()
    {
        if (_contact is { } contact && _viewModel is { } viewModel && contact.IsEditable && viewModel.EditContactCommand.CanExecute(contact))
            viewModel.EditContactCommand.Execute(contact);
    }

    private void Show(AccountContactViewModel? contact)
    {
        if (View is null) return;
        Follow(contact);
        if (contact is null)
        {
            // Deleted or cleared: the card leaves its host.
            if (_navigation is { CurrentDetailKey: ContactsFragment.CardKey } navigation) PeopleViews.Observe(navigation.CloseRenderingFrameAsync(), ReportError);
            return;
        }
        Apply(contact);
    }

    private void Follow(AccountContactViewModel? contact)
    {
        if (_contact is not null) _contact.PropertyChanged -= ContactChanged;
        _contact = contact;
        if (contact is not null) contact.PropertyChanged += ContactChanged;
    }

    private void ContactChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (null or "" or nameof(AccountContactViewModel.IsFavorite) or nameof(AccountContactViewModel.SourceContact)
            or nameof(AccountContactViewModel.Categories) or nameof(AccountContactViewModel.Name) or nameof(AccountContactViewModel.ContactPictureFileId))) return;
        if (Entry is { } entry) _ = entry.Dispatcher.ExecuteOnUIThread(() => { if (View is not null && _contact is { } current) Apply(current); });
    }

    private void Apply(AccountContactViewModel contact)
    {
        if (View is not { } view || _viewModel is not { } viewModel) return;
        var context = view.RequireContext();
        var source = contact.SourceContact;
        if (_toolbar is not null)
        {
            _toolbar.Title = _compact ? string.Empty : contact.Name;
            if (_toolbar.Menu.FindItem(MenuFavorite) is { } favorite)
            {
                favorite.SetTitle(contact.FavoriteActionText);
                favorite.SetIcon(WinoIcons.Drawable(context, contact.IsFavorite ? WinoIconGlyph.StarFilled : WinoIconGlyph.Star, WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph),
                    contact.IsFavorite ? WinoColors.Caution(context) : WinoColors.SecondaryText(context)));
            }
            _toolbar.Menu.FindItem(MenuEdit)?.SetVisible(contact.IsEditable);
            _toolbar.Menu.FindItem(MenuDelete)?.SetVisible(contact.CanDelete);
        }
        if (view.FindViewById<WinoContactPicture>(Resource.Id.contact_card_avatar) is { } avatar)
        {
            avatar.SetIdentity(contact.Name, contact.Address ?? contact.Name);
            avatar.Image = PeopleViews.LoadPicture(_pictureStorage, _pictures, contact.ContactPictureFileId, ReportError);
        }
        SetText(view, Resource.Id.contact_card_name, contact.Name);
        SetText(view, Resource.Id.contact_card_job, contact.JobTitleOrCompany);
        SetText(view, Resource.Id.contact_card_source, contact.SourceLabel);
        if (view.FindViewById<ChipGroup>(Resource.Id.contact_card_chips) is { } chips)
        {
            chips.RemoveAllViews();
            foreach (var category in contact.Categories)
            {
                var chip = WinoChips.Pill(context, category.Name ?? string.Empty, WinoIconGlyph.Tag);
                if (WinoColors.FromHexString(category.BackgroundColorHex) is { } fill)
                {
                    chip.ChipBackgroundColor = global::Android.Content.Res.ColorStateList.ValueOf(fill);
                    chip.SetTextColor(WinoColors.FromHexString(category.TextColorHex) ?? WinoColors.ReadableText(fill));
                    chip.ChipIcon = null;
                }
                chips.AddView(chip);
            }
            chips.Visibility = contact.HasCategories ? ViewStates.Visible : ViewStates.Gone;
        }
        if (view.FindViewById<MaterialButton>(Resource.Id.contact_card_send) is { } send) send.Enabled = viewModel.ComposeToContactCommand.CanExecute(contact);
        if (view.FindViewById<MaterialButton>(Resource.Id.contact_card_edit) is { } edit) edit.Visibility = contact.IsEditable ? ViewStates.Visible : ViewStates.Gone;
        if (view.FindViewById<MaterialButton>(Resource.Id.contact_card_remove_from_list) is { } remove)
            remove.Visibility = viewModel.SelectedFilter?.IsList == true ? ViewStates.Visible : ViewStates.Gone;
        if (view.FindViewById<LinearLayout>(Resource.Id.contact_card_sections) is { } sections) ApplySections(context, sections, source);
    }

    private static void SetText(View view, int id, string? text)
    {
        if (view.FindViewById<TextView>(id) is not { } label) return;
        label.Text = text ?? string.Empty;
        label.Visibility = string.IsNullOrWhiteSpace(text) ? ViewStates.Gone : ViewStates.Visible;
    }

    /// <summary>The Windows field sections in their order; an empty section is left out (Mac rule).</summary>
    private void ApplySections(Context context, LinearLayout host, AccountContact source)
    {
        host.RemoveAllViews();
        AddSection(context, host, Translator.ContactDetail_Email, source.EmailAddresses.OrderByDescending(item => item.IsPrimary).ThenBy(item => item.Order)
            .Select(item => (WinoIconGlyph.Mail, item.Address, item.Label, (Action?)(() => Compose(item.Address)))));
        AddSection(context, host, Translator.ContactDetail_Phone, source.PhoneNumbers.OrderByDescending(item => item.IsPrimary).ThenBy(item => item.Order)
            .Select(item => (WinoIconGlyph.Phone, item.Number, item.Kind.ToString(), (Action?)null)));
        AddSection(context, host, Translator.ContactDetail_Address, source.PostalAddresses
            .Select(item => (WinoIconGlyph.Location, ContactDisplayFormatter.FormatPostalAddress(item), item.Kind.ToString(), (Action?)null)));
        AddSection(context, host, Translator.ContactDetail_Personal,
        [
            (WinoIconGlyph.CalendarToday, ContactDisplayFormatter.FormatBirthday(source.BirthdayYear, source.BirthdayMonth, source.BirthdayDay), Translator.ContactDetail_Birthday, null),
            (WinoIconGlyph.Globe, source.Website, Translator.ContactDetail_Website, null),
            (WinoIconGlyph.Note, source.Notes, Translator.ContactDetail_Notes, null)
        ]);
    }

    private void Compose(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || _contact is not { } contact || _viewModel is not { } viewModel) return;
        if (viewModel.ComposeToContactCommand.CanExecute(contact)) viewModel.ComposeToContactCommand.Execute(contact);
    }

    private static void AddSection(Context context, LinearLayout host, string title, IEnumerable<(WinoIconGlyph Glyph, string? Value, string? Label, Action? Run)> items)
    {
        var rows = items.Where(item => !string.IsNullOrWhiteSpace(item.Value)).ToList();
        if (rows.Count == 0) return;
        var header = WinoTokens.Style(new TextView(context) { Text = title }, WinoTextStyle.Subheader);
        header.SetTextColor(WinoColors.AccentText(context));
        header.Gravity = GravityFlags.CenterVertical;
        header.SetMinHeight(WinoTokens.Px(context, Resource.Dimension.wino_m3_subheader_height));
        WinoTokens.Pad(header, Resource.Dimension.wino_people_card_padding, Resource.Dimension.wino_space_s, Resource.Dimension.wino_people_card_padding, 0);
        AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(header, true);
        host.AddView(header, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        foreach (var (glyph, value, label, run) in rows) host.AddView(Item(context, glyph, value ?? string.Empty, label, run));
    }

    /// <summary>A Material 3 list item: the glyph in a 56 dp leading column, the value (body-large) over its label.</summary>
    private static View Item(Context context, WinoIconGlyph glyph, string value, string? label, Action? run)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_people_item_min_height));
        WinoTokens.Pad(row, Resource.Dimension.wino_people_card_padding, Resource.Dimension.wino_space_s, Resource.Dimension.wino_people_card_padding, Resource.Dimension.wino_space_s);
        var icon = new WinoIconView(context) { Icon = glyph, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph) };
        row.AddView(icon, new LinearLayout.LayoutParams(WinoTokens.Px(context, Resource.Dimension.wino_people_item_leading) - WinoTokens.Px(context, Resource.Dimension.wino_people_card_padding),
            ViewGroup.LayoutParams.WrapContent));
        var text = new LinearLayout(context) { Orientation = Orientation.Vertical };
        var primary = WinoTokens.Style(new TextView(context) { Text = value }, WinoTextStyle.ItemTitle);
        primary.SetTextIsSelectable(run is null);
        text.AddView(primary);
        if (!string.IsNullOrWhiteSpace(label))
        {
            var secondary = WinoTokens.Style(new TextView(context) { Text = label }, WinoTextStyle.ItemSupporting);
            secondary.SetMaxLines(1);
            secondary.Ellipsize = TextUtils.TruncateAt.End;
            text.AddView(secondary);
        }
        row.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        if (run is not null)
        {
            row.Clickable = true;
            row.Focusable = true;
            row.Background = AndroidX.Core.Content.ContextCompat.GetDrawable(context, ShellViews.Ripple(context));
            row.Click += (_, _) => run();
        }
        row.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        return row;
    }
}
