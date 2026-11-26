using CoreGraphics;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Onboarding;

/// <summary>Small factories shared by the setup page's steps (the Mac AccountSetupUi).</summary>
internal static class AccountSetupUi
{
    public static UILabel Caption(string? text = null)
        => WinoStyle.Label(text ?? string.Empty, WinoStyle.Description, WinoStyle.SecondaryText, 0);

    public static UILabel Error()
        => WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.Critical, 0);

    public static UILabel Header(string text) => WinoStyle.Label(text, WinoStyle.Subheadline, WinoStyle.PrimaryText, 0);

    /// <summary>A borderless accent link (Windows HyperlinkButton).</summary>
    public static UIButton Link(string title, UIFont? font = null)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(6, 0, 6, 0);
        configuration.BaseForegroundColor = WinoStyle.Accent;
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        button.HorizontalAlignment = UIControlContentHorizontalAlignment.Leading;
        SetTitle(button, title, font);
        return button;
    }

    /// <summary>Sets a configured button's title, keeping its font (a link's smaller caption size).</summary>
    public static void SetTitle(UIButton button, string? title, UIFont? font = null)
    {
        if (button.Configuration is not { } configuration) return;
        var text = title ?? string.Empty;
        var attributes = new UIStringAttributes { Font = font ?? WinoStyle.Subheadline };
        configuration.AttributedTitle = new Foundation.NSAttributedString(text, attributes);
        configuration.TitleLineBreakMode = UILineBreakMode.WordWrap;
        button.Configuration = configuration;
        button.AccessibilityLabel = text;
    }

    public static UIActivityIndicatorView Spinner()
        => new(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = true, TranslatesAutoresizingMaskIntoConstraints = false };

    public static void SetSpinning(UIActivityIndicatorView spinner, bool spinning)
    {
        if (spinning == spinner.IsAnimating) return;
        if (spinning) spinner.StartAnimating();
        else spinner.StopAnimating();
    }

    /// <summary>A vertical stack whose arranged views all take its width.</summary>
    public static UIStackView Column(double spacing, params UIView[] views)
    {
        var stack = WinoLayout.VStack(spacing, views);
        stack.Alignment = UIStackViewAlignment.Fill;
        return stack;
    }

    /// <summary>A leading row of views that keeps its natural width inside a full-width column.</summary>
    public static UIView Leading(params UIView[] views)
    {
        var row = WinoLayout.HStack(WinoStyle.Space2, views);
        row.Alignment = UIStackViewAlignment.Center;
        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        host.AddSubview(row);
        NSLayoutConstraint.ActivateConstraints(
        [
            row.TopAnchor.ConstraintEqualTo(host.TopAnchor),
            row.BottomAnchor.ConstraintEqualTo(host.BottomAnchor),
            row.LeadingAnchor.ConstraintEqualTo(host.LeadingAnchor),
            row.TrailingAnchor.ConstraintLessThanOrEqualTo(host.TrailingAnchor)
        ]);
        return host;
    }

    /// <summary>A button with a provider mark before its title (Windows SocialSignInButtonStyle): an outlined pill.</summary>
    public static UIButton ProviderButton()
    {
        var configuration = UIButtonConfiguration.BorderedButtonConfiguration;
        configuration.BaseBackgroundColor = UIColor.SystemBackground;
        configuration.BaseForegroundColor = WinoStyle.PrimaryText;
        configuration.CornerStyle = UIButtonConfigurationCornerStyle.Capsule;
        configuration.ImagePadding = 8;
        configuration.TitleLineBreakMode = UILineBreakMode.TailTruncation;
        configuration.Background.StrokeColor = UIColor.Separator;
        configuration.Background.StrokeWidth = 1;
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        button.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        return button;
    }

    /// <summary>Title and image of a configured button in one change.</summary>
    public static void SetContent(UIButton button, string? title, UIImage? image)
    {
        if (button.Configuration is not { } configuration) return;
        configuration.Title = title ?? string.Empty;
        configuration.Image = image;
        button.Configuration = configuration;
        button.AccessibilityLabel = title;
    }
}

/// <summary>
/// Lays out its visible subviews in lines that wrap (Windows WinoWrapPanel), leading-aligned or centred. Hidden
/// subviews take no room; call <see cref="Reflow"/> after showing or hiding one.
/// </summary>
internal sealed class AccountSetupFlowView : UIView
{
    private nfloat _height;

