using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Android.Content;
using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Widget;
using AndroidX.Activity;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Chip;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.TextField;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;

namespace Wino.Mail.Android.Views.Contacts;

/// <summary>
/// The contact editor (Windows ContactEditPage, Mac and iOS ContactEditPageViewController; canvas Material pass
/// ContactEditor) over the shared <see cref="ContactEditPageViewModel"/>, as a Material 3 full-screen dialog on every
/// width: Close, the title and the filled Save button; the hero with the photo (Change photo and Remove), the preview name
/// and the favourite star; the Windows section switcher as single-select chips; then the section's outlined fields, in
/// the Windows order (Save to is the first field, as on Windows, so the board's foot line is left out). Close and system back ask the shared discard question when there are changes; Save and Close leave
/// through the shared coordinator's back, which closes this window-host page. After saving an existing contact the
/// contacts page selects it again (the Mac PendingSelection).
/// </summary>
public sealed class ContactEditorFragment : WinoFragment<ContactEditPageViewModel>
{
    private readonly List<(ContactEditorCategory Category, View View)> _sections = [];
    private readonly List<(ContactEditorCategory Category, Chip Chip)> _categoryChips = [];
    private ContactsPageViewModel? _contacts;
    private INavigationService? _navigation;
    private LinearLayout? _form;
    private WinoContactPicture? _picture;
    private TextView? _previewName;
    private TextView? _previewSubtitle;
    private WinoIconView? _favorite;
    private WinoInfoBar? _error;
    private Guid? _contactId;
    private bool _wasSaving;
    private bool _leaving;

    protected override int LayoutResource => Resource.Layout.fragment_contact_editor;

    protected override void OnBind(View view, ContactEditPageViewModel viewModel, IDispatcher dispatcher)
    {
        var services = MainApplication.Services ?? throw new InvalidOperationException("The application services are not ready.");
        var context = view.RequireContext();
        _contacts = services.GetService<ContactsPageViewModel>();
        _navigation = services.GetService<INavigationService>();
        _contactId = (Entry?.Parameter as ContactEditNavigationParameter)?.ContactId;
        WinoInsets.ApplyWithKeyboard(view.FindViewById(Resource.Id.contact_editor_root) ?? view, top: true);
        if (view.FindViewById<WinoMaxWidthLayout>(Resource.Id.contact_editor_column) is { } column)
            column.MaxWidthDp = WinoTokens.Dp(context, Resource.Dimension.wino_people_editor_max_width);

        BuildToolbar(view, context, viewModel, dispatcher);
        _form = view.FindViewById<LinearLayout>(Resource.Id.contact_editor_form);
        if (_form is null) return;
        _form.AddView(BuildHero(context, viewModel, dispatcher));
        _form.AddView(BuildSwitcher(context, viewModel));
        _error = new WinoInfoBar(context) { Severity = WinoInfoBarSeverity.Error, Title = Translator.ContactInfoBar_ErrorTitle, IsClosable = true };
        _error.Closed += (_, _) => viewModel.IsErrorOpen = false;
        _form.AddView(_error, Margins(context, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)));
        BuildContactInformation(context, viewModel, dispatcher);
        BuildWork(context, viewModel, dispatcher);
        BuildAddress(context, viewModel);
        BuildOther(context, viewModel, dispatcher);
        BuildNotes(context, viewModel, dispatcher);

        Bind(viewModel, dispatcher, nameof(ContactEditPageViewModel.SelectedCategory), static vm => vm.SelectedCategory, category =>
        {
            foreach (var (sectionCategory, section) in _sections) section.Visibility = sectionCategory == category ? ViewStates.Visible : ViewStates.Gone;
            foreach (var (chipCategory, chip) in _categoryChips) chip.Checked = chipCategory == category;
        });
        Bind(viewModel, dispatcher, nameof(ContactEditPageViewModel.IsErrorOpen), static vm => vm.IsErrorOpen, open => { if (_error is not null) _error.IsOpen = open; });
        Bind(viewModel, dispatcher, nameof(ContactEditPageViewModel.ErrorMessage), static vm => vm.ErrorMessage, message => { if (_error is not null) _error.Message = message; });
        Bind(viewModel, dispatcher, nameof(ContactEditPageViewModel.IsSaving), static vm => vm.IsSaving, saving =>
        {
            // The router keeps NavigationResult to itself here; bring the saved contact into view like the Mac does.
            if (_wasSaving && !saving && !viewModel.IsErrorOpen && !viewModel.IsDirty && _contactId is { } id && _contacts is { } contacts)
                PeopleViews.Observe(contacts.LoadAndSelectContactAsync(id), ReportError);
            _wasSaving = saving;
        });

