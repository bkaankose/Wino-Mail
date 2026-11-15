using AppKit;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Onboarding;

/// <summary>The email field's cell: a recognised provider's mark sits in the leading inset (Windows GetEmailPadding).</summary>
[Register(nameof(AccountSetupAddressCell))]
internal sealed class AccountSetupAddressCell : NSTextFieldCell
{
    public AccountSetupAddressCell() { }
    public AccountSetupAddressCell(NativeHandle handle) : base(handle) { }

    public nfloat LeadingInset { get; set; }

    public override CGRect DrawingRectForBounds(CGRect theRect)
    {
        var rect = base.DrawingRectForBounds(theRect);
        return new CGRect(rect.X + LeadingInset, rect.Y, rect.Width - LeadingInset, rect.Height);
    }
}

/// <summary>Small factories shared by the setup page's steps.</summary>
internal static class AccountSetupUi
{
    public static NSTextField Caption(string? text = null)
        => WinoStyle.Label(text, WinoStyle.Description, WinoStyle.SecondaryText, 0);

    public static NSTextField Error()
        => WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.Critical, 0);

    public static NSTextField Header(string text) => WinoStyle.Label(text, WinoStyle.Body);

    /// <summary>A borderless accent link (Windows HyperlinkButton).</summary>
    public static NSButton Link(string title, double fontSize = 13)
    {
        var button = new NSButton { BezelStyle = NSBezelStyle.Inline, Bordered = false, TranslatesAutoresizingMaskIntoConstraints = false };
        SetLinkTitle(button, title, fontSize);
        return button;
    }

    public static void SetLinkTitle(NSButton button, string title, double fontSize = 13)
    {
        button.Title = title;
        button.AttributedTitle = new NSAttributedString(title, new NSStringAttributes
        {
            ForegroundColor = WinoStyle.Accent,
            Font = NSFont.SystemFontOfSize((nfloat)fontSize)
        });
        WinoAccessibility.Label(button, title);
    }

    public static NSProgressIndicator Spinner() => new()
    {
        Style = NSProgressIndicatorStyle.Spinning,
        ControlSize = NSControlSize.Small,
        Indeterminate = true,
        IsDisplayedWhenStopped = false,
        TranslatesAutoresizingMaskIntoConstraints = false
    };

    public static void SetSpinning(NSProgressIndicator spinner, bool spinning)
    {
        spinner.Hidden = !spinning;
        if (spinning) spinner.StartAnimation(null);
        else spinner.StopAnimation(null);
    }

    /// <summary>A vertical stack whose arranged views all take its width.</summary>
    public static NSStackView Column(double spacing, params NSView[] views)
    {
        var stack = WinoLayout.VStack(spacing, views);
        foreach (var view in views) view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        return stack;
    }

    /// <summary>A leading row of buttons that keeps its natural width inside a full-width column.</summary>
    public static NSView Buttons(params NSView[] views)
    {
        var row = WinoLayout.HStack(WinoStyle.Space2, views);
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
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
}

/// <summary>
/// One mark of the address step's "Works with" shelf (Windows AccountSetupProviderShelfItem template): a 36pt circle
/// with the brand glyph, or a capsule with a label for IMAP and POP3. The matched provider gets an accent ring and the
/// others dim. It is a picture, not a button.
/// </summary>
internal sealed class AccountSetupShelfItemView : WinoSurfaceView
{
    private const double Height = 36;
    private readonly WinoIconView _glyph = new(WinoIconGlyph.None, 18);
    private readonly NSTextField _label = WinoStyle.Label(string.Empty, WinoStyle.CaptionStrong);
    private readonly NSLayoutConstraint _width;
    private AccountSetupProviderShelfItem? _item;

