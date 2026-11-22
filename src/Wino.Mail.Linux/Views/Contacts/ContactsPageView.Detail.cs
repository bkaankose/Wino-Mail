using System.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Contacts;
using Wino.Mail.Linux.Views.Common;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Contacts;

/// <summary>
/// The selected contact in the end pane (macOS ContactsPageViewController.Detail): favourite, picture, name, job,
/// source, categories, the four actions and the field cards. It follows changes of the shown contact; in the swapped
/// (narrow) layout a back button returns to the list.
/// </summary>
internal sealed partial class ContactsPageView
{
    private const int DetailPictureSize = 88;

    private Gtk.Stack? _detailStack;
    private Gtk.ScrolledWindow? _detailScroller;
    private Gtk.Button? _detailBack;
    private Gtk.Button? _detailStar;
    private Gtk.Label? _detailStarGlyph;
    private WinoContactPicture? _detailPicture;
    private Gtk.Label? _detailName;
    private Gtk.Label? _detailJob;
    private Gtk.Label? _detailSource;
    private WinoCategoryChipRow? _detailChips;
    private Gtk.Button? _sendMail;
    private Gtk.Button? _editContact;
    private Gtk.Button? _removeFromList;
    private Gtk.Button? _deleteContact;
    private Gtk.Box? _detailCards;
    private AccountContactViewModel? _detailContact;
    private PropertyChangedEventHandler? _detailChanged;

    internal AccountContactViewModel? DetailContact => _detailContact;

    partial void BuildDetail(Adw.Bin host)
    {
        var empty = host.GetChild();
        host.SetChild(null);
        _detailStack = Gtk.Stack.New();
        _detailStack.SetTransitionType(Gtk.StackTransitionType.None);
        if (empty is not null) _detailStack.AddNamed(empty, "empty");

        var column = Gtk.Box.New(Gtk.Orientation.Vertical, 14);
        column.SetMarginTop(18);
        column.SetMarginBottom(24);
        column.SetMarginStart(18);
        column.SetMarginEnd(18);

        var top = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        _detailBack = Gtk.Button.NewFromIconName("go-previous-symbolic");
        _detailBack.AddCssClass("flat");
        WinoA11y.LabelWithTooltip(_detailBack, Translator.Buttons_Back);
        _detailBack.SetVisible(false);
        top.Append(_detailBack);
        var spacer = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        spacer.SetHexpand(true);
        top.Append(spacer);
        _detailStarGlyph = WinoIcons.Label(WinoIconGlyph.Star, 18);
        _detailStar = Gtk.Button.New();
        _detailStar.SetChild(_detailStarGlyph);
        _detailStar.AddCssClass("flat");
        _detailStar.SetSizeRequest(28, 28);
        top.Append(_detailStar);
        column.Append(top);

        var identity = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        identity.SetHalign(Gtk.Align.Center);
        _detailPicture = new WinoContactPicture(DetailPictureSize);
        _detailPicture.Root.SetMarginBottom(6);
        identity.Append(_detailPicture.Root);
        _detailName = CenteredLabel("title-2");
        _detailName.SetSelectable(true);
        identity.Append(_detailName);
        _detailJob = CenteredLabel("dim-label");
        identity.Append(_detailJob);
        _detailSource = CenteredLabel("caption");
        _detailSource.AddCssClass("dim-label");
        identity.Append(_detailSource);
        _detailChips = new WinoCategoryChipRow();
        _detailChips.Root.SetHalign(Gtk.Align.Center);
        identity.Append(_detailChips.Root);
        column.Append(identity);

        var actions = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        actions.SetHalign(Gtk.Align.Center);
        _sendMail = Gtk.Button.New();
        _sendMail.SetChild(WinoIcons.ButtonContent(WinoIconGlyph.Mail, Translator.ContactAction_SendMail));
        _sendMail.AddCssClass("suggested-action");
        WinoA11y.Label(_sendMail, Translator.ContactAction_SendMail);
        actions.Append(_sendMail);
        _editContact = ActionButton(WinoIconGlyph.Edit, Translator.ContactAction_Edit);
        actions.Append(_editContact);
        _removeFromList = ActionButton(WinoIconGlyph.List, Translator.ContactAction_RemoveFromList);
        _removeFromList.SetVisible(false);
        actions.Append(_removeFromList);
        _deleteContact = ActionButton(WinoIconGlyph.Delete, Translator.ContactAction_Delete);
        actions.Append(_deleteContact);
        column.Append(actions);

        _detailCards = Gtk.Box.New(Gtk.Orientation.Vertical, 14);
        column.Append(_detailCards);

        var clamp = Adw.Clamp.New();
        clamp.SetMaximumSize(420);
        clamp.SetChild(column);
        _detailScroller = Gtk.ScrolledWindow.New();
        _detailScroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        _detailScroller.SetChild(clamp);
        _detailStack.AddNamed(_detailScroller, "detail");
        _detailStack.SetVisibleChildName("empty");
        host.SetChild(_detailStack);
    }