    public AccountSetupFlowView(bool centered = false, double spacing = 8, double lineSpacing = 8)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Centered = centered;
        Spacing = spacing;
        LineSpacing = lineSpacing;
    }

    public bool Centered { get; }
    public double Spacing { get; }
    public double LineSpacing { get; }

    public void AddItem(UIView view)
    {
        view.TranslatesAutoresizingMaskIntoConstraints = true;
        AddSubview(view);
        Reflow();
    }

    public void Reflow()
    {
        SetNeedsLayout();
        InvalidateIntrinsicContentSize();
    }

    /// <summary>True when any subview is shown.</summary>
    public bool HasVisibleItems => Subviews.Any(view => !view.Hidden);

    private nfloat Arrange(nfloat width, bool apply)
    {
        if (width <= 0) return 0;
        var lines = new List<List<(UIView View, CGSize Size)>>();
        var current = new List<(UIView View, CGSize Size)>();
        nfloat x = 0;
        foreach (var view in Subviews)
        {
            if (view.Hidden) continue;
            var size = view.SystemLayoutSizeFittingSize(UIView.UILayoutFittingCompressedSize);
            size.Width = (nfloat)Math.Min(Math.Ceiling(size.Width), width);
            size.Height = (nfloat)Math.Ceiling(size.Height);
            if (current.Count > 0 && x + size.Width > width)
            {
                lines.Add(current);
                current = new();
                x = 0;
            }
            current.Add((view, size));
            x += size.Width + (nfloat)Spacing;
        }
        if (current.Count > 0) lines.Add(current);

        nfloat y = 0;
        foreach (var line in lines)
        {
            nfloat lineHeight = line.Max(item => item.Size.Height);
            nfloat lineWidth = (nfloat)line.Sum(item => (double)item.Size.Width) + (nfloat)Spacing * (line.Count - 1);
            nfloat left = Centered ? (nfloat)Math.Floor((width - lineWidth) / 2) : 0;
            if (apply)
            {
                foreach (var (view, size) in line)
                {
                    view.Frame = new CGRect(left, y + (lineHeight - size.Height) / 2, size.Width, size.Height);
                    left += size.Width + (nfloat)Spacing;
                }
            }
            y += lineHeight + (nfloat)LineSpacing;
        }
        return lines.Count == 0 ? 0 : y - (nfloat)LineSpacing;
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        var height = Arrange(Bounds.Width, true);
        if (height != _height)
        {
            _height = height;
            InvalidateIntrinsicContentSize();
        }
    }

    public override CGSize IntrinsicContentSize
        => new(NoIntrinsicMetric, Bounds.Width > 0 ? Arrange(Bounds.Width, false) : (HasVisibleItems ? 36 : 0));
}

/// <summary>
/// One mark of the address step's provider shelf (Windows AccountSetupProviderShelfItem template): a 36pt circle with the
/// brand glyph, or a capsule with a label for IMAP and POP3. The matched provider gets an accent ring and the others dim.
/// It is a picture, not a button.
/// </summary>
internal sealed class AccountSetupShelfItemView : WinoSurfaceView
{
    private const double Height = 36;
    private readonly WinoIconView _glyph = new(WinoIconGlyph.None, 18);
    private readonly UILabel _label = WinoStyle.Label(string.Empty, WinoStyle.CaptionStrong);
    private AccountSetupProviderShelfItem? _item;

    public AccountSetupShelfItemView()
    {
        Fill = WinoSettingsStyle.SubtleFill;
        CornerRadius = Height / 2;
        AddSubview(_glyph);
        AddSubview(_label);
        WinoLayout.Size(_glyph, 18, 18);
        _label.TranslatesAutoresizingMaskIntoConstraints = false;
        NSLayoutConstraint.ActivateConstraints(
        [
            _glyph.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            _glyph.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            _label.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            _label.CenterYAnchor.ConstraintEqualTo(CenterYAnchor)
        ]);
        IsAccessibilityElement = true;
        AccessibilityTraits = UIAccessibilityTrait.Image;
    }

    /// <summary>A circle for a brand mark; a capsule as wide as its label otherwise.</summary>
    public override CGSize IntrinsicContentSize => _item is { HasLabel: true }
        ? new CGSize((nfloat)Math.Ceiling(_label.IntrinsicContentSize.Width + 24), Height)
        : new CGSize(Height, Height);

    public override CGSize SystemLayoutSizeFittingSize(CGSize size) => IntrinsicContentSize;

    public void Apply(AccountSetupProviderShelfItem item)
    {
        if (_item == item) return;
        _item = item;
        _glyph.Hidden = !item.HasGlyph;
        _glyph.Icon = item.HasGlyph ? item.Glyph : WinoIconGlyph.None;
        _label.Hidden = !item.HasLabel;
        _label.Text = item.Label;
        InvalidateIntrinsicContentSize();
        Stroke = item.IsMatched ? WinoStyle.Accent : null;
        StrokeWidth = item.IsMatched ? 2 : 0;
        Alpha = item.IsDimmed ? 0.4f : 1f;
        AccessibilityLabel = item.Title;
    }
}

