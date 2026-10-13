using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Contacts;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Contacts;

/// <summary>
/// The contact editor (Windows ContactEditPage, Mac ContactEditPageViewController) as a sheet: a full-screen sheet on
/// iPhone and a form sheet on iPad. Cancel and Save sit in the sheet's bar; the hero shows the photo (with Change and
/// Remove), the preview name and the favourite star; a section switcher (Contact information, Work, Address, Other,
/// Notes) selects the form below. Save and Cancel leave through the router's back, which
/// closes a single-page sheet (<see cref="UIKitNavigationService"/>).
/// </summary>
public sealed class ContactEditPageViewController : WinoViewController<ContactEditPageViewModel>
{
    private readonly UIKitNavigationService _navigation;
    private readonly ContactsPageViewModel _contacts;
    private readonly List<(ContactEditorCategory Category, UIView View)> _sections = new();
    private readonly bool _compact = UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Phone;
    private readonly WinoContactPicture _picture = new(72);
    private readonly UILabel _previewName = WinoStyle.Label(string.Empty, WinoStyle.Heading);
    private readonly UILabel _previewSubtitle = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText);
    private readonly UIButton _favorite = new(UIButtonType.System);
    private readonly UISegmentedControl _switcher = new();
    private readonly WinoInfoBar _error = new(WinoInfoBarSeverity.Error, Translator.ContactInfoBar_ErrorTitle) { Hidden = true };
    private readonly UIScrollView _scroll = new() { TranslatesAutoresizingMaskIntoConstraints = false, KeyboardDismissMode = UIScrollViewKeyboardDismissMode.Interactive };
    private UIBarButtonItem? _save;
    private Guid? _contactId;
    private bool _wasSaving;
    private bool _released;

    public ContactEditPageViewController(ContactEditPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger,
        UIKitNavigationService navigation, ContactsPageViewModel contacts) : base(viewModel, dispatcher, logger)
    {
        _navigation = navigation;
        _contacts = contacts;
    }

    public override void LoadView()
    {
        var root = new UIView { BackgroundColor = UIColor.SystemGroupedBackground };
        var hero = BuildHero();

        string[] titles =
        [
            Translator.ContactEditor_CategoryContactInformation, Translator.ContactEditor_CategoryWork,
            Translator.ContactEditor_CategoryAddress, Translator.ContactEditor_CategoryOther, Translator.ContactEditor_CategoryNotes
        ];
        for (int index = 0; index < titles.Length; index++) _switcher.InsertSegment(titles[index], index, false);
        _switcher.ApportionsSegmentWidthsByContent = true;
        _switcher.TranslatesAutoresizingMaskIntoConstraints = false;
        _switcher.ValueChanged += (_, _) => ViewModel.SelectedCategory = (ContactEditorCategory)(int)_switcher.SelectedSegment;
        // Five long labels do not fit an iPhone: the switcher scrolls sideways.
        var switcherScroll = new UIScrollView { TranslatesAutoresizingMaskIntoConstraints = false, ShowsHorizontalScrollIndicator = false };
        switcherScroll.AddSubview(_switcher);
        NSLayoutConstraint.ActivateConstraints(
        [
            _switcher.LeadingAnchor.ConstraintEqualTo(switcherScroll.ContentLayoutGuide.LeadingAnchor, 16),
            _switcher.TrailingAnchor.ConstraintEqualTo(switcherScroll.ContentLayoutGuide.TrailingAnchor, -16),
            _switcher.TopAnchor.ConstraintEqualTo(switcherScroll.ContentLayoutGuide.TopAnchor, 10),
            _switcher.BottomAnchor.ConstraintEqualTo(switcherScroll.ContentLayoutGuide.BottomAnchor, -6),
            _switcher.HeightAnchor.ConstraintEqualTo(switcherScroll.FrameLayoutGuide.HeightAnchor, 1, -16),
            switcherScroll.HeightAnchor.ConstraintEqualTo(48)
        ]);

        _error.IsClosable = true;
        _error.Closed += (_, _) => ViewModel.IsErrorOpen = false;

        var form = WinoLayout.VStack(0, _error, BuildContactInformation(), BuildWork(), BuildAddress(), BuildOther(), BuildNotes());
        form.Alignment = UIStackViewAlignment.Fill;
        form.SetCustomSpacing(12, _error);
        WinoLayout.Insets(form, 16, 20, 32, 20);
        _scroll.AddSubview(form);

        var column = WinoLayout.VStack(0, hero, switcherScroll, _scroll);
        column.Alignment = UIStackViewAlignment.Fill;
        root.AddSubview(column);
        var guide = root.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(
        [
            column.TopAnchor.ConstraintEqualTo(guide.TopAnchor),
            column.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor),
            column.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor),
            column.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
            form.TopAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.TopAnchor),
            form.BottomAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.BottomAnchor),
            form.LeadingAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.LeadingAnchor),
            form.TrailingAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.TrailingAnchor),
            form.WidthAnchor.ConstraintEqualTo(_scroll.FrameLayoutGuide.WidthAnchor)
        ]);
        View = root;
    }

    private UIView BuildHero()
    {
        var hero = new WinoSurfaceView { Fill = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.025), WinoStyle.Hex(0xFFFFFF, 0.04)) };
        var photo = new UIButton(UIButtonType.System) { ShowsMenuAsPrimaryAction = true, TranslatesAutoresizingMaskIntoConstraints = false };
        var badge = new UIImageView(ModeBarItems.Glyph(WinoIconGlyph.Camera, 14)) { TintColor = WinoStyle.PrimaryText, ContentMode = UIViewContentMode.Center };
        var badgeSurface = new WinoSurfaceView { Fill = WinoStyle.ZoneFill, Stroke = WinoContactStyle.CardStroke, CornerRadius = 14 };
        WinoLayout.Size(badgeSurface, 28, 28);
        WinoLayout.Fill(badge, badgeSurface);
        photo.Menu = UIMenu.Create([UIDeferredMenuElement.CreateUncached(completion => completion(
        [
            ModeBarItems.Action(Translator.ContactAction_ChangePhoto, WinoIconGlyph.Camera, () => Observe(ViewModel.ChoosePhotoCommand.ExecuteAsync(null))),
            ModeBarItems.Action(ViewModel.RemovePhotoLabel ?? Translator.ContactEditDialog_RemovePhoto, WinoIconGlyph.Delete,
                () => ViewModel.RemovePhotoCommand.Execute(null), destructive: true)
        ]))]);
        WinoAccessibility.Label(photo, Translator.ContactAction_PhotoOptions);
        var photoHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        photoHost.AddSubview(_picture);
        photoHost.AddSubview(badgeSurface);
        photoHost.AddSubview(photo);
        WinoLayout.Fill(photo, photoHost);
        NSLayoutConstraint.ActivateConstraints(
        [
            photoHost.WidthAnchor.ConstraintEqualTo(72), photoHost.HeightAnchor.ConstraintEqualTo(72),
            _picture.CenterXAnchor.ConstraintEqualTo(photoHost.CenterXAnchor), _picture.CenterYAnchor.ConstraintEqualTo(photoHost.CenterYAnchor),
            badgeSurface.TrailingAnchor.ConstraintEqualTo(photoHost.TrailingAnchor, 4), badgeSurface.BottomAnchor.ConstraintEqualTo(photoHost.BottomAnchor, 4)
        ]);

        var text = WinoLayout.VStack(2, _previewName, _previewSubtitle);
        WinoLayout.Priorities(text, UILayoutConstraintAxis.Horizontal, 1, 250);
        _favorite.PrimaryActionTriggered += (_, _) =>
        {
            if (ViewModel.ToggleFavoriteCommand.CanExecute(null)) Observe(ViewModel.ToggleFavoriteCommand.ExecuteAsync(null));
        };
        WinoLayout.Size(_favorite, 44, 44);
        var row = WinoLayout.Insets(WinoLayout.HStack(16, photoHost, text, WinoLayout.Spacer(), _favorite), 14, 20, 14, 16);
        row.Alignment = UIStackViewAlignment.Center;
        WinoLayout.Fill(row, hero);
        return hero;
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        NavigationItem.LeftBarButtonItem = new UIBarButtonItem(Translator.ContactEditor_Cancel, UIBarButtonItemStyle.Plain, (_, _) => Observe(CancelAsync()));
        _save = new UIBarButtonItem(Translator.Buttons_Save, UIBarButtonItemStyle.Done, (_, _) =>
        {
            if (ViewModel.SaveCommand.CanExecute(null)) Observe(ViewModel.SaveCommand.ExecuteAsync(null));
        });
        NavigationItem.RightBarButtonItem = _save;
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        _contactId = (parameter as ContactEditNavigationParameter)?.ContactId;
        Title = _contactId is null ? Translator.ContactEditDialog_AddTitle : Translator.ContactEditDialog_Title;
        Bind(nameof(ViewModel.PreviewDisplayName), static vm => vm.PreviewDisplayName, value =>
        {
            _previewName.Text = value ?? string.Empty;
            _picture.SetIdentity(value, ViewModel.EmailAddresses.FirstOrDefault()?.Address ?? value);
        });
        Bind(nameof(ViewModel.PreviewSubtitle), static vm => vm.PreviewSubtitle, value => _previewSubtitle.Text = value ?? string.Empty);
        Bind(nameof(ViewModel.PreviewPhotoBytes), static vm => vm.PreviewPhotoBytes, _ => ApplyPhoto());
        Bind(nameof(ViewModel.PreviewPhotoPath), static vm => vm.PreviewPhotoPath, _ => ApplyPhoto());
        Bind(nameof(ViewModel.IsFavorite), static vm => vm.IsFavorite, favorite =>
        {
            var configuration = UIButtonConfiguration.PlainButtonConfiguration;
            configuration.Image = ModeBarItems.Star(favorite, 22);
            _favorite.Configuration = configuration;
            WinoAccessibility.Label(_favorite, favorite ? Translator.ContactAction_Unfavorite : Translator.ContactAction_Favorite);
        });
        Bind(nameof(ViewModel.SelectedCategory), static vm => vm.SelectedCategory, category =>
        {
            _switcher.SelectedSegment = (int)category;
            foreach (var (sectionCategory, view) in _sections) view.Hidden = sectionCategory != category;
        });
        Bind(nameof(ViewModel.IsErrorOpen), static vm => vm.IsErrorOpen, open => _error.Hidden = !open);
        Bind(nameof(ViewModel.ErrorMessage), static vm => vm.ErrorMessage, message => _error.Message = message);
        // A dirty editor is not swiped away by accident; Cancel asks first.
        Bind(nameof(ViewModel.IsDirty), static vm => vm.IsDirty, dirty =>
        {
            ModalInPresentation = dirty;
            if (NavigationController is { } stack) stack.ModalInPresentation = dirty;
        });
        Bindings.Own(new CommandBinding(ViewModel.SaveCommand, () => null, enabled => { if (_save is not null) _save.Enabled = enabled; }, Dispatcher, ReportError));
        Bind(nameof(ViewModel.IsSaving), static vm => vm.IsSaving, saving =>
        {
            // The router keeps NavigationResult to itself here; bring the saved contact into view like the Mac does.
            if (_wasSaving && !saving && !ViewModel.IsErrorOpen && !ViewModel.IsDirty && _contactId is { } id) Observe(_contacts.LoadAndSelectContactAsync(id));
            _wasSaving = saving;
        });
