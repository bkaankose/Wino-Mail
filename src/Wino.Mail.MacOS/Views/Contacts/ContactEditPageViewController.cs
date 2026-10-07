using System.Collections.Specialized;
using System.ComponentModel;
using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Contacts;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Contacts;

/// <summary>
/// Contact editor (Windows ContactEditPage) shown in the shell content zone in place of the
/// contact list, exactly like the Windows frame does. A hero with the photo, preview name,
/// favourite, Cancel and Save sits on top; a section switcher (Contact information, Work,
/// Address, Other, Notes) selects the form below. Save and Cancel return to the list through
/// the router's history.
/// </summary>
public sealed class ContactEditPageViewController : WinoViewController<ContactEditPageViewModel>
{
    private readonly AppKitNavigationService _navigation;
    private readonly List<(ContactEditorCategory Category, NSView View)> _sections = new();
    private WinoContactPicture _picture = null!;
    private NSTextField _previewName = null!;
    private NSTextField _previewSubtitle = null!;
    private NSButton _favorite = null!;
    private NSSegmentedControl _switcher = null!;
    private WinoInfoBar _error = null!;
    private NSView _root = null!;
    private Guid? _contactId;
    private bool _wasSaving;
    private bool _released;

    public ContactEditPageViewController(ContactEditPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger, AppKitNavigationService navigation)
        : base(viewModel, dispatcher, logger)
    {
        _navigation = navigation;
    }