    public AccountSetupShelfItemView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Fill = WinoSettingsStyle.SubtleFill;
        CornerRadius = Height / 2;
        AddSubview(_glyph);
        AddSubview(_label);
        WinoLayout.Size(_glyph, 18, 18);
        _width = WidthAnchor.ConstraintEqualTo((nfloat)Height);
        NSLayoutConstraint.ActivateConstraints(
        [
            HeightAnchor.ConstraintEqualTo((nfloat)Height),
            _width,
            _glyph.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            _glyph.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            _label.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            _label.CenterYAnchor.ConstraintEqualTo(CenterYAnchor)
        ]);
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.ImageRole;
    }

    public void Apply(AccountSetupProviderShelfItem item)
    {
        if (_item == item) return;
        _item = item;
        _glyph.Hidden = !item.HasGlyph;
        _glyph.Icon = item.HasGlyph ? item.Glyph : WinoIconGlyph.None;
        _label.Hidden = !item.HasLabel;
        _label.StringValue = item.Label;
        _width.Constant = item.HasLabel ? (nfloat)Math.Ceiling(_label.AttributedStringValue.Size.Width + 24) : (nfloat)Height;
        Stroke = item.IsMatched ? WinoStyle.Accent : null;
        StrokeWidth = item.IsMatched ? 2 : 0;
        AlphaValue = item.IsDimmed ? 0.4f : 1f;
        ToolTip = item.Title;
        AccessibilityLabel = item.Title;
    }
}

/// <summary>
/// One row of the sign-in method list (Windows AccountSetupChoiceTemplate in a single-selection ItemsView): a card with
/// the option's label and its "(recommended)" caption, outlined in the accent colour when selected.
/// </summary>
internal sealed class AccountSetupChoiceCard : WinoSurfaceView
{
    private bool _isSelected;

    public AccountSetupChoiceCard(AccountSetupChoice choice)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Choice = choice;
        Fill = WinoSettingsStyle.CardFill;
        CornerRadius = 6;
        var label = WinoStyle.Label(choice.Label, WinoStyle.Body);
        var caption = WinoStyle.Label(choice.Caption, WinoStyle.Description, WinoStyle.SecondaryText);
        caption.Hidden = !choice.HasCaption;
        var row = WinoLayout.HStack(6, label, caption);
        row.Alignment = NSLayoutAttribute.FirstBaseline;
        AddSubview(row);
        NSLayoutConstraint.ActivateConstraints(
        [
            HeightAnchor.ConstraintGreaterThanOrEqualTo(40),
            row.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 12),
            row.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor, -12),
            row.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            row.TopAnchor.ConstraintGreaterThanOrEqualTo(TopAnchor, 8)
        ]);
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.RadioButtonRole;
        AccessibilityLabel = choice.AutomationName;
        IsSelected = false;
    }

    public AccountSetupChoice Choice { get; }
    public event EventHandler? Pressed;
    public event EventHandler<int>? Moved;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            Stroke = value ? WinoStyle.Accent : WinoSettingsStyle.CardStroke;
            StrokeWidth = value ? 2 : 1;
            AccessibilityValue = new NSNumber(value ? 1 : 0);
        }
    }

    public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;
    public override bool AcceptsFirstResponder() => true;
    public override bool CanBecomeKeyView => true;

    public override void MouseUp(NSEvent theEvent)
    {
        if (!Bounds.Contains(ConvertPointFromView(theEvent.LocationInWindow, null))) return;
        Window?.MakeFirstResponder(this);
        Pressed?.Invoke(this, EventArgs.Empty);
    }

    public override void KeyDown(NSEvent theEvent)
    {
        switch (theEvent.KeyCode)
        {
            case 125: Moved?.Invoke(this, 1); return;
            case 126: Moved?.Invoke(this, -1); return;
        }
        if (theEvent.Characters == " ") { Pressed?.Invoke(this, EventArgs.Empty); return; }
        base.KeyDown(theEvent);
    }

    public override bool AccessibilityPerformPress()
    {
        Pressed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public override void DrawFocusRingMask() => NSBezierPath.FromRoundedRect(Bounds, 6, 6).Fill();
    public override CGRect FocusRingMaskBounds => Bounds;
}