    partial void BindDetail()
    {
        if (_detailBack is null || _detailStar is null || _sendMail is null || _editContact is null || _removeFromList is null || _deleteContact is null) return;
        var self = _self;
        Bindings.Own(GtkSignals.OnClicked(_detailBack, () => self.Value?.ViewModel.ClearSelectedContactCommand.Execute(null)));
        Bindings.Own(GtkSignals.OnClicked(_detailStar, () => self.Value?.RunDetail(static (view, contact) => view.ToggleFavorite(contact))));
        Bindings.Own(GtkSignals.OnClicked(_sendMail, () => self.Value?.RunDetail(static (view, contact) =>
        {
            if (view.ViewModel.ComposeToContactCommand.CanExecute(contact)) view.ComposeTo([contact]);
        })));
        Bindings.Own(GtkSignals.OnClicked(_editContact, () => self.Value?.RunDetail(static (view, contact) => view.ViewModel.EditContactCommand.Execute(contact))));
        Bindings.Own(GtkSignals.OnClicked(_removeFromList, () => self.Value?.RunDetail(static (view, contact) =>
            view.Observe(view.ViewModel.RemoveFromCurrentListCommand.ExecuteAsync(contact)))));
        Bindings.Own(GtkSignals.OnClicked(_deleteContact, () => self.Value?.RunDetail(static (view, contact) =>
            view.Observe(view.ViewModel.DeleteContactCommand.ExecuteAsync(contact)))));

        Bind.Bind(nameof(ContactsPageViewModel.SelectedContact), static vm => vm.SelectedContact, ShowDetail);
        Bind.Bind(nameof(ContactsPageViewModel.SelectedFilter), static vm => vm.SelectedFilter?.IsList == true, _removeFromList.SetVisible);
        Action<Guid> loaded = id =>
        {
            if (self.Value is { _detailContact: { } shown } view && shown.ContactPictureFileId == id && view._detailPicture is { } picture)
                picture.Picture = view._pictures.Get(id);
        };
        _pictures.Loaded += loaded;
        Bindings.Own(new ActionDisposable(() =>
        {
            _pictures.Loaded -= loaded;
            ObserveDetailContact(null);
        }));
    }

    partial void LayoutStateChanged(EndPaneState state) => _detailBack?.SetVisible(state == EndPaneState.Swapped);

    private void RunDetail(Action<ContactsPageView, AccountContactViewModel> action)
    {
        if (!IsReleased && _detailContact is { } contact) action(this, contact);
    }

    private void ShowDetail(AccountContactViewModel? contact)
    {
        ObserveDetailContact(contact);
        _detailStack?.SetVisibleChildName(contact is null ? "empty" : "detail");
        if (contact is null) return;
        ApplyDetail(contact);
        _detailScroller?.GetVadjustment().SetValue(0);
    }

    private void ObserveDetailContact(AccountContactViewModel? contact)
    {
        if (_detailContact is { } previous && _detailChanged is { } handler) previous.PropertyChanged -= handler;
        _detailContact = contact;
        _detailChanged = null;
        if (contact is null) return;
        var self = _self;
        PropertyChangedEventHandler changed = (_, args) =>
        {
            if (args.PropertyName is not (null or "" or nameof(AccountContactViewModel.IsFavorite) or nameof(AccountContactViewModel.SourceContact)
                or nameof(AccountContactViewModel.Categories) or nameof(AccountContactViewModel.Name) or nameof(AccountContactViewModel.ContactPictureFileId))) return;
            self.Value?.RunOnUI(() =>
            {
                if (self.Value is { _detailContact: { } current } view && ReferenceEquals(current, contact)) view.ApplyDetail(current);
            });
        };
        _detailChanged = changed;
        contact.PropertyChanged += changed;
    }