    public override void LoadView()
    {
        _root = new NSView();
        var zone = new WinoZoneView();
        WinoLayout.Fill(zone, _root, 2, 4, 7, 7);
        var content = zone.ContentView;

        var hero = BuildHero();
        var stroke = new WinoSeparator();
        _switcher = NSSegmentedControl.FromLabels(
        [
            Translator.ContactEditor_CategoryContactInformation, Translator.ContactEditor_CategoryWork,
            Translator.ContactEditor_CategoryAddress, Translator.ContactEditor_CategoryOther, Translator.ContactEditor_CategoryNotes
        ], NSSegmentSwitchTracking.SelectOne, () => ViewModel.SelectedCategory = (ContactEditorCategory)(int)_switcher.SelectedSegment);
        _switcher.SegmentStyle = NSSegmentStyle.Rounded;
        _switcher.TranslatesAutoresizingMaskIntoConstraints = false;
        var switcherRow = WinoLayout.HStack(0, _switcher);
        switcherRow.EdgeInsets = new NSEdgeInsets(12, 24, 0, 24);

        _error = new WinoInfoBar(WinoInfoBarSeverity.Error, Translator.ContactInfoBar_ErrorTitle) { Hidden = true, IsClosable = true };
        _error.Closed += (_, _) => ViewModel.IsErrorOpen = false;

        var form = WinoLayout.VStack(0, _error, BuildContactInformation(), BuildWork(), BuildAddress(), BuildOther(), BuildNotes());
        form.EdgeInsets = new NSEdgeInsets(16, 24, 32, 24);
        var document = new FlippedView();
        document.AddSubview(form);
        var scroll = new NSScrollView { DocumentView = document, HasVerticalScroller = true, AutohidesScrollers = true, DrawsBackground = false, TranslatesAutoresizingMaskIntoConstraints = false };

        content.AddSubview(hero);
        content.AddSubview(stroke);
        content.AddSubview(switcherRow);
        content.AddSubview(scroll);
        NSLayoutConstraint.ActivateConstraints(
        [
            hero.TopAnchor.ConstraintEqualTo(content.TopAnchor),
            hero.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            hero.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            stroke.TopAnchor.ConstraintEqualTo(hero.BottomAnchor),
            stroke.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            stroke.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            switcherRow.TopAnchor.ConstraintEqualTo(stroke.BottomAnchor),
            switcherRow.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            switcherRow.TrailingAnchor.ConstraintLessThanOrEqualTo(content.TrailingAnchor),
            scroll.TopAnchor.ConstraintEqualTo(switcherRow.BottomAnchor),
            scroll.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(content.BottomAnchor),
            document.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor),
            form.LeadingAnchor.ConstraintEqualTo(document.LeadingAnchor),
            form.TrailingAnchor.ConstraintEqualTo(document.TrailingAnchor),
            form.TopAnchor.ConstraintEqualTo(document.TopAnchor),
            form.BottomAnchor.ConstraintEqualTo(document.BottomAnchor),
            _error.WidthAnchor.ConstraintEqualTo(form.WidthAnchor, 1, -48)
        ]);
        foreach (var (_, view) in _sections) view.WidthAnchor.ConstraintEqualTo(form.WidthAnchor, 1, -48).Active = true;
        form.SetCustomSpacing(12, _error);
        View = _root;
    }

    private NSView BuildHero()
    {
        var hero = new WinoSurfaceView { Fill = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.025), WinoStyle.Hex(0xFFFFFF, 0.04)) };

        _picture = new WinoContactPicture(72);
        var photoMenu = new NSMenu();
        photoMenu.AddItem(new NSMenuItem(Translator.ContactAction_ChangePhoto, (_, _) => Observe(ViewModel.ChoosePhotoCommand.ExecuteAsync(null))));
        var remove = new NSMenuItem(ViewModel.RemovePhotoLabel ?? Translator.ContactEditDialog_RemovePhoto, (_, _) => ViewModel.RemovePhotoCommand.Execute(null));
        photoMenu.AddItem(remove);
        // A pull-down button reserves room for its disclosure arrow and offsets its image, so the
        // badge draws the glyph itself, centred on both axes, under a transparent hit target.
        var photoButton = new NSButton
        {
            Bordered = false,
            Title = string.Empty,
            ToolTip = Translator.ContactAction_PhotoOptions,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoAccessibility.Label(photoButton, Translator.ContactAction_PhotoOptions);
        photoButton.Activated += (_, _) => photoMenu.PopUpMenu(null, new CoreGraphics.CGPoint(0, photoButton.Bounds.Height + 4), photoButton);
        var photoBadge = new WinoSurfaceView { Fill = WinoStyle.ZoneFill, Stroke = WinoContactStyle.CardStroke, CornerRadius = 14 };
        WinoLayout.Size(photoBadge, 28, 28);
        var cameraIcon = new WinoIconView(WinoIconGlyph.Camera, 14);
        photoBadge.AddSubview(cameraIcon);
        NSLayoutConstraint.ActivateConstraints(
        [
            cameraIcon.CenterXAnchor.ConstraintEqualTo(photoBadge.CenterXAnchor), cameraIcon.CenterYAnchor.ConstraintEqualTo(photoBadge.CenterYAnchor),
            cameraIcon.WidthAnchor.ConstraintEqualTo(14), cameraIcon.HeightAnchor.ConstraintEqualTo(14)
        ]);
        WinoLayout.Fill(photoButton, photoBadge);
        var photoHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        photoHost.AddSubview(_picture);
        photoHost.AddSubview(photoBadge);
        NSLayoutConstraint.ActivateConstraints(
        [
            photoHost.WidthAnchor.ConstraintEqualTo(72), photoHost.HeightAnchor.ConstraintEqualTo(72),
            _picture.CenterXAnchor.ConstraintEqualTo(photoHost.CenterXAnchor), _picture.CenterYAnchor.ConstraintEqualTo(photoHost.CenterYAnchor),
            photoBadge.TrailingAnchor.ConstraintEqualTo(photoHost.TrailingAnchor, 4), photoBadge.BottomAnchor.ConstraintEqualTo(photoHost.BottomAnchor, 4)
        ]);

        _previewName = WinoStyle.Label(string.Empty, WinoStyle.PageTitle);
        _previewSubtitle = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText);
        var text = WinoLayout.VStack(2, _previewName, _previewSubtitle);
        text.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        _favorite = CommandButton(string.Empty, ViewModel.ToggleFavoriteCommand);
        _favorite.Bordered = false;
        _favorite.ImagePosition = NSCellImagePosition.ImageOnly;
        WinoLayout.Size(_favorite, 32, 32);
        var cancel = new NSButton { Title = Translator.ContactEditor_Cancel, BezelStyle = NSBezelStyle.Rounded, TranslatesAutoresizingMaskIntoConstraints = false, KeyEquivalent = "\u001b" };
        cancel.Activated += (_, _) => Observe(CancelAsync());
        var save = CommandButton(Translator.Buttons_Save, ViewModel.SaveCommand);
        save.KeyEquivalent = "\r";
        save.BezelColor = WinoStyle.Accent;
        var buttons = WinoLayout.HStack(8, _favorite, cancel, save);

        var row = WinoLayout.HStack(16, photoHost, text, WinoLayout.Spacer(), buttons);
        row.EdgeInsets = new NSEdgeInsets(14, 24, 14, 24);
        WinoLayout.Fill(row, hero);
        return hero;
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        _contactId = (parameter as ContactEditNavigationParameter)?.ContactId;
        Bind(nameof(ViewModel.PreviewDisplayName), vm => vm.PreviewDisplayName, value =>
        {
            _previewName.StringValue = value ?? string.Empty;
            _picture.SetIdentity(value, ViewModel.EmailAddresses.FirstOrDefault()?.Address ?? value);
        });
        Bind(nameof(ViewModel.PreviewSubtitle), vm => vm.PreviewSubtitle, value => _previewSubtitle.StringValue = value ?? string.Empty);
        Bind(nameof(ViewModel.PreviewPhotoBytes), vm => vm.PreviewPhotoBytes, _ => ApplyPhoto());
        Bind(nameof(ViewModel.PreviewPhotoPath), vm => vm.PreviewPhotoPath, _ => ApplyPhoto());
        Bind(nameof(ViewModel.IsFavorite), vm => vm.IsFavorite, favorite =>
        {
            _favorite.Image = WinoContactStyle.StarImage(favorite, 17);
            _favorite.ToolTip = favorite ? Translator.ContactAction_Unfavorite : Translator.ContactAction_Favorite;
        });
        Bind(nameof(ViewModel.SelectedCategory), vm => vm.SelectedCategory, category =>
        {
            _switcher.SelectedSegment = (int)category;
            foreach (var (sectionCategory, view) in _sections) view.Hidden = sectionCategory != category;
        });
        Bind(nameof(ViewModel.IsErrorOpen), vm => vm.IsErrorOpen, open => _error.Hidden = !open);
        Bind(nameof(ViewModel.ErrorMessage), vm => vm.ErrorMessage, message => _error.Message = message);
        Bind(nameof(ViewModel.IsSaving), vm => vm.IsSaving, saving =>
        {
            // The router does not hand NavigationResult back to the list; remember the saved contact ourselves.
            if (_wasSaving && !saving && !ViewModel.IsErrorOpen && !ViewModel.IsDirty && _contactId is { } id) ContactsPageViewController.PendingSelection = id;
            _wasSaving = saving;
        });
        ViewModel.OnNavigatedTo(mode, parameter!);