/// <summary>
/// One row of the sign-in method and server kind lists (Windows AccountSetupChoiceTemplate in a single-selection
/// ItemsView): a card with the option's mark when it has one, its label, its "(recommended)" caption and its
/// description, outlined in the accent colour when selected.
/// </summary>
internal sealed class AccountSetupChoiceCard : WinoPressableView
{
    private readonly WinoIconView _check = new(WinoIconGlyph.Checkmark, 16, WinoStyle.Accent) { Colorful = false };
    private bool _isSelected;

    public AccountSetupChoiceCard(AccountSetupChoice choice)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Choice = choice;
        Fill = WinoSettingsStyle.GroupFill;
        CornerRadius = 10;
        var label = WinoStyle.Label(choice.Label, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        var caption = WinoStyle.Label(choice.Caption, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        caption.Hidden = !choice.HasCaption;
        var description = WinoStyle.Label(choice.Description, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        description.Hidden = !choice.HasDescription;
        var text = WinoLayout.VStack(2, label, caption, description);
        text.Alignment = UIStackViewAlignment.Leading;
        WinoLayout.Size(_check, 18, 18);
        var row = WinoLayout.HStack(WinoStyle.Space3, text, WinoLayout.Spacer(), _check);
        if (choice.HasGlyph)
        {
            var mark = new WinoIconView(choice.Glyph, 20) { IsAccessibilityElement = false };
            WinoLayout.Size(mark, 24, 24);
            row.InsertArrangedSubview(mark, 0);
        }
        row.Alignment = UIStackViewAlignment.Center;
        AddSubview(row);
        NSLayoutConstraint.ActivateConstraints(
        [
            HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget + 4),
            row.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 16),
            row.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -14),
            row.TopAnchor.ConstraintEqualTo(TopAnchor, 10),
            row.BottomAnchor.ConstraintEqualTo(BottomAnchor, -10)
        ]);
        AccessibilityLabel = choice.AutomationName;
        IsSelected = false;
    }

    public AccountSetupChoice Choice { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            Stroke = value ? WinoStyle.Accent : WinoSettingsStyle.CardStroke;
            StrokeWidth = value ? 2 : 1;
            _check.Hidden = !value;
            AccessibilityTraits = value ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
        }
    }
}

/// <summary>
/// One app on the review step (Windows AccountSetupAppRowTemplate): a card with the app's icon, title and status, its
/// switch, and the source (or mail range) menu under it; the server address, the note and the error follow the card.
/// </summary>
internal sealed class AccountSetupAppRowView : UIView
{
    private readonly UILabel _status = AccountSetupUi.Caption();
    private readonly WinoPopUpButton _source;
    private readonly UISwitch _switch = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UITextField _server = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UILabel _note = AccountSetupUi.Caption();
    private readonly UILabel _error = AccountSetupUi.Error();
    private readonly UIView _sourceRow;
    private int _shownIndex = -2;
    private bool _applying;

    public AccountSetupAppRowView(AccountSetupAppRow row)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Row = row;

        var icon = new WinoIconView(row.Glyph, 22);
        WinoLayout.Size(icon, 26, 26);
        var title = WinoStyle.Label(row.Title, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        var text = WinoLayout.VStack(2, title, _status);
        text.Alignment = UIStackViewAlignment.Fill;
        text.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);

        _switch.AccessibilityLabel = string.Format(Translator.AccountSetup_Review_UseApp, row.Title);
        _switch.Hidden = !row.IsSwitchVisible;
        _switch.ValueChanged += (_, _) => { if (!_applying) Row.IsOn = _switch.On; };
        WinoLayout.Priorities(_switch, UILayoutConstraintAxis.Horizontal, 1000, 1000);
        var header = WinoLayout.HStack(WinoStyle.Space3, icon, text, _switch);
        header.Alignment = UIStackViewAlignment.Center;

