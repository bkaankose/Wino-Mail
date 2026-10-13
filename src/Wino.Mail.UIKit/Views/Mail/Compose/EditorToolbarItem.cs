using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail.Compose;

/// <summary>
/// One command of the keyboard formatting bar (the Mac EditorFormatButton): a 36pt Wino glyph button that runs an
/// action, or opens a menu built when it opens. Toggles show their on state with the selected fill and the accent.
/// </summary>
internal sealed class EditorToolbarButton : UIButton
{
    private bool _isActive;
    private WinoIconGlyph _glyph;

    public EditorToolbarButton(WinoIconGlyph glyph, string label, Action? action = null, Func<UIMenuElement[]>? menu = null)
    {
        _glyph = glyph;
        Label = label;
        TranslatesAutoresizingMaskIntoConstraints = false;
        Configuration = UIButtonConfiguration.PlainButtonConfiguration;
        WinoLayout.Size(this, 38, 36);
        AccessibilityLabel = label;
        if (action is not null) PrimaryActionTriggered += (_, _) => action();
        if (menu is not null)
        {
            Menu = UIMenu.Create(label, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline,
                [UIDeferredMenuElement.CreateUncached(completion => completion(menu()))]);
            ShowsMenuAsPrimaryAction = true;
        }
        UpdateAppearance();
    }

    public string Label { get; private set; }

    /// <summary>On state of a toggle (bold, a list, the current alignment).</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            if (value) AccessibilityTraits |= UIAccessibilityTrait.Selected;
            else AccessibilityTraits &= ~UIAccessibilityTrait.Selected;
            UpdateAppearance();
        }
    }

    public void SetGlyph(WinoIconGlyph glyph, string label)
    {
        _glyph = glyph;
        Label = label;
        AccessibilityLabel = label;
        UpdateAppearance();
    }

    /// <summary>Re-reads the accent after a theme change.</summary>
    public void Refresh() => UpdateAppearance();

    private void UpdateAppearance()
    {
        // The plain configuration dims a disabled button by itself.
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.Image(_glyph, 18, null, Label);
        configuration.BaseForegroundColor = _isActive ? WinoStyle.Accent : WinoStyle.PrimaryText;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(6, 8, 6, 8);
        var background = UIBackgroundConfiguration.ClearConfiguration;
        background.BackgroundColor = _isActive ? WinoStyle.SelectedFill : UIColor.Clear;
        background.CornerRadius = (nfloat)WinoStyle.ControlRadius;
        configuration.Background = background;
        Configuration = configuration;
    }
}

/// <summary>A command of the bar: its button and whether it applies now (hidden otherwise).</summary>
internal sealed class EditorToolbarItem(UIView view, Func<bool>? isAvailable = null)
{
    public UIView View { get; } = view;

    public bool IsDivider { get; private init; }

    public bool IsAvailable => isAvailable?.Invoke() ?? true;

    /// <summary>A thin vertical rule between groups.</summary>
    public static EditorToolbarItem Divider()
    {
        var rule = new UIView { TranslatesAutoresizingMaskIntoConstraints = false, BackgroundColor = WinoStyle.Separator };
        WinoLayout.Size(rule, 1, 20);
        return new EditorToolbarItem(rule) { IsDivider = true };
    }
}