/// <summary>
/// One app on the review step (Windows AccountSetupAppRowTemplate): a settings card with the app's icon, title and
/// status, the source drop-down and the switch, then the server address, the note and the error under it.
/// </summary>
internal sealed class AccountSetupAppRowView : NSView
{
    private readonly WinoSettingsCard _card;
    private readonly NSPopUpButton _source = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly NSSwitch _switch = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly NSTextField _server = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly NSTextField _note = AccountSetupUi.Caption();
    private readonly NSTextField _error = AccountSetupUi.Error();
    private bool _applying;

    public AccountSetupAppRowView(AccountSetupAppRow row)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Row = row;

        _source.AddItems(row.Options.Select(option => option.Label).ToArray());
        WinoAccessibility.Label(_source, row.Kind == AccountSetupAppKind.Mail
            ? Translator.AccountSetup_Review_MailRange
            : string.Format(Translator.AccountSetup_Review_SourceOf, row.Title));
        _source.WidthAnchor.ConstraintEqualTo(230).Active = true;
        _source.Activated += (_, _) =>
        {
            var index = (int)_source.IndexOfSelectedItem;
            if (!_applying && index >= 0 && index < Row.Options.Count) Row.SelectedOption = Row.Options[index];
        };

        WinoAccessibility.Label(_switch, string.Format(Translator.AccountSetup_Review_UseApp, row.Title));
        _switch.Activated += (_, _) => { if (!_applying) Row.IsOn = _switch.State != 0; };
        _switch.Hidden = !row.IsSwitchVisible;
        // Mail has no switch; the empty slot keeps its drop-down in line with the others. The slot takes the switch's
        // own width (it grew in macOS 26), so the drop-down never runs under it.
        var switchSlot = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        switchSlot.AddSubview(_switch);
        NSLayoutConstraint.ActivateConstraints(
        [
            switchSlot.WidthAnchor.ConstraintEqualTo(_switch.WidthAnchor),
            switchSlot.HeightAnchor.ConstraintEqualTo(_switch.HeightAnchor),
            _switch.LeadingAnchor.ConstraintEqualTo(switchSlot.LeadingAnchor),
            _switch.CenterYAnchor.ConstraintEqualTo(switchSlot.CenterYAnchor)
        ]);
        var trailing = WinoLayout.HStack(WinoStyle.Space3, _source, switchSlot);

        _card = new WinoSettingsCard(row.Title, null, row.Glyph, trailing);

        _server.PlaceholderString = Translator.AccountSetup_ServerAddressPlaceholder;
        _server.UsesSingleLineMode = true;
        _server.Cell.Scrollable = true;
        _server.ContentType = NSTextContentType.Url;
        WinoAccessibility.Label(_server, Translator.AccountSetup_ServerAddressHeader);
        WinoAccessibility.Help(_server, Translator.AccountSetup_ServerAddressDescription);
        _server.Changed += (_, _) => { if (!_applying) Row.ServerUrl = _server.StringValue; };

        var stack = WinoLayout.VStack(4, _card, _server, Indented(_note), Indented(_error));
        foreach (var view in stack.ArrangedSubviews) view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        WinoLayout.Fill(stack, this);
        Apply();
    }

    public AccountSetupAppRow Row { get; }

    public NSTextField ServerField => _server;

    private static NSView Indented(NSTextField label)
    {
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(label, host, 0, 4, 0, 4);
        return host;
    }

    public void SetEnabled(bool enabled)
    {
        _source.Enabled = enabled;
        _switch.Enabled = enabled;
        _server.Enabled = enabled;
    }

    public void SetWrapWidth(double width)
    {
        _note.PreferredMaxLayoutWidth = (nfloat)(width - 8);
        _error.PreferredMaxLayoutWidth = (nfloat)(width - 8);
    }

    public void Apply()
    {
        _applying = true;
        try
        {
            var row = Row;
            _card.Description = row.HasStatus ? row.StatusText : null;
            _switch.State = row.IsOn ? 1 : 0;
            _source.Hidden = !row.IsSourceVisible;
            var index = row.SelectedOption is null ? -1 : row.Options.IndexOf(row.SelectedOption);
            if (index >= 0 && _source.IndexOfSelectedItem != index) _source.SelectItem(index);
            WinoAccessibility.Help(_source, row.AutomationName);
            _server.Hidden = !row.IsServerEntryShown;
            if (_server.StringValue != row.ServerUrl) _server.StringValue = row.ServerUrl;
            _note.StringValue = row.Note;
            _note.Superview!.Hidden = !row.IsNoteShown;
            _error.StringValue = row.Error;
            _error.Superview!.Hidden = !row.HasError;
        }
        finally { _applying = false; }
    }
}