        var sourceName = row.Kind == AccountSetupAppKind.Mail
            ? Translator.AccountSetup_Review_MailRange
            : string.Format(Translator.AccountSetup_Review_SourceOf, row.Title);
        _source = new WinoPopUpButton(sourceName);
        _source.Chosen = index =>
        {
            if (!_applying && index >= 0 && index < Row.Options.Count) Row.SelectedOption = Row.Options[index];
        };
        var sourceLabel = WinoStyle.Label(sourceName, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        _sourceRow = WinoLayout.VStack(4, sourceLabel, _source);
        ((UIStackView)_sourceRow).Alignment = UIStackViewAlignment.Fill;

        var cardStack = WinoLayout.VStack(WinoStyle.Space3, header, _sourceRow);
        cardStack.Alignment = UIStackViewAlignment.Fill;
        var card = new WinoSurfaceView { Fill = WinoSettingsStyle.GroupFill, Stroke = WinoSettingsStyle.GroupStroke, StrokeWidth = 1, CornerRadius = 12 };
        card.TranslatesAutoresizingMaskIntoConstraints = false;
        WinoLayout.Fill(cardStack, card, 12, 14, 12, 14);

        _server.BorderStyle = UITextBorderStyle.RoundedRect;
        _server.Placeholder = Translator.AccountSetup_ServerAddressPlaceholder;
        _server.Font = WinoStyle.Body;
        _server.KeyboardType = UIKeyboardType.Url;
        _server.TextContentType = UITextContentType.Url;
        _server.AutocorrectionType = UITextAutocorrectionType.No;
        _server.AutocapitalizationType = UITextAutocapitalizationType.None;
        _server.ClearButtonMode = UITextFieldViewMode.WhileEditing;
        _server.AccessibilityLabel = Translator.AccountSetup_ServerAddressHeader;
        _server.AccessibilityHint = Translator.AccountSetup_ServerAddressDescription;
        _server.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        _server.EditingChanged += (_, _) => { if (!_applying) Row.ServerUrl = _server.Text ?? string.Empty; };

        var stack = WinoLayout.VStack(6, card, _server, Indented(_note), Indented(_error));
        stack.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Fill(stack, this);
        Apply();
    }

    public AccountSetupAppRow Row { get; }

    public UITextField ServerField => _server;

    private static UIView Indented(UILabel label)
    {
        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(label, host, 0, 4, 0, 4);
        return host;
    }

    public void SetEnabled(bool enabled)
    {
        _source.Enabled = enabled;
        _switch.Enabled = enabled;
        _server.Enabled = enabled;
    }

    public void Apply()
    {
        _applying = true;
        try
        {
            var row = Row;
            _status.Text = row.StatusText;
            _status.Hidden = !row.HasStatus;
            if (_switch.On != row.IsOn) _switch.SetState(row.IsOn, false);
            _sourceRow.Hidden = !row.IsSourceVisible;
            var index = row.SelectedOption is null ? -1 : row.Options.IndexOf(row.SelectedOption);
            if (index != _shownIndex)
            {
                _shownIndex = index;
                if (row.Options.Count == 0) _source.Menu = null;
                else _source.SetItems(row.Options.Select(option => option.Label).ToArray(), index);
            }
            _source.AccessibilityValue = row.AutomationName;
            _server.Hidden = !row.IsServerEntryShown;
            if ((_server.Text ?? string.Empty) != row.ServerUrl) _server.Text = row.ServerUrl;
            _note.Text = row.Note;
            _note.Superview!.Hidden = !row.IsNoteShown;
            _error.Text = row.Error;
            _error.Superview!.Hidden = !row.HasError;
        }
        finally { _applying = false; }
    }
}

/// <summary>One line of the result under "Account added" (Windows AccountSetupResultRowTemplate).</summary>
internal sealed class AccountSetupResultRowView : UIView
{
    public AccountSetupResultRowView(AccountSetupResultRow row)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        var icon = new WinoIconView(row.Glyph, 18);
        WinoLayout.Size(icon, 22, 22);
        var title = WinoStyle.Label(row.Title, WinoStyle.BodyStrong);
        WinoLayout.Priorities(title, UILayoutConstraintAxis.Horizontal, 750, 750);
        var detail = WinoStyle.Label(row.Detail, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        detail.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);
        var stack = WinoLayout.HStack(10, icon, title, detail);
        stack.Alignment = UIStackViewAlignment.FirstBaseline;
        WinoLayout.Fill(stack, this, 4, 0, 4, 0);
        IsAccessibilityElement = true;
        AccessibilityTraits = UIAccessibilityTrait.StaticText;
        AccessibilityLabel = row.AutomationName;
    }
}

/// <summary>
/// An InfoBar with a wrapping row of buttons inside it under its text (Windows InfoBar.Content). The bar and its
/// buttons show and hide together.
/// </summary>
internal sealed class AccountSetupActionBar : UIView
{
    public AccountSetupActionBar(WinoInfoBar bar, params UIView[] actions)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Bar = bar;
        Actions = new AccountSetupFlowView();
        foreach (var action in actions) Actions.AddItem(action);
        bar.Content = Actions;
        WinoLayout.Fill(bar, this);
    }

    public WinoInfoBar Bar { get; }

    public AccountSetupFlowView Actions { get; }
}
