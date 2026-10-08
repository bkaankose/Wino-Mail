using System.ComponentModel;
using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Contacts;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Contacts;

public sealed partial class ContactsPageViewController
{
    private NSView _detailEmpty = null!;
    private NSScrollView _detailScroll = null!;
    private NSStackView _detailStack = null!;
    private NSButton _detailStar = null!;
    private WinoContactPicture _detailPicture = null!;
    private NSTextField _detailName = null!;
    private NSTextField _detailJob = null!;
    private NSTextField _detailSource = null!;
    private NSStackView _detailChipsHost = null!;
    private NSButton _sendMailButton = null!;
    private NSButton _editButton = null!;
    private NSButton _removeFromListButton = null!;
    private NSButton _deleteButton = null!;
    private NSStackView _detailCards = null!;
    private AccountContactViewModel? _detailContact;

    private NSView BuildDetailPane()
    {
        var pane = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };

        // Empty state (Windows WinoNoContactSelectedIllustration + caption).
        var emptyIcon = new WinoIconView(WinoIconGlyph.Person, 64, WinoStyle.TertiaryText);
        var emptyText = WinoStyle.Label(Translator.ContactsPage_NoContactSelected, WinoStyle.Body, WinoStyle.TertiaryText, 0);
        emptyText.Alignment = NSTextAlignment.Center;
        var emptyStack = WinoLayout.VStack(14, emptyIcon, emptyText);
        emptyStack.Alignment = NSLayoutAttribute.CenterX;
        _detailEmpty = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        _detailEmpty.AddSubview(emptyStack);
        WinoLayout.Fill(_detailEmpty, pane);
        NSLayoutConstraint.ActivateConstraints(
        [
            emptyStack.CenterXAnchor.ConstraintEqualTo(_detailEmpty.CenterXAnchor),
            emptyStack.CenterYAnchor.ConstraintEqualTo(_detailEmpty.CenterYAnchor),
            emptyStack.WidthAnchor.ConstraintLessThanOrEqualTo(_detailEmpty.WidthAnchor, 1, -48)
        ]);

        // Star, top right.
        _detailStar = new NSButton { Bordered = false, TranslatesAutoresizingMaskIntoConstraints = false };
        _detailStar.SetButtonType(NSButtonType.MomentaryChange);
        _detailStar.ImagePosition = NSCellImagePosition.ImageOnly;
        _detailStar.Activated += (_, _) => { if (_detailContact is { } contact) ToggleFavorite(contact); };
        WinoLayout.Size(_detailStar, 28, 28);
        var starRow = WinoLayout.HStack(0, WinoLayout.Spacer(), _detailStar);

