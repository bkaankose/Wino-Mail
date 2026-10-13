using System.ComponentModel;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Contacts;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Contacts;

/// <summary>
/// The contact card (Windows ContactsPage detail pane, Mac ContactsPageViewController.Detail) over the shared
/// <see cref="ContactsPageViewModel.SelectedContact"/>: the favourite star, the 88pt picture, name, job, source and
/// category chips, the action row (Send mail, Call and FaceTime through <see cref="IExternalLauncher"/>, Edit) and the
/// read-only field cards (Email, Phone, Address, Personal), with Remove from this list and Delete at the foot. The iPhone
/// pushes it; the iPad hosts it in the reader column, where it shows an empty state while nothing is selected.
/// </summary>
public sealed class ContactCardViewController : UIViewController
{
    private readonly ContactsPageViewModel _viewModel;
    private readonly IDispatcher _dispatcher;
    private readonly IExternalLauncher _launcher;
    private readonly Func<AccountContactViewModel, UIImage?> _picture;
    private readonly Action<Exception> _error;
    private readonly bool _showsEmptyState;
    private readonly BindingScope _bindings = new();
    private readonly UIScrollView _scroll = new() { TranslatesAutoresizingMaskIntoConstraints = false, AlwaysBounceVertical = true };
    private readonly UIStackView _stack = WinoLayout.VStack(16);
    private readonly WinoStateView _empty = new(WinoIconGlyph.Person, string.Empty, Translator.ContactsPage_NoContactSelected);
    private readonly UIButton _star = new(UIButtonType.System);
    private readonly WinoContactPicture _avatar = new(88);
    private readonly UILabel _name = WinoStyle.Label(string.Empty, WinoStyle.Scaled(22, UIFontWeight.Semibold), maximumLines: 0);
    private readonly UILabel _job = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText, 0);
    private readonly UILabel _source = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.TertiaryText, 0);
    private readonly UIStackView _chips = WinoLayout.HStack(4);
    private readonly UIStackView _actions = WinoLayout.HStack(10);
    private readonly UIStackView _cards = WinoLayout.VStack(14);
    private readonly UIStackView _footer = WinoLayout.VStack(8);
    private AccountContactViewModel? _contact;
    private bool _released;

    internal ContactCardViewController(ContactsPageViewModel viewModel, IDispatcher dispatcher, IExternalLauncher launcher,
        Func<AccountContactViewModel, UIImage?> picture, Action<Exception> error, bool showsEmptyState)
    {
        _viewModel = viewModel;
        _dispatcher = dispatcher;
        _launcher = launcher;
        _picture = picture;
        _error = error;
        _showsEmptyState = showsEmptyState;
    }

    /// <summary>The contact on screen.</summary>
    public AccountContactViewModel? Contact => _contact;

    /// <summary>Raised when the selection empties (the iPhone pops the card).</summary>
    public event EventHandler? SelectionCleared;

    public override void LoadView()
    {
        var root = new UIView { BackgroundColor = UIColor.Clear };
        var content = root;
        // The iPad reader column already frames its page in a Wino zone; the iPhone push gets its own.
        if (!_showsEmptyState)
        {
            var zone = new WinoZoneView();
            root.AddSubview(zone);
            var guide = root.SafeAreaLayoutGuide;
            NSLayoutConstraint.ActivateConstraints(
            [
                zone.TopAnchor.ConstraintEqualTo(guide.TopAnchor, 2),
                zone.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, (nfloat)WinoStyle.ZoneGutter),
                zone.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -(nfloat)WinoStyle.ZoneGutter),
                zone.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor, -(nfloat)WinoStyle.ZoneGutter)
            ]);
            content = zone.ContentView;
        }

        _star.PrimaryActionTriggered += (_, _) => { if (_contact is { } contact) Observe(_viewModel.ToggleFavoriteCommand.ExecuteAsync(contact)); };
        WinoLayout.Size(_star, 44, 44);
        var starRow = WinoLayout.HStack(0, WinoLayout.Spacer(), _star);

        _name.TextAlignment = UITextAlignment.Center;
        _job.TextAlignment = UITextAlignment.Center;
        _source.TextAlignment = UITextAlignment.Center;
        var identity = WinoLayout.VStack(6, _avatar, _name, _job, _source, _chips);
        identity.Alignment = UIStackViewAlignment.Center;
        identity.SetCustomSpacing(12, _avatar);

        _actions.Distribution = UIStackViewDistribution.FillEqually;
        _cards.Alignment = UIStackViewAlignment.Fill;
        _footer.Alignment = UIStackViewAlignment.Fill;
        _stack.Alignment = UIStackViewAlignment.Fill;
        foreach (var view in new UIView[] { starRow, identity, _actions, _cards, _footer }) _stack.AddArrangedSubview(view);
        _stack.SetCustomSpacing(-12, starRow);
        WinoLayout.Insets(_stack, 8, 18, 28, 18);

        _scroll.AddSubview(_stack);
        WinoLayout.Fill(_scroll, content);
        NSLayoutConstraint.ActivateConstraints(
        [
            _stack.TopAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.TopAnchor),
            _stack.BottomAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.BottomAnchor),
            _stack.LeadingAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.LeadingAnchor),
            _stack.TrailingAnchor.ConstraintEqualTo(_scroll.ContentLayoutGuide.TrailingAnchor),
            _stack.WidthAnchor.ConstraintEqualTo(_scroll.FrameLayoutGuide.WidthAnchor)
        ]);
        _empty.Place(content);
        View = root;
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        _bindings.Own(new PropertyBinding<ContactsPageViewModel, AccountContactViewModel?>(_viewModel, nameof(ContactsPageViewModel.SelectedContact),
            static vm => vm.SelectedContact, Show, _dispatcher, _error));
        _bindings.Own(new PropertyBinding<ContactsPageViewModel, bool>(_viewModel, nameof(ContactsPageViewModel.SelectedFilter),
            static vm => vm.SelectedFilter?.IsList == true, _ => { if (_contact is { } contact) Apply(contact); }, _dispatcher, _error));
        _bindings.Own(new ActionDisposable(() => Follow(null)));
    }

    private void Show(AccountContactViewModel? contact)
    {
        if (_released) return;
        Follow(contact);
        _empty.Hidden = contact is not null || !_showsEmptyState;
        _scroll.Hidden = contact is null;
        if (contact is null)
        {
            SelectionCleared?.Invoke(this, EventArgs.Empty);
            return;
        }
        Apply(contact);
        _scroll.SetContentOffset(new CoreGraphics.CGPoint(0, -_scroll.AdjustedContentInset.Top), false);
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
        _ = _dispatcher.ExecuteOnUIThread(() => { if (!_released && _contact is { } current) Apply(current); });
    }

    private void Apply(AccountContactViewModel contact)
    {
        var source = contact.SourceContact;
        var starConfiguration = UIButtonConfiguration.PlainButtonConfiguration;
        starConfiguration.Image = ModeBarItems.Star(contact.IsFavorite, 22);
        _star.Configuration = starConfiguration;
        WinoAccessibility.Label(_star, contact.FavoriteActionText);

        _avatar.SetIdentity(contact.Name, contact.Address);
        _avatar.Image = _picture(contact);
        _name.Text = contact.Name ?? string.Empty;
        _job.Text = contact.JobTitleOrCompany ?? string.Empty;
        _job.Hidden = string.IsNullOrWhiteSpace(_job.Text);
        _source.Text = contact.SourceLabel ?? string.Empty;
        foreach (var view in _chips.ArrangedSubviews) { _chips.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        foreach (var category in contact.Categories) _chips.AddArrangedSubview(new WinoCategoryChip(category));
        _chips.Hidden = !contact.HasCategories;
        Title = contact.Name;

        ApplyActions(contact, source);
        ApplyCards(source);
        ApplyFooter(contact);
    }

    /// <summary>The action row: Send mail (accent), Call and FaceTime when the contact has a number or address, Edit.</summary>
    private void ApplyActions(AccountContactViewModel contact, AccountContact source)
    {
        foreach (var view in _actions.ArrangedSubviews) { _actions.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        var phone = source.PrimaryPhoneNumber;
        var email = source.PrimaryEmailAddress;
        _actions.AddArrangedSubview(ActionButton(WinoIconGlyph.Mail, Translator.ContactAction_SendMail, true,
            _viewModel.ComposeToContactCommand.CanExecute(contact), () => _viewModel.ComposeToContactCommand.Execute(contact)));
        if (!string.IsNullOrWhiteSpace(phone))
            _actions.AddArrangedSubview(ActionButton(WinoIconGlyph.Phone, Translator.IOS_ContactAction_Call, false, true, () => Launch("tel", phone)));
        var faceTime = !string.IsNullOrWhiteSpace(phone) ? phone : email;
        if (!string.IsNullOrWhiteSpace(faceTime))
            _actions.AddArrangedSubview(ActionButton(WinoIconGlyph.Camera, Translator.IOS_ContactAction_FaceTime, false, true, () => Launch("facetime", faceTime)));
        _actions.AddArrangedSubview(ActionButton(WinoIconGlyph.Edit, Translator.Buttons_Edit, false,
            contact.IsEditable && _viewModel.EditContactCommand.CanExecute(contact), () => _viewModel.EditContactCommand.Execute(contact)));
    }

    private static UIButton ActionButton(WinoIconGlyph glyph, string title, bool accent, bool enabled, Action run)
    {
        var configuration = accent ? UIButtonConfiguration.FilledButtonConfiguration : UIButtonConfiguration.TintedButtonConfiguration;
        configuration.Image = ModeBarItems.Glyph(glyph, 20);
        configuration.ImagePlacement = NSDirectionalRectEdge.Top;
        configuration.ImagePadding = 4;
        configuration.AttributedTitle = new NSAttributedString(title, new UIStringAttributes { Font = WinoStyle.CaptionStrong });
        configuration.TitleLineBreakMode = UILineBreakMode.TailTruncation;
        configuration.ButtonSize = UIButtonConfigurationSize.Small;
        configuration.CornerStyle = UIButtonConfigurationCornerStyle.Large;
        if (accent) configuration.BaseBackgroundColor = WinoStyle.Accent;
        else configuration.BaseForegroundColor = WinoStyle.Accent;
        var button = new UIButton { Configuration = configuration, Enabled = enabled, TranslatesAutoresizingMaskIntoConstraints = false };
        button.PrimaryActionTriggered += (_, _) => run();
        button.HeightAnchor.ConstraintGreaterThanOrEqualTo(58).Active = true;
        WinoAccessibility.Label(button, title);
        return button;
    }

    private void Launch(string scheme, string value)
    {
        var target = scheme == "tel" || value.IndexOf('@') < 0 ? new string(value.Where(ch => char.IsDigit(ch) || ch is '+' or '*' or '#').ToArray()) : value;
        if (string.IsNullOrEmpty(target) || !Uri.TryCreate($"{scheme}:{target}", UriKind.Absolute, out var uri)) return;
        Observe(_launcher.LaunchUriAsync(uri));
    }

    /// <summary>The read-only field cards in the Windows order; an empty card is left out (Mac rule).</summary>
    private void ApplyCards(AccountContact source)
    {
        foreach (var view in _cards.ArrangedSubviews) { _cards.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        var email = new WinoContactFieldCard(Translator.ContactDetail_Email);
        foreach (var address in source.EmailAddresses.OrderByDescending(item => item.IsPrimary).ThenBy(item => item.Order))
            email.Add(address.Address, address.Label, WinoIconGlyph.Mail);
        AddCard(email);

        var phone = new WinoContactFieldCard(Translator.ContactDetail_Phone);
        foreach (var number in source.PhoneNumbers.OrderByDescending(item => item.IsPrimary).ThenBy(item => item.Order))
            phone.Add(number.Number, number.Kind.ToString(), WinoIconGlyph.Phone);
        AddCard(phone);

        var postal = new WinoContactFieldCard(Translator.ContactDetail_Address);
        foreach (var address in source.PostalAddresses)
            postal.Add(FormatAddress(address), address.Kind.ToString(), WinoIconGlyph.Location, wrap: true);
        AddCard(postal);

        var personal = new WinoContactFieldCard(Translator.ContactDetail_Personal);
        personal.Add(ContactDisplayFormatter.FormatBirthday(source.BirthdayYear, source.BirthdayMonth, source.BirthdayDay), Translator.ContactDetail_Birthday);
        personal.Add(source.Website, Translator.ContactDetail_Website, WinoIconGlyph.Globe);
        personal.Add(source.Notes, Translator.ContactDetail_Notes, WinoIconGlyph.Note, wrap: true);
        AddCard(personal);
    }

    private void AddCard(WinoContactFieldCard card)
    {
        if (card.Count > 0) _cards.AddArrangedSubview(card);
    }

    private static string FormatAddress(ContactPostalAddress address)
    {
        var line2 = string.Join(" ", new[] { address.PostalCode, address.City }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return string.Join("\n", new[] { address.PostOfficeBox, address.Street, line2, address.Region, address.Country }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    /// <summary>Remove from this list (list filters only) and Delete, as plain destructive rows.</summary>
    private void ApplyFooter(AccountContactViewModel contact)
    {
        foreach (var view in _footer.ArrangedSubviews) { _footer.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        if (_viewModel.SelectedFilter?.IsList == true)
            _footer.AddArrangedSubview(FooterButton(WinoIconGlyph.List, Translator.ContactAction_RemoveFromList, WinoStyle.Accent, true,
                () => Observe(_viewModel.RemoveFromCurrentListCommand.ExecuteAsync(contact))));
        _footer.AddArrangedSubview(FooterButton(WinoIconGlyph.Delete, Translator.ContactAction_Delete, WinoStyle.Critical,
            contact.IsEditable && _viewModel.DeleteContactCommand.CanExecute(contact), () => Observe(_viewModel.DeleteContactCommand.ExecuteAsync(contact))));
    }

    private static UIButton FooterButton(WinoIconGlyph glyph, string title, UIColor tint, bool enabled, Action run)
    {
        var configuration = UIButtonConfiguration.GrayButtonConfiguration;
        configuration.Image = ModeBarItems.Glyph(glyph, 18);
        configuration.ImagePadding = 8;
        configuration.Title = title;
        configuration.BaseForegroundColor = tint;
        configuration.CornerStyle = UIButtonConfigurationCornerStyle.Large;
        var button = new UIButton { Configuration = configuration, Enabled = enabled, TranslatesAutoresizingMaskIntoConstraints = false };
        button.HorizontalAlignment = UIControlContentHorizontalAlignment.Leading;
        button.HeightAnchor.ConstraintGreaterThanOrEqualTo(44).Active = true;
        button.PrimaryActionTriggered += (_, _) => run();
        return button;
    }

    private async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { _error(exception); }
    }

    /// <summary>Stops following the ViewModel (the page released or the card left its host).</summary>
    public void Release()
    {
        if (_released) return;
        _released = true;
        _bindings.Dispose();
        SelectionCleared = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Release();
        base.Dispose(disposing);
    }
}