        // System back asks the discard question first (the coordinator's back would close the page directly).
        var back = new BackCallback(() => PeopleViews.Observe(CancelAsync(), ReportError));
        RequireActivity().OnBackPressedDispatcher.AddCallback(ViewLifecycleOwner, back);
        Bindings.Own(new ActionDisposable(back.Remove));
    }

    private sealed class BackCallback(Action handle) : OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed() => handle();
    }

    private async Task CancelAsync()
    {
        if (_leaving || ViewModel is not { } viewModel || _navigation is not { } navigation) return;
        if (!await viewModel.CanNavigateBackAsync()) return;
        // The ViewModel's dirty guard already ran; leave without asking again.
        _leaving = true;
        viewModel.IsDirty = false;
        await navigation.GoBackAsync();
    }

    // ---- Top app bar: Close, title, Save ----

    private void BuildToolbar(View view, Context context, ContactEditPageViewModel viewModel, IDispatcher dispatcher)
    {
        if (view.FindViewById<MaterialToolbar>(Resource.Id.contact_editor_toolbar) is { } toolbar)
        {
            toolbar.Title = _contactId is null ? Translator.ContactEditDialog_AddTitle : Translator.ContactEditDialog_Title;
            toolbar.SetNavigationIcon(Resource.Drawable.wino_ic_close);
            toolbar.NavigationContentDescription = Translator.Buttons_Close;
            toolbar.NavigationClick += (_, _) => PeopleViews.Observe(CancelAsync(), ReportError);
        }
        if (view.FindViewById<MaterialButton>(Resource.Id.contact_editor_save) is { } save)
        {
            save.Text = Translator.Buttons_Save;
            WinoMaterialAccent.Tint(save, filled: true);
            BindCommand(save, viewModel.SaveCommand, dispatcher);
        }
    }

    // ---- Hero: photo, preview name, favourite ----

    private View BuildHero(Context context, ContactEditPageViewModel viewModel, IDispatcher dispatcher)
    {
        var hero = new LinearLayout(context) { Orientation = Orientation.Vertical };
        hero.SetGravity(GravityFlags.CenterHorizontal);
        WinoTokens.Pad(hero, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_s);
        _picture = new WinoContactPicture(context) { Size = WinoTokens.Dp(context, Resource.Dimension.wino_people_editor_avatar) };
        hero.AddView(_picture);

        var photo = new MaterialButton(context, null, Resource.Attribute.borderlessButtonStyle) { Text = Translator.ContactAction_ChangePhoto, ContentDescription = Translator.ContactAction_PhotoOptions };
        WinoMaterialAccent.Tint(photo, filled: false);
        photo.Click += (_, _) =>
        {
            var popup = new PopupMenu(context, photo);
            popup.Menu.Add(0, 1, 0, Translator.ContactAction_ChangePhoto);
            popup.Menu.Add(0, 2, 1, viewModel.RemovePhotoLabel ?? Translator.ContactEditDialog_RemovePhoto);
            popup.MenuItemClick += (_, args) =>
            {
                if (args.Item?.ItemId == 1) PeopleViews.Observe(viewModel.ChoosePhotoCommand.ExecuteAsync(null), ReportError);
                else if (args.Item?.ItemId == 2) viewModel.RemovePhotoCommand.Execute(null);
            };
            popup.Show();
        };
        hero.AddView(photo);

        var nameRow = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        nameRow.SetGravity(GravityFlags.Center);
        var names = new LinearLayout(context) { Orientation = Orientation.Vertical };
        names.SetGravity(GravityFlags.CenterHorizontal);
        _previewName = WinoTokens.Style(new TextView(context) { Gravity = GravityFlags.Center }, WinoTextStyle.Title);
        _previewSubtitle = WinoTokens.Style(new TextView(context) { Gravity = GravityFlags.Center }, WinoTextStyle.BodySecondary);
        names.AddView(_previewName);
        names.AddView(_previewSubtitle);
        nameRow.AddView(names, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        _favorite = new WinoIconView(context) { GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph), Clickable = true, Focusable = true, ImportantForAccessibility = ImportantForAccessibility.Yes };
        _favorite.Background = AndroidX.Core.Content.ContextCompat.GetDrawable(context, Shell.ShellViews.BorderlessRipple(context));
        _favorite.Click += (_, _) => { if (viewModel.ToggleFavoriteCommand.CanExecute(null)) PeopleViews.Observe(viewModel.ToggleFavoriteCommand.ExecuteAsync(null), ReportError); };
        int touch = WinoTokens.Px(context, Resource.Dimension.wino_m3_icon_button);
        nameRow.AddView(_favorite, new LinearLayout.LayoutParams(touch, touch));
        hero.AddView(nameRow);

        Bind(viewModel, dispatcher, nameof(ContactEditPageViewModel.PreviewDisplayName), static vm => vm.PreviewDisplayName, value =>
        {
            _previewName.Text = value ?? string.Empty;
            _picture.SetIdentity(value, viewModel.EmailAddresses.FirstOrDefault()?.Address ?? value);
        });
        Bind(viewModel, dispatcher, nameof(ContactEditPageViewModel.PreviewSubtitle), static vm => vm.PreviewSubtitle, value =>
        {
            _previewSubtitle.Text = value ?? string.Empty;
            _previewSubtitle.Visibility = string.IsNullOrWhiteSpace(value) ? ViewStates.Gone : ViewStates.Visible;
        });
        Bind(viewModel, dispatcher, nameof(ContactEditPageViewModel.PreviewPhotoBytes), static vm => vm.PreviewPhotoBytes, _ => ApplyPhoto(viewModel));
        Bind(viewModel, dispatcher, nameof(ContactEditPageViewModel.PreviewPhotoPath), static vm => vm.PreviewPhotoPath, _ => ApplyPhoto(viewModel));
        Bind(viewModel, dispatcher, nameof(ContactEditPageViewModel.IsFavorite), static vm => vm.IsFavorite, favorite =>
        {
            _favorite.Icon = favorite ? WinoIconGlyph.StarFilled : WinoIconGlyph.Star;
            _favorite.SetTextColor(favorite ? WinoColors.Caution(context) : WinoColors.SecondaryText(context));
            _favorite.ContentDescription = favorite ? Translator.ContactAction_Unfavorite : Translator.ContactAction_Favorite;
        });
        return hero;
    }

    private void ApplyPhoto(ContactEditPageViewModel viewModel)
    {
        if (_picture is null) return;
        Bitmap? image = null;
        try
        {
            if (viewModel.PreviewPhotoBytes is { Length: > 0 } bytes) image = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
            else if (!string.IsNullOrEmpty(viewModel.PreviewPhotoPath) && File.Exists(viewModel.PreviewPhotoPath)) image = BitmapFactory.DecodeFile(viewModel.PreviewPhotoPath);
        }
        catch (Exception exception) { ReportError(exception); }
        _picture.Image = image;
    }

    // ---- Section switcher (Windows SelectorBar) ----

    private View BuildSwitcher(Context context, ContactEditPageViewModel viewModel)
    {
        var scroll = new HorizontalScrollView(context) { HorizontalScrollBarEnabled = false };
        var group = new ChipGroup(context) { SingleLine = true, SingleSelection = true, SelectionRequired = true };
        group.ChipSpacingHorizontal = WinoTokens.Px(context, Resource.Dimension.wino_m3_chip_spacing);
        WinoTokens.Pad(group, Resource.Dimension.wino_space_l, 0, Resource.Dimension.wino_space_l, 0);
        (ContactEditorCategory Category, string Title)[] categories =
        [
            (ContactEditorCategory.ContactInformation, Translator.ContactEditor_CategoryContactInformation),
            (ContactEditorCategory.Work, Translator.ContactEditor_CategoryWork),
            (ContactEditorCategory.Address, Translator.ContactEditor_CategoryAddress),
            (ContactEditorCategory.Other, Translator.ContactEditor_CategoryOther),
            (ContactEditorCategory.Notes, Translator.ContactEditor_CategoryNotes)
        ];
        foreach (var (category, title) in categories)
        {
            var chip = WinoChips.Filter(context, title, viewModel.SelectedCategory == category);
            chip.Click += (_, _) => viewModel.SelectedCategory = category;
            group.AddView(chip);
            _categoryChips.Add((category, chip));
        }
        scroll.AddView(group);
        scroll.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, WinoTokens.Px(context, Resource.Dimension.wino_m3_chip_row_height));
        return scroll;
    }

    // ---- Sections (Windows order) ----

    private void BuildContactInformation(Context context, ContactEditPageViewModel vm, IDispatcher dispatcher)
    {
        var section = Section(context, ContactEditorCategory.ContactInformation);

        // Save to: the destination dropdown for a new contact, the source line when editing.
        var destination = SettingsBinder.CreateDropdown(context, 0);
        void RebuildDestination() => destination.Text = vm.SelectedDestination?.DisplayName ?? Translator.ContactEditor_SaveTo;
        destination.Click += (_, _) =>
        {
            var items = vm.Destinations.ToList();
            WinoChoiceSheet.Show(context, Translator.ContactEditor_SaveTo, items.Select(item => item.DisplayName ?? string.Empty).ToList(),
                items.FindIndex(item => item.AddressBookId == vm.SelectedDestination?.AddressBookId), index => vm.SelectedDestination = items[index]);
        };
        var source = WinoTokens.Style(new TextView(context), WinoTextStyle.BodySecondary);
        Collection(vm.Destinations, RebuildDestination);
        Bind(vm, dispatcher, nameof(vm.SelectedDestination), static v => v.SelectedDestination, _ => RebuildDestination());
        Bind(vm, dispatcher, nameof(vm.SourceDescription), static v => v.SourceDescription, value => source.Text = value ?? string.Empty);
        Bind(vm, dispatcher, nameof(vm.IsEditMode), static v => v.IsEditMode, edit =>
        {
            destination.Visibility = edit ? ViewStates.Gone : ViewStates.Visible;
            source.Visibility = edit ? ViewStates.Visible : ViewStates.Gone;
        });
        Line(section, WinoIconGlyph.People, Heading(context, Translator.ContactEditor_SaveTo), destination, source);

        Line(section, WinoIconGlyph.Person,
            Field(context, Translator.ContactEditor_GivenName, nameof(vm.GivenName), static v => v.GivenName, static (v, s) => v.GivenName = s, InputTypes.TextVariationPersonName | InputTypes.TextFlagCapWords),
            Field(context, Translator.ContactEditor_Surname, nameof(vm.Surname), static v => v.Surname, static (v, s) => v.Surname = s, InputTypes.TextVariationPersonName | InputTypes.TextFlagCapWords),
            Field(context, Translator.ContactEditor_DisplayName, nameof(vm.DisplayName), static v => v.DisplayName, static (v, s) => v.DisplayName = s, InputTypes.TextVariationPersonName | InputTypes.TextFlagCapWords),
            Field(context, Translator.ContactEditor_Nickname, nameof(vm.Nickname), static v => v.Nickname, static (v, s) => v.Nickname = s, InputTypes.TextFlagCapWords),
            Field(context, Translator.ContactEditor_FileAs, nameof(vm.FileAs), static v => v.FileAs, static (v, s) => v.FileAs = s, InputTypes.TextFlagCapWords));

        var emails = Rows(context, vm.EmailAddresses, email => Pair(context,
            EntityField(context, Translator.ContactEditor_Email, () => email.Address, value => email.Address = value, InputTypes.TextVariationEmailAddress),
            EntityField(context, Translator.ContactEditor_EmailLabel, () => email.Label, value => email.Label = value, InputTypes.TextFlagCapWords, narrow: true),
            RemoveButton(context, () => vm.RemoveEmailCommand.Execute(email))));
        var addEmail = TextButton(context, Translator.ContactEditor_AddEmail, () => vm.AddEmailCommand.Execute(null));
        Collection(vm.EmailAddresses, () => addEmail.Visibility = vm.EmailAddresses.Count >= 3 ? ViewStates.Gone : ViewStates.Visible);
        Line(section, WinoIconGlyph.Mail, emails, addEmail);

        var phones = Rows(context, vm.PhoneNumbers, phone =>
        {
            var kind = SettingsBinder.CreateDropdown(context, 0);
            kind.Text = phone.Kind.ToString();
            kind.ContentDescription = Translator.ContactEditor_PhoneKind;
            kind.Click += (_, _) =>
            {
                var kinds = Enum.GetValues<ContactPhoneKind>();
                WinoChoiceSheet.Show(context, Translator.ContactEditor_PhoneKind, kinds.Select(value => value.ToString()).ToList(), Array.IndexOf(kinds, phone.Kind), index =>
                {
                    phone.Kind = kinds[index];
                    kind.Text = phone.Kind.ToString();
                    vm.MarkDirty();
                });
            };
            return Pair(context, EntityField(context, Translator.ContactEditor_Phone, () => phone.Number, value => phone.Number = value, InputTypes.ClassPhone), kind,
                RemoveButton(context, () => vm.RemovePhoneCommand.Execute(phone)));
        });
        Line(section, WinoIconGlyph.Phone, phones, TextButton(context, Translator.ContactEditor_AddPhone, () => vm.AddPhoneCommand.Execute(null)));

        var lists = Rows(context, vm.ListMemberships, membership => Switch(context, membership.Name, () => membership.IsMember, value => membership.IsMember = value));
        var listsLine = Line(section, WinoIconGlyph.List, Heading(context, Translator.ContactEditor_Lists), lists);
        Collection(vm.ListMemberships, () => listsLine.Visibility = vm.HasLists ? ViewStates.Visible : ViewStates.Gone);

        var categories = Rows(context, vm.CategoryMemberships, membership => Switch(context, membership.Name, () => membership.IsMember, value => membership.IsMember = value));
        var categoriesLine = Line(section, WinoIconGlyph.Tag, Heading(context, Translator.SettingsMailCategories_Title), categories);
        Collection(vm.CategoryMemberships, () => categoriesLine.Visibility = vm.HasCategories ? ViewStates.Visible : ViewStates.Gone);
    }

    private void BuildWork(Context context, ContactEditPageViewModel vm, IDispatcher dispatcher)
    {
        var section = Section(context, ContactEditorCategory.Work);
        Line(section, WinoIconGlyph.Briefcase,
            Field(context, Translator.ContactEditor_Company, nameof(vm.CompanyName), static v => v.CompanyName, static (v, s) => v.CompanyName = s, InputTypes.TextFlagCapWords),
            Field(context, Translator.ContactEditor_Department, nameof(vm.Department), static v => v.Department, static (v, s) => v.Department = s, InputTypes.TextFlagCapWords),
            Field(context, Translator.ContactEditor_JobTitle, nameof(vm.JobTitle), static v => v.JobTitle, static (v, s) => v.JobTitle = s, InputTypes.TextFlagCapWords),
            Field(context, Translator.ContactEditor_Office, nameof(vm.OfficeLocation), static v => v.OfficeLocation, static (v, s) => v.OfficeLocation = s, InputTypes.TextFlagCapWords));
        Line(section, WinoIconGlyph.People,
            Field(context, Translator.ContactEditor_Manager, nameof(vm.ManagerName), static v => v.ManagerName, static (v, s) => v.ManagerName = s, InputTypes.TextFlagCapWords),
            Field(context, Translator.ContactEditor_Assistant, nameof(vm.AssistantName), static v => v.AssistantName, static (v, s) => v.AssistantName = s, InputTypes.TextFlagCapWords));
    }

    private void BuildAddress(Context context, ContactEditPageViewModel vm)
    {
        var section = Section(context, ContactEditorCategory.Address);
        var addresses = Rows(context, vm.PostalAddresses, address =>
        {
            var column = Column(context);
            column.AddView(Heading(context, address.Kind.ToString()));
            column.AddView(EntityField(context, Translator.ContactEditor_Street, () => address.Street, value => address.Street = value, InputTypes.TextVariationPostalAddress | InputTypes.TextFlagCapWords));
            column.AddView(EntityField(context, Translator.ContactEditor_City, () => address.City, value => address.City = value, InputTypes.TextFlagCapWords));
            column.AddView(EntityField(context, Translator.ContactEditor_Region, () => address.Region, value => address.Region = value, InputTypes.TextFlagCapWords));
            column.AddView(EntityField(context, Translator.ContactEditor_PostalCode, () => address.PostalCode, value => address.PostalCode = value, InputTypes.TextFlagCapCharacters));
            column.AddView(EntityField(context, Translator.ContactEditor_Country, () => address.Country, value => address.Country = value, InputTypes.TextFlagCapWords));
            return column;
        });
        var empty = WinoTokens.Style(new TextView(context) { Text = Translator.ContactEditor_AddressesDescription }, WinoTextStyle.BodySecondary);
        Collection(vm.PostalAddresses, () => empty.Visibility = vm.PostalAddresses.Count == 0 ? ViewStates.Visible : ViewStates.Gone);
        Line(section, WinoIconGlyph.Location, Heading(context, Translator.ContactEditor_Addresses), empty, addresses);
    }

    private void BuildOther(Context context, ContactEditPageViewModel vm, IDispatcher dispatcher)
    {
        var section = Section(context, ContactEditorCategory.Other);
        var birthday = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        foreach (var field in new[]
        {
            IntField(context, Translator.ContactEditor_BirthDay, nameof(vm.BirthdayDay), static v => v.BirthdayDay, static (v, i) => v.BirthdayDay = i),
            IntField(context, Translator.ContactEditor_BirthMonth, nameof(vm.BirthdayMonth), static v => v.BirthdayMonth, static (v, i) => v.BirthdayMonth = i),
            IntField(context, Translator.ContactEditor_BirthYear, nameof(vm.BirthdayYear), static v => v.BirthdayYear, static (v, i) => v.BirthdayYear = i)
        })
        {
            birthday.AddView(field, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { MarginEnd = WinoTokens.Px(context, Resource.Dimension.wino_space_s) });
        }
        Line(section, WinoIconGlyph.CalendarToday, Heading(context, Translator.ContactEditor_Birthday), birthday);
        Line(section, WinoIconGlyph.Globe, Field(context, Translator.ContactEditor_Website, nameof(vm.Website), static v => v.Website, static (v, s) => v.Website = s, InputTypes.TextVariationUri));

        var ims = Rows(context, vm.ImAddresses, im => Pair(context,
            EntityField(context, Translator.ContactEditor_InstantMessaging, () => im.Address, value => im.Address = value, InputTypes.TextVariationEmailAddress),
            RemoveButton(context, () => vm.RemoveImAddressCommand.Execute(im))));
        Line(section, WinoIconGlyph.Globe, ims, TextButton(context, Translator.ContactEditor_AddIm, () => vm.AddImAddressCommand.Execute(null)));

        var children = Rows(context, vm.Relations, relation => Pair(context,
            EntityField(context, Translator.ContactEditor_Child, () => relation.Name, value => relation.Name = value, InputTypes.TextFlagCapWords),
            RemoveButton(context, () => vm.RemoveRelationCommand.Execute(relation))));
        Line(section, WinoIconGlyph.People,
            Field(context, Translator.ContactEditor_Spouse, nameof(vm.SpouseName), static v => v.SpouseName, static (v, s) => v.SpouseName = s, InputTypes.TextFlagCapWords),
            children, TextButton(context, Translator.ContactEditor_AddChild, () => vm.AddChildCommand.Execute(null)));
    }

    private void BuildNotes(Context context, ContactEditPageViewModel vm, IDispatcher dispatcher)
    {
        var section = Section(context, ContactEditorCategory.Notes);
        var notes = SettingsBinder.CreateTextField(context, Translator.ContactEditor_Notes, multiline: true);
        notes.EditText?.SetMinHeight(WinoTokens.Px(context, Resource.Dimension.wino_people_editor_notes_min_height));
        BindText(notes, vm, nameof(vm.Notes), static v => v.Notes, static (v, s) => v.Notes = s);
        Line(section, WinoIconGlyph.Note, notes);
    }

    // ---- Form helpers ----

    private LinearLayout Section(Context context, ContactEditorCategory category)
    {
        var section = Column(context);
        WinoTokens.Pad(section, 0, Resource.Dimension.wino_space_s, 0, 0);
        _sections.Add((category, section));
        _form?.AddView(section, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        return section;
    }

    private static LinearLayout Column(Context context)
    {
        var column = new LinearLayout(context) { Orientation = Orientation.Vertical };
        column.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        return column;
    }

    /// <summary>One editor line (board ContactEditor): the 24 dp glyph in the leading column, then the fields stacked.</summary>
    private static View Line(LinearLayout section, WinoIconGlyph glyph, params View[] content)
    {
        var context = section.RequireContext();
        var line = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        WinoTokens.Pad(line, Resource.Dimension.wino_space_l, Resource.Dimension.wino_people_editor_line_gap, Resource.Dimension.wino_space_l, 0);
        var icon = new WinoIconView(context) { Icon = glyph, GlyphSize = WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph) };
        icon.Gravity = GravityFlags.Top | GravityFlags.CenterHorizontal;
        int leading = WinoTokens.Px(context, Resource.Dimension.wino_people_item_leading) - WinoTokens.Px(context, Resource.Dimension.wino_space_l);
        icon.SetPadding(0, WinoTokens.Px(context, Resource.Dimension.wino_space_l), leading - WinoTokens.Px(context, Resource.Dimension.wino_m3_icon), 0);
        line.AddView(icon, new LinearLayout.LayoutParams(leading, ViewGroup.LayoutParams.WrapContent));
        var column = Column(context);
        foreach (var view in content)
        {
            var parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            if (column.ChildCount > 0) parameters.TopMargin = WinoTokens.Px(context, Resource.Dimension.wino_people_editor_field_gap);
            if (view.LayoutParameters is LinearLayout.LayoutParams existing && existing.Width == ViewGroup.LayoutParams.WrapContent) parameters.Width = ViewGroup.LayoutParams.WrapContent;
            column.AddView(view, parameters);
        }
        line.AddView(column, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        section.AddView(line, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        return line;
    }

    private static TextView Heading(Context context, string text)
    {
        var heading = WinoTokens.Style(new TextView(context) { Text = text }, WinoTextStyle.BodyStrong);
        WinoTokens.Pad(heading, 0, Resource.Dimension.wino_space_m, 0, 0);
        AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(heading, true);
        return heading;
    }

    /// <summary>Fields side by side with an optional trailing button (email + label, phone + type).</summary>
    private static LinearLayout Pair(Context context, params View[] views)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        for (int index = 0; index < views.Length; index++)
        {
            var view = views[index];
            bool fill = index == 0;
            var parameters = fill
                ? new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1)
                : new LinearLayout.LayoutParams(view is TextInputLayout ? WinoTokens.Px(context, Resource.Dimension.wino_people_editor_label_width) : ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            if (index > 0) parameters.MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_s);
            row.AddView(view, parameters);
        }
        return row;
    }

    private TextInputLayout Field(Context context, string hint, string property, Func<ContactEditPageViewModel, string?> read, Action<ContactEditPageViewModel, string> write, InputTypes input)
    {
        var layout = SettingsBinder.CreateTextField(context, hint);
        if (layout.EditText is { } field) field.InputType = InputTypes.ClassText | input;
        if (ViewModel is { } viewModel) BindText(layout, viewModel, property, read, write);
        return layout;
    }

    private void BindText(TextInputLayout layout, ContactEditPageViewModel viewModel, string property, Func<ContactEditPageViewModel, string?> read, Action<ContactEditPageViewModel, string> write)
    {
        if (layout.EditText is not { } field || Entry is not { } entry) return;
        var binding = Bindings.Own(new PropertyBinding<ContactEditPageViewModel, string?>(viewModel, property, read,
            value => { if (field.Text != (value ?? string.Empty)) field.Text = value ?? string.Empty; }, entry.Dispatcher, ReportError,
            (vm, text) => write(vm, text ?? string.Empty)));
        EventHandler<TextChangedEventArgs> changed = (_, _) =>
        {
            var text = field.Text ?? string.Empty;
            if (text == (read(viewModel) ?? string.Empty)) return;
            binding.UpdateSource(text);
            viewModel.MarkDirty();
        };
        field.TextChanged += changed;
        Bindings.Own(new ActionDisposable(() => field.TextChanged -= changed));
    }

    private TextInputLayout IntField(Context context, string hint, string property, Func<ContactEditPageViewModel, int?> read, Action<ContactEditPageViewModel, int?> write)
    {
        var layout = SettingsBinder.CreateTextField(context, hint);
        if (layout.EditText is not { } field || ViewModel is not { } viewModel || Entry is not { } entry) return layout;
        field.InputType = InputTypes.ClassNumber;
        var binding = Bindings.Own(new PropertyBinding<ContactEditPageViewModel, int?>(viewModel, property, read,
            value => { var text = value?.ToString() ?? string.Empty; if (field.Text != text) field.Text = text; }, entry.Dispatcher, ReportError, write));
        EventHandler<TextChangedEventArgs> changed = (_, _) =>
        {
            int? value = int.TryParse(field.Text, out var parsed) ? parsed : null;
            if (value == read(viewModel)) return;
            binding.UpdateSource(value);
            viewModel.MarkDirty();
        };
        field.TextChanged += changed;
        Bindings.Own(new ActionDisposable(() => field.TextChanged -= changed));
        return layout;
    }

    /// <summary>A field over a plain entity (email, phone, address rows); edits mark the editor dirty.</summary>
    private TextInputLayout EntityField(Context context, string hint, Func<string?> get, Action<string> set, InputTypes input, bool narrow = false)
    {
        var layout = SettingsBinder.CreateTextField(context, hint);
        if (layout.EditText is { } field)
        {
            field.InputType = InputTypes.ClassText | input;
            if (input == InputTypes.ClassPhone) field.InputType = InputTypes.ClassPhone;
            field.Text = get() ?? string.Empty;
            field.TextChanged += (_, _) =>
            {
                set(field.Text ?? string.Empty);
                ViewModel?.MarkDirty();
            };
        }
        return layout;
    }

    private View Switch(Context context, string? title, Func<bool> get, Action<bool> set)
    {
        var toggle = new MaterialSwitch(context) { Text = title ?? string.Empty, Checked = get() };
        WinoTokens.Style(toggle, WinoTextStyle.BodyLarge);
        WinoMaterialAccent.Tint(toggle);
        toggle.CheckedChange += (_, args) =>
        {
            set(args.IsChecked);
            ViewModel?.MarkDirty();
        };
        return toggle;
    }

    private static MaterialButton RemoveButton(Context context, Action remove)
    {
        var button = new MaterialButton(context, null, Resource.Attribute.borderlessButtonStyle) { ContentDescription = Translator.Buttons_Remove };
        button.Icon = WinoIcons.Drawable(context, WinoIconGlyph.Dismiss, WinoTokens.Dp(context, Resource.Dimension.wino_glyph_s), WinoColors.SecondaryText(context));
        button.IconPadding = 0;
        button.SetMinWidth(WinoTokens.Px(context, Resource.Dimension.wino_m3_icon_button));
        button.SetMinimumWidth(WinoTokens.Px(context, Resource.Dimension.wino_m3_icon_button));
        button.Click += (_, _) => remove();
        return button;
    }

    private static MaterialButton TextButton(Context context, string title, Action run)
    {
        var button = new MaterialButton(context, null, Resource.Attribute.borderlessButtonStyle) { Text = title };
        WinoMaterialAccent.Tint(button, filled: false);
        button.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        button.Click += (_, _) => run();
        return button;
    }

    /// <summary>A vertical list rebuilt from an observable collection.</summary>
    private LinearLayout Rows<T>(Context context, ObservableCollection<T> items, Func<T, View> build) where T : class
    {
        var stack = Column(context);
        void Rebuild()
        {
            stack.RemoveAllViews();
            foreach (var item in items.ToList())
            {
                var parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                if (stack.ChildCount > 0) parameters.TopMargin = WinoTokens.Px(context, Resource.Dimension.wino_people_editor_field_gap);
                stack.AddView(build(item), parameters);
            }
            stack.Visibility = stack.ChildCount == 0 ? ViewStates.Gone : ViewStates.Visible;
        }
        Collection(items, Rebuild);
        return stack;
    }

    private void Collection(INotifyCollectionChanged collection, Action rebuild)
    {
        NotifyCollectionChangedEventHandler handler = (_, _) =>
        {
            if (Entry is { } entry) _ = entry.Dispatcher.ExecuteOnUIThread(() => { if (View is not null) rebuild(); });
        };
        collection.CollectionChanged += handler;
        Bindings.Own(new ActionDisposable(() => collection.CollectionChanged -= handler));
        rebuild();
    }

    private static LinearLayout.LayoutParams Margins(Context context, LinearLayout.LayoutParams parameters)
    {
        int gutter = WinoTokens.Px(context, Resource.Dimension.wino_space_l);
        parameters.SetMargins(gutter, WinoTokens.Px(context, Resource.Dimension.wino_space_s), gutter, 0);
        return parameters;
    }
}