        // Identity block, centred.
        _detailPicture = new WinoContactPicture(88);
        _detailName = WinoStyle.Label(string.Empty, NSFont.SystemFontOfSize(20, NSFontWeight.Semibold), maximumLines: 0);
        _detailName.Alignment = NSTextAlignment.Center;
        _detailJob = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        _detailJob.Alignment = NSTextAlignment.Center;
        _detailSource = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.TertiaryText, 0);
        _detailSource.Alignment = NSTextAlignment.Center;
        _detailChipsHost = WinoLayout.HStack(4);
        var identity = WinoLayout.VStack(6, _detailPicture, _detailName, _detailJob, _detailSource, _detailChipsHost);
        identity.Alignment = NSLayoutAttribute.CenterX;
        identity.EdgeInsets = new NSEdgeInsets(-8, 0, 0, 0);
        identity.SetCustomSpacing(10, _detailPicture);

        // Actions (Windows order: Send mail accent, Edit, Remove from list, Delete).
        _sendMailButton = CommandButton(Translator.ContactAction_SendMail, ViewModel.ComposeToContactCommand, () => _detailContact);
        _sendMailButton.Image = WinoIcons.Image(WinoIconGlyph.Mail, 14);
        _sendMailButton.ImagePosition = NSCellImagePosition.ImageLeading;
        _sendMailButton.BezelColor = WinoStyle.Accent;
        _sendMailButton.ControlSize = NSControlSize.Regular;
        _editButton = IconCommandButton(WinoIconGlyph.Edit, Translator.ContactAction_Edit, ViewModel.EditContactCommand);
        _removeFromListButton = IconCommandButton(WinoIconGlyph.List, Translator.ContactAction_RemoveFromList, ViewModel.RemoveFromCurrentListCommand);
        _deleteButton = IconCommandButton(WinoIconGlyph.Delete, Translator.ContactAction_Delete, ViewModel.DeleteContactCommand);
        var actions = WinoLayout.HStack(6, _sendMailButton, _editButton, _removeFromListButton, _deleteButton);
        var actionsRow = WinoLayout.HStack(0, WinoLayout.Spacer(), actions, WinoLayout.Spacer());

        _detailCards = WinoLayout.VStack(14);
        _detailStack = WinoLayout.VStack(14, starRow, identity, actionsRow, _detailCards);
        _detailStack.EdgeInsets = new NSEdgeInsets(18, 18, 24, 18);

        var document = new FlippedView();
        document.AddSubview(_detailStack);
        _detailScroll = new NSScrollView { DocumentView = document, HasVerticalScroller = true, AutohidesScrollers = true, DrawsBackground = false, TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        WinoLayout.Fill(_detailScroll, pane);
        NSLayoutConstraint.ActivateConstraints(
        [
            document.WidthAnchor.ConstraintEqualTo(_detailScroll.ContentView.WidthAnchor),
            _detailStack.LeadingAnchor.ConstraintEqualTo(document.LeadingAnchor),
            _detailStack.TrailingAnchor.ConstraintEqualTo(document.TrailingAnchor),
            _detailStack.TopAnchor.ConstraintEqualTo(document.TopAnchor),
            _detailStack.BottomAnchor.ConstraintEqualTo(document.BottomAnchor),
            starRow.WidthAnchor.ConstraintEqualTo(_detailStack.WidthAnchor, 1, -36),
            identity.WidthAnchor.ConstraintEqualTo(_detailStack.WidthAnchor, 1, -36),
            actionsRow.WidthAnchor.ConstraintEqualTo(_detailStack.WidthAnchor, 1, -36),
            _detailCards.WidthAnchor.ConstraintEqualTo(_detailStack.WidthAnchor, 1, -36),
            _detailName.WidthAnchor.ConstraintLessThanOrEqualTo(identity.WidthAnchor),
            _detailJob.WidthAnchor.ConstraintLessThanOrEqualTo(identity.WidthAnchor),
            _detailSource.WidthAnchor.ConstraintLessThanOrEqualTo(identity.WidthAnchor)
        ]);
        return pane;
    }

    private NSButton IconCommandButton(WinoIconGlyph glyph, string tooltip, System.Windows.Input.ICommand command)
    {
        var button = CommandButton(string.Empty, command, () => _detailContact);
        button.Image = WinoIcons.Image(glyph, 14);
        button.ImagePosition = NSCellImagePosition.ImageOnly;
        button.ToolTip = tooltip;
        WinoAccessibility.Label(button, tooltip);
        WinoLayout.Size(button, 36);
        return button;
    }

    private void BindDetail()
    {
        Bind(nameof(ViewModel.SelectedContact), vm => vm.SelectedContact, ShowDetail);
        Bind(nameof(ViewModel.SelectedFilter), vm => vm.SelectedFilter?.IsList == true, isList => _removeFromListButton.Hidden = !isList);
        WinoStyle.AccentChanged += DetailAccentChanged;
        Bindings.Own(new ActionDisposable(() =>
        {
            WinoStyle.AccentChanged -= DetailAccentChanged;
            ObserveDetailContact(null);
        }));
    }

    private void DetailAccentChanged(object? sender, EventArgs args) => _sendMailButton.BezelColor = WinoStyle.Accent;

    private void ObserveDetailContact(AccountContactViewModel? contact)
    {
        if (_detailContact is not null) _detailContact.PropertyChanged -= DetailContactChanged;
        _detailContact = contact;
        if (contact is not null) contact.PropertyChanged += DetailContactChanged;
    }

    private void DetailContactChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (null or "" or nameof(AccountContactViewModel.IsFavorite) or nameof(AccountContactViewModel.SourceContact)
            or nameof(AccountContactViewModel.Categories) or nameof(AccountContactViewModel.Name) or nameof(AccountContactViewModel.ContactPictureFileId))) return;
        OnUI(() => { if (_detailContact is { } contact) ApplyDetail(contact); });
    }

    private void ShowDetail(AccountContactViewModel? contact)
    {
        ObserveDetailContact(contact);
        _detailEmpty.Hidden = contact is not null;
        _detailScroll.Hidden = contact is null;
        if (contact is null) return;
        ApplyDetail(contact);
        _detailScroll.ContentView.ScrollToPoint(new CoreGraphics.CGPoint(0, 0));
    }

    private void ApplyDetail(AccountContactViewModel contact)
    {
        var source = contact.SourceContact;
        _detailStar.Image = WinoContactStyle.StarImage(contact.IsFavorite, 17);
        _detailStar.ToolTip = contact.FavoriteActionText;
        WinoAccessibility.Label(_detailStar, contact.FavoriteActionText);

        _detailPicture.SetIdentity(contact.Name, contact.Address);
        _detailPicture.Image = LoadPicture(contact);
        _detailName.StringValue = contact.Name ?? string.Empty;
        _detailJob.StringValue = contact.JobTitleOrCompany ?? string.Empty;
        _detailJob.Hidden = string.IsNullOrWhiteSpace(_detailJob.StringValue);
        _detailSource.StringValue = contact.SourceLabel ?? string.Empty;
        foreach (var view in _detailChipsHost.ArrangedSubviews) view.RemoveFromSuperview();
        foreach (var category in contact.Categories) _detailChipsHost.AddArrangedSubview(new WinoCategoryChip(category));
        _detailChipsHost.Hidden = !contact.HasCategories;

        _editButton.Enabled = contact.IsEditable && ViewModel.EditContactCommand.CanExecute(contact);
        _deleteButton.Enabled = contact.IsEditable && ViewModel.DeleteContactCommand.CanExecute(contact);
        _sendMailButton.Enabled = ViewModel.ComposeToContactCommand.CanExecute(contact);

        foreach (var view in _detailCards.ArrangedSubviews) view.RemoveFromSuperview();
        var email = new WinoContactFieldCard(Translator.ContactDetail_Email);
        foreach (var address in source.EmailAddresses.OrderByDescending(item => item.IsPrimary).ThenBy(item => item.Order))
            email.Add(address.Address, address.Label, WinoIconGlyph.Mail);
        AddCard(email);

        var phone = new WinoContactFieldCard(Translator.ContactDetail_Phone);
        foreach (var number in source.PhoneNumbers.OrderByDescending(item => item.IsPrimary).ThenBy(item => item.Order))
            phone.Add(number.Number, number.Kind.ToString(), WinoIconGlyph.Phone);
        AddCard(phone);

        var postalCard = new WinoContactFieldCard(Translator.ContactDetail_Address);
        foreach (var postal in source.PostalAddresses)
            postalCard.Add(FormatAddress(postal), postal.Kind.ToString(), WinoIconGlyph.Location, wrap: true);
        AddCard(postalCard);

        // Windows always shows the Personal card; on Mac an empty card is dropped like the others.
        var personal = new WinoContactFieldCard(Translator.ContactDetail_Personal);
        personal.Add(FormatBirthday(source.BirthdayYear, source.BirthdayMonth, source.BirthdayDay), Translator.ContactDetail_Birthday);
        personal.Add(source.Website, Translator.ContactDetail_Website, WinoIconGlyph.Globe);
        personal.Add(source.Notes, Translator.ContactDetail_Notes, WinoIconGlyph.Note, wrap: true);
        AddCard(personal);
    }

    private void AddCard(WinoContactFieldCard card)
    {
        if (card.Count == 0) return;
        _detailCards.AddArrangedSubview(card);
        card.WidthAnchor.ConstraintEqualTo(_detailCards.WidthAnchor).Active = true;
    }

    private static string FormatAddress(ContactPostalAddress address)
    {
        var line2 = string.Join(" ", new[] { address.PostalCode, address.City }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return string.Join("\n", new[] { address.PostOfficeBox, address.Street, line2, address.Region, address.Country }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    /// <summary>Same rule as the Windows XamlHelpers.FormatBirthday: "14 March" or "14 March 1990".</summary>
    internal static string FormatBirthday(int? year, int? month, int? day)
    {
        if (month is not (>= 1 and <= 12) || day is not (>= 1 and <= 31)) return string.Empty;
        var date = new DateTime(year ?? 2000, month.Value, Math.Min(day.Value, DateTime.DaysInMonth(year ?? 2000, month.Value)));
        return year.HasValue ? date.ToString("d MMMM yyyy") : date.ToString("d MMMM");
    }

    /// <summary>Top-anchored document view for scroll content.</summary>
    private sealed class FlippedView : NSView
    {
        public FlippedView() => TranslatesAutoresizingMaskIntoConstraints = false;
        public override bool IsFlipped => true;
    }
}