    private void ApplyDetail(AccountContactViewModel contact)
    {
        if (_detailStar is null || _detailStarGlyph is null || _detailPicture is null || _detailName is null || _detailJob is null
            || _detailSource is null || _detailChips is null || _sendMail is null || _editContact is null || _deleteContact is null || _detailCards is null) return;
        var favorite = contact.IsFavorite;
        WinoIcons.SetGlyph(_detailStarGlyph, WinoIcons.Glyph(favorite ? WinoIconGlyph.StarFilled : WinoIconGlyph.Star));
        if (favorite) _detailStar.AddCssClass(WinoCssClass.FavoriteOn);
        else _detailStar.RemoveCssClass(WinoCssClass.FavoriteOn);
        WinoA11y.LabelWithTooltip(_detailStar, contact.FavoriteActionText ?? Translator.ContactAction_Favorite);

        _detailPicture.SetIdentity(contact.Name, contact.Address);
        _detailPicture.Picture = _pictures.Get(contact);
        _detailName.SetText(contact.Name ?? contact.Address ?? string.Empty);
        SetOptional(_detailJob, contact.JobTitleOrCompany);
        SetOptional(_detailSource, contact.SourceLabel);
        _detailChips.Apply(contact.SourceContact?.Categories is { Count: > 0 } categories ? [.. categories] : []);

        _sendMail.SetSensitive(ViewModel.ComposeToContactCommand.CanExecute(contact));
        _editContact.SetSensitive(contact.IsEditable);
        _deleteContact.SetSensitive(contact.IsEditable);

        while (_detailCards.GetFirstChild() is { } child) _detailCards.Remove(child);
        if (contact.SourceContact is not { } source) return;
        var email = new WinoContactFieldCard(Translator.ContactDetail_Email);
        foreach (var address in (source.EmailAddresses ?? []).OrderByDescending(static a => a.IsPrimary).ThenBy(static a => a.Order))
            email.Add(address.Address, address.Label, WinoIconGlyph.Mail);
        AddCard(email);
        var phone = new WinoContactFieldCard(Translator.ContactDetail_Phone);
        foreach (var number in (source.PhoneNumbers ?? []).OrderByDescending(static n => n.IsPrimary).ThenBy(static n => n.Order))
            phone.Add(number.Number, number.Kind.ToString(), WinoIconGlyph.Phone);
        AddCard(phone);
        var postal = new WinoContactFieldCard(Translator.ContactDetail_Address);
        foreach (var address in source.PostalAddresses ?? [])
            postal.Add(ContactDisplayFormatter.FormatPostalAddress(address), address.Kind.ToString(), WinoIconGlyph.Location, wrap: true);
        AddCard(postal);
        var personal = new WinoContactFieldCard(Translator.ContactDetail_Personal);
        personal.Add(ContactDisplayFormatter.FormatBirthday(source.BirthdayYear, source.BirthdayMonth, source.BirthdayDay), Translator.ContactDetail_Birthday);
        personal.Add(source.Website, Translator.ContactDetail_Website, WinoIconGlyph.Globe);
        personal.Add(source.Notes, Translator.ContactDetail_Notes, WinoIconGlyph.Note, wrap: true);
        AddCard(personal);
    }

    private void AddCard(WinoContactFieldCard card)
    {
        if (card.Count > 0) _detailCards?.Append(card.Root);
    }

    internal string DescribeDetail()
    {
        if (_detailContact is not { } contact) return "detail=none";
        var cards = new List<string>();
        for (var child = _detailCards?.GetFirstChild(); child is not null; child = child.GetNextSibling())
        {
            if (child.GetFirstChild() is Gtk.Label caption && caption.GetNextSibling() is Gtk.ListBox list)
            {
                var rows = 0;
                for (var row = list.GetFirstChild(); row is not null; row = row.GetNextSibling()) rows++;
                cards.Add($"{caption.GetText()}({rows})");
            }
        }
        return $"name={_detailName?.GetText()} job={_detailJob?.GetText()} source={_detailSource?.GetText()} chips={_detailChips?.Count} "
            + $"cards=[{string.Join(", ", cards)}] sendMail={_sendMail?.GetSensitive()} edit={_editContact?.GetSensitive()} "
            + $"removeFromList={_removeFromList?.GetVisible()} delete={_deleteContact?.GetSensitive()} back={_detailBack?.GetVisible()} "
            + $"star={(contact.IsFavorite ? "on" : "off")} shown={_detailStack?.GetVisibleChildName()}";
    }

    private static Gtk.Label CenteredLabel(string cssClass)
    {
        var label = Gtk.Label.New(string.Empty);
        label.AddCssClass(cssClass);
        label.SetWrap(true);
        label.SetJustify(Gtk.Justification.Center);
        label.SetHalign(Gtk.Align.Center);
        return label;
    }

    private static void SetOptional(Gtk.Label label, string? text)
    {
        label.SetText(text ?? string.Empty);
        label.SetVisible(!string.IsNullOrWhiteSpace(text));
    }

    private static Gtk.Button ActionButton(WinoIconGlyph glyph, string name)
    {
        var button = WinoIcons.Button(glyph, name, 16, flat: false);
        button.SetSizeRequest(36, 36);
        return button;
    }
}