#if DEBUG
        UIKitDebugBridge.Register("contacts-edit-section", args =>
        {
            ViewModel.SelectedCategory = (ContactEditorCategory)(args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 0);
            return Task.FromResult("ok");
        });
        UIKitDebugBridge.Register("contacts-edit-cancel", async _ => { await CancelAsync(); return "ok"; });
        UIKitDebugBridge.Register("contacts-edit-name", args =>
        {
            ViewModel.GivenName = string.Join(' ', args);
            ViewModel.MarkDirty();
            return Task.FromResult("ok");
        });
        UIKitDebugBridge.Register("contacts-edit-save", async _ => { await ViewModel.SaveCommand.ExecuteAsync(null); return ViewModel.IsErrorOpen ? "error " + ViewModel.ErrorMessage : "ok"; });
#endif
        return base.InitializeAsync(mode, parameter);
    }

    private async Task CancelAsync()
    {
        if (!await ViewModel.CanNavigateBackAsync()) return;
        // The VM's dirty guard already ran; leave without asking again.
        ViewModel.IsDirty = false;
        await _navigation.GoBackAsync();
    }

    private void ApplyPhoto()
    {
        UIImage? image = null;
        try
        {
            if (ViewModel.PreviewPhotoBytes is { Length: > 0 } bytes) image = UIImage.LoadFromData(NSData.FromArray(bytes));
            else if (!string.IsNullOrEmpty(ViewModel.PreviewPhotoPath) && File.Exists(ViewModel.PreviewPhotoPath)) image = UIImage.FromFile(ViewModel.PreviewPhotoPath);
        }
        catch (Exception exception) { ReportError(exception); }
        _picture.Image = image;
    }

    // ---- Sections ----

    private UIView BuildContactInformation()
    {
        var vm = ViewModel;
        var destination = new UIButton(UIButtonType.System) { ShowsMenuAsPrimaryAction = true, ChangesSelectionAsPrimaryAction = false };
        destination.HorizontalAlignment = UIControlContentHorizontalAlignment.Leading;
        var destinationLabel = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        void RebuildDestinations()
        {
            var selected = vm.SelectedDestination;
            destination.SetTitle(selected?.DisplayName ?? string.Empty, UIControlState.Normal);
            destination.Menu = UIMenu.Create(vm.Destinations.Select(item => (UIMenuElement)ModeBarItems.Action(item.DisplayName ?? string.Empty, WinoIconGlyph.People,
                () => vm.SelectedDestination = item, on: selected is not null && item.AddressBookId == selected.AddressBookId)).ToArray());
        }
        Collection(vm.Destinations, RebuildDestinations);
        Bind(nameof(vm.SelectedDestination), static v => v.SelectedDestination, _ => RebuildDestinations());
        Bind(nameof(vm.SourceDescription), static v => v.SourceDescription, value => destinationLabel.Text = value ?? string.Empty);
        Bind(nameof(vm.IsEditMode), static v => v.IsEditMode, edit => { destination.Hidden = edit; destinationLabel.Hidden = !edit; });
        var saveTo = Section(Translator.ContactEditor_SaveTo, Translator.ContactEditor_SaveToDescription, WinoIconGlyph.People, destination, destinationLabel);

        var identity = Section(Translator.ContactEditor_Identity, Translator.ContactEditor_IdentityDescription, WinoIconGlyph.Person,
            Pair(Field(Translator.ContactEditor_GivenName, nameof(vm.GivenName), static v => v.GivenName, static (v, s) => v.GivenName = s, UITextContentType.GivenName),
                 Field(Translator.ContactEditor_Surname, nameof(vm.Surname), static v => v.Surname, static (v, s) => v.Surname = s, UITextContentType.FamilyName)),
            Field(Translator.ContactEditor_DisplayName, nameof(vm.DisplayName), static v => v.DisplayName, static (v, s) => v.DisplayName = s, UITextContentType.Name),
            Pair(Field(Translator.ContactEditor_Nickname, nameof(vm.Nickname), static v => v.Nickname, static (v, s) => v.Nickname = s, UITextContentType.Nickname),
                 Field(Translator.ContactEditor_FileAs, nameof(vm.FileAs), static v => v.FileAs, static (v, s) => v.FileAs = s)));

        var emailRows = Rows(vm.EmailAddresses, email => Pair(
            EntityField(Translator.ContactEditor_Email, () => email.Address, value => email.Address = value, UIKeyboardType.EmailAddress),
            EntityField(Translator.ContactEditor_EmailLabel, () => email.Label, value => email.Label = value),
            RemoveButton(() => vm.RemoveEmailCommand.Execute(email))));
        var addEmail = CommandButton(Translator.ContactEditor_AddEmail, vm.AddEmailCommand);
        Collection(vm.EmailAddresses, () => addEmail.Hidden = vm.EmailAddresses.Count >= 3);
        var emails = Section(Translator.ContactEditor_Emails, Translator.ContactEditor_EmailsDescription, WinoIconGlyph.Mail, emailRows, addEmail);

        var phoneRows = Rows(vm.PhoneNumbers, phone =>
        {
            var kind = new UIButton(UIButtonType.System) { ShowsMenuAsPrimaryAction = true };
            void ApplyKind()
            {
                kind.SetTitle(phone.Kind.ToString(), UIControlState.Normal);
                kind.Menu = UIMenu.Create(Enum.GetValues<ContactPhoneKind>().Select(value => (UIMenuElement)ModeBarItems.Action(value.ToString(), WinoIconGlyph.None, () =>
                {
                    phone.Kind = value;
                    vm.MarkDirty();
                    ApplyKind();
                }, on: phone.Kind == value)).ToArray());
            }
            ApplyKind();
            return Pair(EntityField(Translator.ContactEditor_Phone, () => phone.Number, value => phone.Number = value, UIKeyboardType.PhonePad),
                Labelled(Translator.ContactEditor_PhoneKind, kind), RemoveButton(() => vm.RemovePhoneCommand.Execute(phone)));
        });
        var phones = Section(Translator.ContactEditor_Phones, Translator.ContactEditor_PhonesDescription, WinoIconGlyph.Phone, phoneRows,
            CommandButton(Translator.ContactEditor_AddPhone, vm.AddPhoneCommand));

        var listRows = Rows(vm.ListMemberships, membership => Check(membership.Name, () => membership.IsMember, value => membership.IsMember = value));
        var lists = Section(Translator.ContactEditor_Lists, Translator.ContactEditor_ListsDescription, WinoIconGlyph.List, listRows);
        Collection(vm.ListMemberships, () => lists.Hidden = !vm.HasLists);

        var categoryRows = Rows(vm.CategoryMemberships, membership => Check(membership.Name, () => membership.IsMember, value => membership.IsMember = value));
        var categories = Section(Translator.SettingsMailCategories_Title, Translator.ContactEditor_CategoriesDescription, WinoIconGlyph.Tag, categoryRows);
        Collection(vm.CategoryMemberships, () => categories.Hidden = !vm.HasCategories);

        return Register(ContactEditorCategory.ContactInformation, saveTo, identity, emails, phones, lists, categories);
    }

    private UIView BuildWork()
    {
        var vm = ViewModel;
        var work = Section(Translator.ContactEditor_Work, Translator.ContactEditor_WorkDescription, WinoIconGlyph.Briefcase,
            Pair(Field(Translator.ContactEditor_Company, nameof(vm.CompanyName), static v => v.CompanyName, static (v, s) => v.CompanyName = s, UITextContentType.OrganizationName),
                 Field(Translator.ContactEditor_Department, nameof(vm.Department), static v => v.Department, static (v, s) => v.Department = s)),
            Pair(Field(Translator.ContactEditor_JobTitle, nameof(vm.JobTitle), static v => v.JobTitle, static (v, s) => v.JobTitle = s, UITextContentType.JobTitle),
                 Field(Translator.ContactEditor_Office, nameof(vm.OfficeLocation), static v => v.OfficeLocation, static (v, s) => v.OfficeLocation = s)));
        var relations = Section(Translator.ContactEditor_Relations, Translator.ContactEditor_RelationsDescription, WinoIconGlyph.People,
            Pair(Field(Translator.ContactEditor_Manager, nameof(vm.ManagerName), static v => v.ManagerName, static (v, s) => v.ManagerName = s),
                 Field(Translator.ContactEditor_Assistant, nameof(vm.AssistantName), static v => v.AssistantName, static (v, s) => v.AssistantName = s)));
        return Register(ContactEditorCategory.Work, work, relations);
    }

    private UIView BuildAddress()
    {
        var vm = ViewModel;
        var cards = Rows(vm.PostalAddresses, address =>
        {
            var card = new WinoSurfaceView { Fill = WinoContactStyle.CardFill, Stroke = WinoContactStyle.CardStroke, CornerRadius = 10 };
            var body = WinoLayout.VStack(10,
                WinoStyle.Label(address.Kind.ToString(), WinoStyle.BodyStrong),
                EntityField(Translator.ContactEditor_Street, () => address.Street, value => address.Street = value),
                Pair(EntityField(Translator.ContactEditor_City, () => address.City, value => address.City = value),
                     EntityField(Translator.ContactEditor_Region, () => address.Region, value => address.Region = value)),
                Pair(EntityField(Translator.ContactEditor_PostalCode, () => address.PostalCode, value => address.PostalCode = value),
                     EntityField(Translator.ContactEditor_Country, () => address.Country, value => address.Country = value)));
            body.Alignment = UIStackViewAlignment.Fill;
            WinoLayout.Fill(body, card, 14);
            return card;
        });
        return Register(ContactEditorCategory.Address, Section(Translator.ContactEditor_Addresses, Translator.ContactEditor_AddressesDescription, WinoIconGlyph.Location, cards));
    }

    private UIView BuildOther()
    {
        var vm = ViewModel;
        var day = IntField(Translator.ContactEditor_BirthDay, nameof(vm.BirthdayDay), static v => v.BirthdayDay, static (v, i) => v.BirthdayDay = i);
        var month = IntField(Translator.ContactEditor_BirthMonth, nameof(vm.BirthdayMonth), static v => v.BirthdayMonth, static (v, i) => v.BirthdayMonth = i);
        var year = IntField(Translator.ContactEditor_BirthYear, nameof(vm.BirthdayYear), static v => v.BirthdayYear, static (v, i) => v.BirthdayYear = i);
        var birthdayRow = WinoLayout.HStack(8, day, month, year);
        birthdayRow.Distribution = UIStackViewDistribution.FillEqually;
        var birthday = Labelled(Translator.ContactEditor_Birthday, birthdayRow);
        var personal = Section(Translator.ContactEditor_Personal, Translator.ContactEditor_PersonalDescription, WinoIconGlyph.Heart,
            birthday, Field(Translator.ContactEditor_Website, nameof(vm.Website), static v => v.Website, static (v, s) => v.Website = s, UITextContentType.Url, UIKeyboardType.Url));

        var imRows = Rows(vm.ImAddresses, im => Pair(
            EntityField(Translator.ContactEditor_InstantMessaging, () => im.Address, value => im.Address = value),
            EntityField(Translator.IOS_ContactEditor_Protocol, () => im.Protocol, value => im.Protocol = value),
            RemoveButton(() => vm.RemoveImAddressCommand.Execute(im))));
        var messaging = Section(Translator.ContactEditor_InstantMessaging, Translator.ContactEditor_InstantMessagingDescription, WinoIconGlyph.Globe,
            imRows, CommandButton(Translator.ContactEditor_AddIm, vm.AddImAddressCommand));

        var childRows = Rows(vm.Relations, relation => Pair(
            EntityField(Translator.ContactEditor_Child, () => relation.Name, value => relation.Name = value),
            RemoveButton(() => vm.RemoveRelationCommand.Execute(relation))));
        var family = Section(Translator.ContactEditor_Relations, Translator.ContactEditor_RelationsDescription, WinoIconGlyph.People,
            Field(Translator.ContactEditor_Spouse, nameof(vm.SpouseName), static v => v.SpouseName, static (v, s) => v.SpouseName = s),
            childRows, CommandButton(Translator.ContactEditor_AddChild, vm.AddChildCommand));
        return Register(ContactEditorCategory.Other, personal, messaging, family);
    }

    private UIView BuildNotes()
    {
        var vm = ViewModel;
        var text = new UITextView { Font = WinoStyle.Body, TextColor = WinoStyle.PrimaryText, BackgroundColor = UIColor.Clear, TranslatesAutoresizingMaskIntoConstraints = false };
        text.TextContainerInset = new UIEdgeInsets(10, 8, 10, 8);
        var frame = new WinoSurfaceView { Fill = WinoContactStyle.CardFill, Stroke = WinoContactStyle.CardStroke, CornerRadius = 10 };
        WinoLayout.Fill(text, frame, 1);
        frame.HeightAnchor.ConstraintEqualTo(220).Active = true;
        var binding = Bindings.Own(new PropertyBinding<ContactEditPageViewModel, string?>(vm, nameof(vm.Notes), static v => v.Notes,
            value => { if (text.Text != (value ?? string.Empty)) text.Text = value ?? string.Empty; }, Dispatcher, ReportError, static (v, s) => v.Notes = s ?? string.Empty));
        EventHandler changed = (_, _) => { binding.UpdateSource(text.Text); vm.MarkDirty(); };
        text.Changed += changed;
        Bindings.Own(new ActionDisposable(() => text.Changed -= changed));
        WinoAccessibility.Label(text, Translator.ContactEditor_Notes);
        return Register(ContactEditorCategory.Notes, Section(Translator.ContactEditor_Notes, Translator.ContactEditor_NotesDescription, WinoIconGlyph.Note, frame));
    }

    // ---- Form helpers ----

    private UIView Register(ContactEditorCategory category, params UIView[] sections)
    {
        var stack = WinoLayout.VStack(28, sections);
        stack.Alignment = UIStackViewAlignment.Fill;
        stack.Hidden = category != ContactEditorCategory.ContactInformation;
        _sections.Add((category, stack));
        return stack;
    }

    /// <summary>Windows editor section: glyph + title, description beneath, body with a 12pt top gap.</summary>
    private static UIStackView Section(string title, string description, WinoIconGlyph glyph, params UIView[] body)
    {
        var icon = new WinoIconView(glyph, 16, WinoStyle.SecondaryText);
        var heading = WinoLayout.HStack(10, icon, WinoStyle.Label(title, WinoStyle.BodyStrong));
        heading.AccessibilityTraits = UIAccessibilityTrait.Header;
        var caption = WinoStyle.Label(description, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        var content = WinoLayout.VStack(10, body);
        content.Alignment = UIStackViewAlignment.Fill;
        var section = WinoLayout.VStack(6, heading, caption, content);
        section.Alignment = UIStackViewAlignment.Fill;
        section.SetCustomSpacing(12, caption);
        return section;
    }

    private static UIStackView Labelled(string header, UIView control)
    {
        var stack = WinoLayout.VStack(4, WinoStyle.Label(header, WinoStyle.Caption, WinoStyle.SecondaryText), control);
        stack.Alignment = UIStackViewAlignment.Fill;
        return stack;
    }

    /// <summary>Fields side by side on iPad (Windows wide layout); stacked on iPhone, with a remove button kept on the row.</summary>
    private UIStackView Pair(params UIView[] views)
    {
        var fields = views.Where(view => view is not UIButton).ToArray();
        var buttons = views.OfType<UIButton>().ToArray();
        UIStackView row;
        if (_compact && fields.Length > 1)
        {
            var column = WinoLayout.VStack(10, fields);
            column.Alignment = UIStackViewAlignment.Fill;
            if (buttons.Length == 0) return column;
            row = WinoLayout.HStack(10, [column, .. buttons]);
        }
        else
        {
            row = WinoLayout.HStack(12, views);
            for (int index = 1; index < fields.Length; index++) fields[index].WidthAnchor.ConstraintEqualTo(fields[0].WidthAnchor).Active = true;
        }
        row.Alignment = UIStackViewAlignment.Bottom;
        return row;
    }

    private static UITextField TextField(string placeholder, UIKeyboardType keyboard = UIKeyboardType.Default)
    {
        var field = new UITextField
        {
            BorderStyle = UITextBorderStyle.RoundedRect,
            Placeholder = placeholder,
            Font = WinoStyle.Body,
            KeyboardType = keyboard,
            AutocorrectionType = keyboard == UIKeyboardType.Default ? UITextAutocorrectionType.Default : UITextAutocorrectionType.No,
            AutocapitalizationType = keyboard == UIKeyboardType.Default ? UITextAutocapitalizationType.Words : UITextAutocapitalizationType.None,
            ClearButtonMode = UITextFieldViewMode.WhileEditing,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        field.HeightAnchor.ConstraintGreaterThanOrEqualTo(40).Active = true;
        WinoLayout.Priorities(field, UILayoutConstraintAxis.Horizontal, 1, 250);
        WinoAccessibility.Label(field, placeholder);
        return field;
    }

    private UIStackView Field(string header, string property, Func<ContactEditPageViewModel, string?> read, Action<ContactEditPageViewModel, string> write,
        NSString? content = null, UIKeyboardType keyboard = UIKeyboardType.Default)
    {
        var field = TextField(header, keyboard);
        if (content is not null) field.TextContentType = content;
        var binding = Bindings.Own(new PropertyBinding<ContactEditPageViewModel, string?>(ViewModel, property, read,
            value => { if (field.Text != (value ?? string.Empty)) field.Text = value ?? string.Empty; }, Dispatcher, ReportError,
            (vm, s) => write(vm, s ?? string.Empty)));
        EventHandler changed = (_, _) => { binding.UpdateSource(field.Text); ViewModel.MarkDirty(); };
        field.EditingChanged += changed;
        Bindings.Own(new ActionDisposable(() => field.EditingChanged -= changed));
        return Labelled(header, field);
    }

    private UIStackView IntField(string header, string property, Func<ContactEditPageViewModel, int?> read, Action<ContactEditPageViewModel, int?> write)
    {
        var field = TextField(header, UIKeyboardType.NumberPad);
        field.TextAlignment = UITextAlignment.Center;
        field.ClearButtonMode = UITextFieldViewMode.Never;
        var binding = Bindings.Own(new PropertyBinding<ContactEditPageViewModel, int?>(ViewModel, property, read,
            value => { var text = value?.ToString() ?? string.Empty; if (field.Text != text) field.Text = text; }, Dispatcher, ReportError, write));
        EventHandler changed = (_, _) => { binding.UpdateSource(int.TryParse(field.Text, out var parsed) ? parsed : null); ViewModel.MarkDirty(); };
        field.EditingChanged += changed;
        Bindings.Own(new ActionDisposable(() => field.EditingChanged -= changed));
        return Labelled(header, field);
    }

    /// <summary>A field over a plain entity (email, phone, address rows); edits mark the editor dirty.</summary>
    private UIStackView EntityField(string header, Func<string?> get, Action<string> set, UIKeyboardType keyboard = UIKeyboardType.Default)
    {
        var field = TextField(header, keyboard);
        field.Text = get() ?? string.Empty;
        field.EditingChanged += (_, _) => { set(field.Text ?? string.Empty); ViewModel.MarkDirty(); };
        return Labelled(header, field);
    }

    private UIView Check(string title, Func<bool> get, Action<bool> set)
    {
        var toggle = new UISwitch { On = get(), TranslatesAutoresizingMaskIntoConstraints = false };
        toggle.ValueChanged += (_, _) => { set(toggle.On); ViewModel.MarkDirty(); };
        var label = WinoStyle.Label(title, WinoStyle.Body);
        WinoLayout.Priorities(label, UILayoutConstraintAxis.Horizontal, 1, 250);
        var row = WinoLayout.HStack(12, label, WinoLayout.Spacer(), toggle);
        row.Alignment = UIStackViewAlignment.Center;
        WinoAccessibility.Label(toggle, title);
        return row;
    }

    private static UIButton RemoveButton(Action remove)
    {
        var configuration = UIButtonConfiguration.GrayButtonConfiguration;
        configuration.Image = ModeBarItems.Glyph(WinoIconGlyph.Dismiss, 12);
        configuration.CornerStyle = UIButtonConfigurationCornerStyle.Capsule;
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(button, 40, 40);
        button.PrimaryActionTriggered += (_, _) => remove();
        WinoAccessibility.Label(button, Translator.Buttons_Remove);
        return button;
    }

    /// <summary>A vertical list rebuilt from an observable collection.</summary>
    private UIStackView Rows<T>(ObservableCollection<T> items, Func<T, UIView> build) where T : class
    {
        var stack = WinoLayout.VStack(10);
        stack.Alignment = UIStackViewAlignment.Fill;
        void Rebuild()
        {
            foreach (var view in stack.ArrangedSubviews) { stack.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
            foreach (var item in items) stack.AddArrangedSubview(build(item));
        }
        Collection(items, Rebuild);
        return stack;
    }

    private void Collection(INotifyCollectionChanged collection, Action rebuild)
    {
        NotifyCollectionChangedEventHandler handler = (_, _) => OnUI(rebuild);
        collection.CollectionChanged += handler;
        Bindings.Own(new ActionDisposable(() => collection.CollectionChanged -= handler));
        rebuild();
    }

    private void OnUI(Action action)
    {
        if (_released) return;
        _ = Dispatcher.ExecuteOnUIThread(() => { if (!_released) action(); });
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { ReportError(exception); }
    }

    public override async Task ReleaseAsync()
    {
        _released = true;
        await base.ReleaseAsync();
    }
}