#if DEBUG
        MacDebugBridge.Register("contacts-edit-section", args => { ViewModel.SelectedCategory = (ContactEditorCategory)int.Parse(args[0]); return Task.FromResult("ok"); });
        MacDebugBridge.Register("contacts-edit-cancel", async _ => { await CancelAsync(); return "ok"; });
#endif
        return Task.CompletedTask;
    }

    private async Task CancelAsync()
    {
        if (!await ViewModel.CanNavigateBackAsync()) return;
        await _navigation.GoBackAsync();
    }

    private void ApplyPhoto()
    {
        NSImage? image = null;
        try
        {
            if (ViewModel.PreviewPhotoBytes is { Length: > 0 } bytes) image = new NSImage(NSData.FromArray(bytes));
            else if (!string.IsNullOrEmpty(ViewModel.PreviewPhotoPath) && File.Exists(ViewModel.PreviewPhotoPath)) image = new NSImage(ViewModel.PreviewPhotoPath);
        }
        catch (Exception exception) { ReportError(exception); }
        _picture.Image = image;
    }

    // ---- Sections ----

    private NSView BuildContactInformation()
    {
        var vm = ViewModel;
        var destinationPopup = new NSPopUpButton { TranslatesAutoresizingMaskIntoConstraints = false };
        var destinationLabel = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText);
        void RebuildDestinations()
        {
            destinationPopup.RemoveAllItems();
            foreach (var destination in vm.Destinations) destinationPopup.AddItem(destination.DisplayName);
            var index = vm.SelectedDestination is { } selected ? vm.Destinations.ToList().FindIndex(item => item.AddressBookId == selected.AddressBookId) : -1;
            destinationPopup.SelectItem(index);
        }
        Collection(vm.Destinations, RebuildDestinations);
        Bind(nameof(vm.SelectedDestination), v => v.SelectedDestination, _ => RebuildDestinations());
        destinationPopup.Activated += (_, _) =>
        {
            var index = (int)destinationPopup.IndexOfSelectedItem;
            if (index >= 0 && index < vm.Destinations.Count) vm.SelectedDestination = vm.Destinations[index];
        };
        Bind(nameof(vm.SourceDescription), v => v.SourceDescription, value => destinationLabel.StringValue = value ?? string.Empty);
        Bind(nameof(vm.IsEditMode), v => v.IsEditMode, edit => { destinationPopup.Hidden = edit; destinationLabel.Hidden = !edit; });
        var saveTo = Section(Translator.ContactEditor_SaveTo, Translator.ContactEditor_SaveToDescription, WinoIconGlyph.People, destinationPopup, destinationLabel);

        var identity = Section(Translator.ContactEditor_Identity, Translator.ContactEditor_IdentityDescription, WinoIconGlyph.Person,
            Pair(Field(Translator.ContactEditor_GivenName, nameof(vm.GivenName), v => v.GivenName, (v, s) => v.GivenName = s),
                 Field(Translator.ContactEditor_Surname, nameof(vm.Surname), v => v.Surname, (v, s) => v.Surname = s)),
            Field(Translator.ContactEditor_DisplayName, nameof(vm.DisplayName), v => v.DisplayName, (v, s) => v.DisplayName = s),
            Pair(Field(Translator.ContactEditor_Nickname, nameof(vm.Nickname), v => v.Nickname, (v, s) => v.Nickname = s),
                 Field(Translator.ContactEditor_FileAs, nameof(vm.FileAs), v => v.FileAs, (v, s) => v.FileAs = s)));

        var emailRows = Rows(vm.EmailAddresses, email => Pair(
            EntityField(Translator.ContactEditor_Email, () => email.Address, value => email.Address = value),
            EntityField(Translator.ContactEditor_EmailLabel, () => email.Label, value => email.Label = value),
            RemoveButton(() => vm.RemoveEmailCommand.Execute(email))));
        var addEmail = CommandButton(Translator.ContactEditor_AddEmail, vm.AddEmailCommand);
        Collection(vm.EmailAddresses, () => addEmail.Hidden = vm.EmailAddresses.Count >= 3);
        var emails = Section(Translator.ContactEditor_Emails, Translator.ContactEditor_EmailsDescription, WinoIconGlyph.Mail, emailRows, addEmail);

        var phoneRows = Rows(vm.PhoneNumbers, phone =>
        {
            var kind = new NSPopUpButton { TranslatesAutoresizingMaskIntoConstraints = false };
            foreach (var name in Enum.GetNames<ContactPhoneKind>()) kind.AddItem(name);
            kind.SelectItem((int)phone.Kind);
            kind.Activated += (_, _) => { phone.Kind = (ContactPhoneKind)(int)kind.IndexOfSelectedItem; vm.MarkDirty(); };
            return Pair(EntityField(Translator.ContactEditor_Phone, () => phone.Number, value => phone.Number = value),
                Labelled(Translator.ContactEditor_PhoneKind, kind), RemoveButton(() => vm.RemovePhoneCommand.Execute(phone)));
        });
        var phones = Section(Translator.ContactEditor_Phones, Translator.ContactEditor_PhonesDescription, WinoIconGlyph.Phone, phoneRows, CommandButton(Translator.ContactEditor_AddPhone, vm.AddPhoneCommand));

        var listRows = Rows(vm.ListMemberships, membership => Check(membership.Name, () => membership.IsMember, value => membership.IsMember = value));
        var lists = Section(Translator.ContactEditor_Lists, Translator.ContactEditor_ListsDescription, WinoIconGlyph.List, listRows);
        Collection(vm.ListMemberships, () => lists.Hidden = !vm.HasLists);

        var categoryRows = Rows(vm.CategoryMemberships, membership => Check(membership.Name, () => membership.IsMember, value => membership.IsMember = value));
        var categories = Section(Translator.SettingsMailCategories_Title, Translator.ContactEditor_CategoriesDescription, WinoIconGlyph.Tag, categoryRows);
        Collection(vm.CategoryMemberships, () => categories.Hidden = !vm.HasCategories);

        return Register(ContactEditorCategory.ContactInformation, saveTo, identity, emails, phones, lists, categories);
    }

    private NSView BuildWork()
    {
        var vm = ViewModel;
        var work = Section(Translator.ContactEditor_Work, Translator.ContactEditor_WorkDescription, WinoIconGlyph.Briefcase,
            Pair(Field(Translator.ContactEditor_Company, nameof(vm.CompanyName), v => v.CompanyName, (v, s) => v.CompanyName = s),
                 Field(Translator.ContactEditor_Department, nameof(vm.Department), v => v.Department, (v, s) => v.Department = s)),
            Pair(Field(Translator.ContactEditor_JobTitle, nameof(vm.JobTitle), v => v.JobTitle, (v, s) => v.JobTitle = s),
                 Field(Translator.ContactEditor_Office, nameof(vm.OfficeLocation), v => v.OfficeLocation, (v, s) => v.OfficeLocation = s)));
        var relations = Section(Translator.ContactEditor_Relations, Translator.ContactEditor_RelationsDescription, WinoIconGlyph.People,
            Pair(Field(Translator.ContactEditor_Manager, nameof(vm.ManagerName), v => v.ManagerName, (v, s) => v.ManagerName = s),
                 Field(Translator.ContactEditor_Assistant, nameof(vm.AssistantName), v => v.AssistantName, (v, s) => v.AssistantName = s)));
        return Register(ContactEditorCategory.Work, work, relations);
    }

    private NSView BuildAddress()
    {
        var vm = ViewModel;
        var cards = Rows(vm.PostalAddresses, address =>
        {
            var card = new WinoSurfaceView { Fill = WinoContactStyle.CardFill, Stroke = WinoContactStyle.CardStroke, CornerRadius = 8 };
            var body = WinoLayout.VStack(10,
                WinoStyle.Label(address.Kind.ToString(), WinoStyle.BodyStrong),
                EntityField(Translator.ContactEditor_Street, () => address.Street, value => address.Street = value),
                Pair(EntityField(Translator.ContactEditor_City, () => address.City, value => address.City = value),
                     EntityField(Translator.ContactEditor_Region, () => address.Region, value => address.Region = value)),
                Pair(EntityField(Translator.ContactEditor_PostalCode, () => address.PostalCode, value => address.PostalCode = value),
                     EntityField(Translator.ContactEditor_Country, () => address.Country, value => address.Country = value)));
            WinoLayout.Fill(body, card, 16);
            foreach (var child in body.ArrangedSubviews) child.WidthAnchor.ConstraintEqualTo(body.WidthAnchor).Active = true;
            return card;
        });
        return Register(ContactEditorCategory.Address, Section(Translator.ContactEditor_Addresses, Translator.ContactEditor_AddressesDescription, WinoIconGlyph.Location, cards));
    }

    private NSView BuildOther()
    {
        var vm = ViewModel;
        var day = IntField(Translator.ContactEditor_BirthDay, nameof(vm.BirthdayDay), v => v.BirthdayDay, (v, i) => v.BirthdayDay = i);
        var month = IntField(Translator.ContactEditor_BirthMonth, nameof(vm.BirthdayMonth), v => v.BirthdayMonth, (v, i) => v.BirthdayMonth = i);
        var year = IntField(Translator.ContactEditor_BirthYear, nameof(vm.BirthdayYear), v => v.BirthdayYear, (v, i) => v.BirthdayYear = i);
        var birthday = Labelled(Translator.ContactEditor_Birthday, WinoLayout.HStack(8, day, month, year));
        var personal = Section(Translator.ContactEditor_Personal, Translator.ContactEditor_PersonalDescription, WinoIconGlyph.Heart,
            birthday, Field(Translator.ContactEditor_Website, nameof(vm.Website), v => v.Website, (v, s) => v.Website = s));

        var imRows = Rows(vm.ImAddresses, im => Pair(
            EntityField(Translator.ContactEditor_InstantMessaging, () => im.Address, value => im.Address = value),
            EntityField("Protocol", () => im.Protocol, value => im.Protocol = value),
            RemoveButton(() => vm.RemoveImAddressCommand.Execute(im))));
        var messaging = Section(Translator.ContactEditor_InstantMessaging, Translator.ContactEditor_InstantMessagingDescription, WinoIconGlyph.Globe,
            imRows, CommandButton(Translator.ContactEditor_AddIm, vm.AddImAddressCommand));

        var childRows = Rows(vm.Relations, relation => Pair(
            EntityField(Translator.ContactEditor_Child, () => relation.Name, value => relation.Name = value),
            RemoveButton(() => vm.RemoveRelationCommand.Execute(relation))));
        var family = Section(Translator.ContactEditor_Relations, Translator.ContactEditor_RelationsDescription, WinoIconGlyph.People,
            Field(Translator.ContactEditor_Spouse, nameof(vm.SpouseName), v => v.SpouseName, (v, s) => v.SpouseName = s),
            childRows, CommandButton(Translator.ContactEditor_AddChild, vm.AddChildCommand));
        return Register(ContactEditorCategory.Other, personal, messaging, family);
    }

    private NSView BuildNotes()
    {
        var vm = ViewModel;
        var text = new NSTextView { Font = WinoStyle.Body, TextColor = WinoStyle.PrimaryText, DrawsBackground = false, RichText = false, AutomaticQuoteSubstitutionEnabled = false, TextContainerInset = new CoreGraphics.CGSize(8, 8) };
        text.MinSize = new CoreGraphics.CGSize(0, 180);
        text.MaxSize = new CoreGraphics.CGSize(nfloat.MaxValue, nfloat.MaxValue);
        text.VerticallyResizable = true;
        text.HorizontallyResizable = false;
        text.AutoresizingMask = NSViewResizingMask.WidthSizable;
        text.TextContainer!.WidthTracksTextView = true;
        var scroll = new NSScrollView { DocumentView = text, HasVerticalScroller = true, DrawsBackground = false, TranslatesAutoresizingMaskIntoConstraints = false, BorderType = NSBorderType.NoBorder };
        var frame = new WinoSurfaceView { Fill = WinoContactStyle.CardFill, Stroke = WinoContactStyle.CardStroke, CornerRadius = 8 };
        WinoLayout.Fill(scroll, frame, 1);
        frame.HeightAnchor.ConstraintEqualTo(220).Active = true;
        var binding = Bindings.Own(new PropertyBinding<ContactEditPageViewModel, string?>(vm, nameof(vm.Notes), v => v.Notes,
            value => { if (text.Value != (value ?? string.Empty)) text.Value = value ?? string.Empty; }, Dispatcher, ReportError, (v, s) => v.Notes = s!));
        EventHandler changed = (_, _) => { binding.UpdateSource(text.Value); vm.MarkDirty(); };
        text.TextDidChange += changed;
        Bindings.Own(new ActionDisposable(() => text.TextDidChange -= changed));
        return Register(ContactEditorCategory.Notes, Section(Translator.ContactEditor_Notes, Translator.ContactEditor_NotesDescription, WinoIconGlyph.Note, frame));
    }

    // ---- Form helpers ----

    private NSView Register(ContactEditorCategory category, params NSView[] sections)
    {
        var stack = WinoLayout.VStack(28, sections);
        foreach (var section in sections) section.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        stack.Hidden = category != ContactEditorCategory.ContactInformation;
        _sections.Add((category, stack));
        return stack;
    }

    /// <summary>Windows editor section: glyph + title, description beneath, body with 12 pt top gap.</summary>
    private static NSStackView Section(string title, string description, WinoIconGlyph glyph, params NSView[] body)
    {
        var icon = new WinoIconView(glyph, 16, WinoStyle.SecondaryText);
        var heading = WinoLayout.HStack(10, icon, WinoStyle.Label(title, WinoStyle.Heading));
        var caption = WinoStyle.Label(description, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        var content = WinoLayout.VStack(10, body);
        var section = WinoLayout.VStack(6, heading, caption, content);
        section.SetCustomSpacing(12, caption);
        NSLayoutConstraint.ActivateConstraints([caption.WidthAnchor.ConstraintEqualTo(section.WidthAnchor), content.WidthAnchor.ConstraintEqualTo(section.WidthAnchor)]);
        foreach (var view in body)
            if (view is NSStackView or WinoSurfaceView) view.WidthAnchor.ConstraintEqualTo(content.WidthAnchor).Active = true;
        return section;
    }

    private static NSStackView Labelled(string header, NSView control)
    {
        var stack = WinoLayout.VStack(4, WinoStyle.Label(header, WinoStyle.Caption, WinoStyle.SecondaryText), control);
        if (control is NSTextField) control.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        return stack;
    }

    /// <summary>Two fields side by side (Windows wide layout), each taking half of the row.</summary>
    private static NSStackView Pair(params NSView[] views)
    {
        var row = WinoLayout.HStack(12, views);
        row.Alignment = NSLayoutAttribute.Bottom;
        row.Distribution = NSStackViewDistribution.Fill;
        var fields = views.OfType<NSStackView>().ToArray();
        for (int index = 1; index < fields.Length; index++) fields[index].WidthAnchor.ConstraintEqualTo(fields[0].WidthAnchor).Active = true;
        foreach (var button in views.OfType<NSButton>()) button.BottomAnchor.ConstraintEqualTo(row.BottomAnchor, -1).Active = true;
        return row;
    }

    private NSStackView Field(string header, string property, Func<ContactEditPageViewModel, string?> read, Action<ContactEditPageViewModel, string> write)
    {
        var field = new NSTextField { TranslatesAutoresizingMaskIntoConstraints = false, PlaceholderString = header };
        field.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        var binding = Bindings.Own(new PropertyBinding<ContactEditPageViewModel, string?>(ViewModel, property, read,
            value => { if (field.StringValue != (value ?? string.Empty)) field.StringValue = value ?? string.Empty; }, Dispatcher, ReportError, (vm, s) => write(vm, s!)));
        EventHandler changed = (_, _) => { binding.UpdateSource(field.StringValue); ViewModel.MarkDirty(); };
        field.Changed += changed;
        Bindings.Own(new ActionDisposable(() => field.Changed -= changed));
        WinoAccessibility.Label(field, header);
        return Labelled(header, field);
    }

    private NSStackView IntField(string header, string property, Func<ContactEditPageViewModel, int?> read, Action<ContactEditPageViewModel, int?> write)
    {
        var field = new NSTextField { TranslatesAutoresizingMaskIntoConstraints = false, Alignment = NSTextAlignment.Center };
        WinoLayout.Size(field, 72);
        var binding = Bindings.Own(new PropertyBinding<ContactEditPageViewModel, int?>(ViewModel, property, read,
            value => { var text = value?.ToString() ?? string.Empty; if (field.StringValue != text) field.StringValue = text; }, Dispatcher, ReportError, write));
        EventHandler changed = (_, _) => { binding.UpdateSource(int.TryParse(field.StringValue, out var parsed) ? parsed : null); ViewModel.MarkDirty(); };
        field.Changed += changed;
        Bindings.Own(new ActionDisposable(() => field.Changed -= changed));
        WinoAccessibility.Label(field, header);
        return Labelled(header, field);
    }

    /// <summary>A field over a plain entity (email, phone, address rows); edits mark the editor dirty.</summary>
    private NSStackView EntityField(string header, Func<string?> get, Action<string> set)
    {
        var field = new NSTextField { TranslatesAutoresizingMaskIntoConstraints = false, PlaceholderString = header, StringValue = get() ?? string.Empty };
        field.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        field.Changed += (_, _) => { set(field.StringValue); ViewModel.MarkDirty(); };
        WinoAccessibility.Label(field, header);
        return Labelled(header, field);
    }

    private NSButton Check(string title, Func<bool> get, Action<bool> set)
    {
        NSButton check = null!;
        check = WinoCheckbox.Create(title, () => { set(check.State == NSCellStateValue.On); ViewModel.MarkDirty(); });
        check.State = get() ? NSCellStateValue.On : NSCellStateValue.Off;
        check.TranslatesAutoresizingMaskIntoConstraints = false;
        return check;
    }

    private static NSButton RemoveButton(Action remove)
    {
        var button = new NSButton { Image = WinoIcons.Image(WinoIconGlyph.Dismiss, 12), BezelStyle = NSBezelStyle.Rounded, ToolTip = Translator.Buttons_Remove, TranslatesAutoresizingMaskIntoConstraints = false };
        button.ImagePosition = NSCellImagePosition.ImageOnly;
        button.Activated += (_, _) => remove();
        WinoAccessibility.Label(button, Translator.Buttons_Remove);
        return button;
    }

    /// <summary>A vertical list rebuilt from an observable collection.</summary>
    private NSStackView Rows<T>(System.Collections.ObjectModel.ObservableCollection<T> items, Func<T, NSView> build) where T : class
    {
        var stack = WinoLayout.VStack(10);
        void Rebuild()
        {
            foreach (var view in stack.ArrangedSubviews) view.RemoveFromSuperview();
            foreach (var item in items)
            {
                var view = build(item);
                stack.AddArrangedSubview(view);
                view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
            }
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

    private void Bind<TValue>(string property, Func<ContactEditPageViewModel, TValue> read, Action<TValue> apply)
        => Bindings.Own(new PropertyBinding<ContactEditPageViewModel, TValue>(ViewModel, property, read, apply, Dispatcher, ReportError));

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

    private sealed class FlippedView : NSView
    {
        public FlippedView() => TranslatesAutoresizingMaskIntoConstraints = false;
        public override bool IsFlipped => true;
    }
}