/// <summary>One line of the result under "Account added" (Windows AccountSetupResultRowTemplate).</summary>
internal sealed class AccountSetupResultRowView : NSView
{
    public AccountSetupResultRowView(AccountSetupResultRow row)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        var icon = new WinoIconView(row.Glyph, 16);
        WinoLayout.Size(icon, 20, 16);
        var title = WinoStyle.Label(row.Title, WinoStyle.BodyStrong);
        title.SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);
        var detail = WinoStyle.Label(row.Detail, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        detail.PreferredMaxLayoutWidth = 300;
        var stack = WinoLayout.HStack(10, icon, title, detail);
        stack.Alignment = NSLayoutAttribute.FirstBaseline;
        WinoLayout.Fill(stack, this, 4, 0, 4, 0);
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.StaticTextRole;
        AccessibilityLabel = row.AutomationName;
    }
}

/// <summary>One entry of the provider picker (Windows AccountSetupProviderChoice template); the current one is highlighted.</summary>
internal sealed class AccountSetupProviderChoiceView : WinoSurfaceView
{
    private readonly bool _isCurrent;
    private bool _hovered;

    public AccountSetupProviderChoiceView(AccountSetupProviderChoice choice, bool isCurrent)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Choice = choice;
        _isCurrent = isCurrent;
        CornerRadius = 6;
        var icon = new WinoIconView(choice.Glyph, 16);
        WinoLayout.Size(icon, 20, 16);
        var title = WinoStyle.Label(choice.Title, WinoStyle.Body);
        title.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var description = WinoStyle.Label(choice.Description, WinoStyle.Caption, WinoStyle.TertiaryText);
        description.Hidden = !choice.HasDescription;
        description.SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);
        var row = WinoLayout.HStack(10, icon, title, WinoLayout.Spacer(), description);
        WinoLayout.Fill(row, this, 0, 8, 0, 8);
        HeightAnchor.ConstraintGreaterThanOrEqualTo(36).Active = true;
        AddTrackingArea(new NSTrackingArea(CGRect.Empty, NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveAlways | NSTrackingAreaOptions.InVisibleRect, this, null));
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.ButtonRole;
        AccessibilityLabel = choice.AutomationName;
        UpdateFill();
    }

    public AccountSetupProviderChoice Choice { get; }
    public event EventHandler? Pressed;

    public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

    public override void MouseEntered(NSEvent theEvent) { _hovered = true; UpdateFill(); }
    public override void MouseExited(NSEvent theEvent) { _hovered = false; UpdateFill(); }

    public override void MouseUp(NSEvent theEvent)
    {
        if (Bounds.Contains(ConvertPointFromView(theEvent.LocationInWindow, null))) Pressed?.Invoke(this, EventArgs.Empty);
    }

    public override bool AccessibilityPerformPress()
    {
        Pressed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void UpdateFill() => Fill = _isCurrent ? WinoSettingsStyle.SelectedFill : _hovered ? WinoStyle.RowHoverFill : null;
}

/// <summary>
/// An InfoBar with a row of buttons inside it under its text (Windows InfoBar.Content). The bar and its buttons show
/// and hide together.
/// </summary>
internal sealed class AccountSetupActionBar : NSView
{
    public AccountSetupActionBar(WinoInfoBar bar, params NSView[] actions)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Bar = bar;
        bar.Content = WinoLayout.HStack(WinoStyle.Space2, actions);
        WinoLayout.Fill(bar, this);
    }

    public WinoInfoBar Bar { get; }
}
