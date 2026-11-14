using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// A Windows ToggleSwitch with custom On/Off content (Allowed / Not allowed): the compact native switch of
/// <see cref="WinoLabeledSwitch"/>. The state text shows before the switch only when it says more than on or off.
/// </summary>
public sealed class WinoTextSwitch : UIView
{
    private readonly WinoLabeledSwitch _switch;

    public WinoTextSwitch(string on, string off, string accessibilityLabel, bool showsLabel = false)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _switch = new WinoLabeledSwitch(on, off) { ShowsLabel = showsLabel };
        WinoAccessibility.Label(_switch.Switch, accessibilityLabel);
        WinoLayout.Fill(_switch, this);
        WinoLayout.Priorities(this, UILayoutConstraintAxis.Horizontal, 750, 750);
        _switch.Toggled += (_, _) => Toggled?.Invoke(this, EventArgs.Empty);
    }

    public UISwitch Switch => _switch.Switch;

    /// <summary>Raised after the user flips the switch (not when <see cref="IsOn"/> is set in code).</summary>
    public event EventHandler? Toggled;

    public bool IsOn
    {
        get => _switch.IsOn;
        set => _switch.IsOn = value;
    }

    public bool IsEnabled
    {
        get => _switch.IsEnabled;
        set => _switch.IsEnabled = value;
    }

    /// <summary>Touches near the compact switch still reach it (the inner switch widens its own target).</summary>
    public override bool PointInside(CGPoint point, UIEvent? uievent) => _switch.PointInside(ConvertPointToView(point, _switch), uievent);
}

/// <summary>A thin rounded progress bar (Windows ProgressBar, 4pt) with an error tint.</summary>
public sealed class WinoBarView : UIView
{
    private double _value;
    private bool _error;
    private UIColor? _tint;

    public WinoBarView(double width = -1, double height = 4)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        BackgroundColor = UIColor.Clear;
        Opaque = false;
        ContentMode = UIViewContentMode.Redraw;
        WinoLayout.Size(this, width, height);
        IsAccessibilityElement = true;
        AccessibilityTraits = UIAccessibilityTrait.UpdatesFrequently;
        WinoTraits.OnAppearanceChanged(this, static view => view.SetNeedsDisplay());
        UpdateAccessibilityValue();
    }

    /// <summary>0 to 100.</summary>
    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 100); UpdateAccessibilityValue(); SetNeedsDisplay(); }
    }

    public bool ShowError { get => _error; set { _error = value; SetNeedsDisplay(); } }
    public UIColor? Tint { get => _tint; set { _tint = value; SetNeedsDisplay(); } }

    private void UpdateAccessibilityValue()
        => AccessibilityValue = (_value / 100).ToString("P0", System.Globalization.CultureInfo.CurrentCulture);

    public override void Draw(CGRect rect)
    {
        var radius = Bounds.Height / 2;
        WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.10), WinoStyle.Hex(0xFFFFFF, 0.14)).SetFill();
        UIBezierPath.FromRoundedRect(Bounds, radius).Fill();
        if (_value <= 0) return;
        (_error ? WinoStyle.Critical : _tint ?? WinoStyle.Accent).SetFill();
        var fill = new CGRect(0, 0, Bounds.Width * _value / 100, Bounds.Height);
        UIBezierPath.FromRoundedRect(fill, radius).Fill();
    }
}

/// <summary>
/// What App Review requires next to an auto-renewable subscription (guideline 3.1.2): its length and renewal, and
/// working Terms of Use (Apple's standard EULA) and Privacy Policy links (the Mac AppStoreSubscriptionTerms).
/// </summary>
internal static class AppStoreSubscriptionTerms
{
    public static UIView Create()
    {
        var terms = WinoStyle.Label(Translator.WinoAccount_Management_AiPackAppStoreTerms, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        var links = WinoLayout.HStack(16,
            Link(Translator.WinoAccount_Management_TermsOfUse, AppUrls.AppleStandardEula),
            Link(Translator.SettingsPrivacyPolicy_Title, AppUrls.PrivacyPolicy),
            WinoLayout.Spacer());
        var stack = WinoLayout.VStack(0, terms, links);
        stack.Alignment = UIStackViewAlignment.Fill;
        return stack;
    }

    private static UIButton Link(string title, string url)
    {
        var button = IntelligenceViews.LinkButton(title);
        button.PrimaryActionTriggered += (_, _) => IntelligenceViews.OpenUrl(url);
        return button;
    }
}

/// <summary>Small factory helpers shared by the Intelligence and Wino Account pages.</summary>
internal static class IntelligenceViews
{
    public static UIActivityIndicatorView Spinner(double size = 16)
    {
        var spinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = true, TranslatesAutoresizingMaskIntoConstraints = false };
        if (size < 20) spinner.Transform = CGAffineTransform.MakeScale((nfloat)(size / 20), (nfloat)(size / 20));
        WinoLayout.Priorities(spinner, UILayoutConstraintAxis.Horizontal, 750, 750);
        return spinner;
    }

    public static void SetSpinning(UIActivityIndicatorView spinner, bool spinning)
    {
        if (spinning) spinner.StartAnimating(); else spinner.StopAnimating();
        spinner.Hidden = !spinning;
    }

    /// <summary>The page-level first-load state: a centred spinner shown until cached content is ready.</summary>
    public static UIView LoadingState(out UIActivityIndicatorView spinner)
    {
        spinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Large) { HidesWhenStopped = true, TranslatesAutoresizingMaskIntoConstraints = false };
        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        host.AddSubview(spinner);
        NSLayoutConstraint.ActivateConstraints(
        [
            spinner.CenterXAnchor.ConstraintEqualTo(host.CenterXAnchor),
            spinner.TopAnchor.ConstraintEqualTo(host.TopAnchor, 48),
            spinner.BottomAnchor.ConstraintEqualTo(host.BottomAnchor, -48)
        ]);
        spinner.StartAnimating();
        return host;
    }

    /// <summary>
    /// Visibility for a transient progress row: it appears only when the work outlasts
    /// <paramref name="delay"/> seconds (quick refreshes never shift the layout) and hides as soon as
    /// the work ends. <paramref name="alive"/> stops a pending show after the page is released.
    /// </summary>
    public static Action<bool> DelayedProgressRow(UIView row, UIActivityIndicatorView spinner, Func<bool> alive, double delay = 0.6)
    {
        var generation = 0;
        row.Hidden = true;
        SetSpinning(spinner, false);
        return on =>
        {
            if (on && !row.Hidden) return;
            var current = ++generation;
            if (!on)
            {
                row.Hidden = true;
                SetSpinning(spinner, false);
                return;
            }
            var timer = NSTimer.CreateTimer(delay, false, _ =>
            {
                if (current != generation || !alive()) return;
                row.Hidden = false;
                SetSpinning(spinner, true);
            });
            // Common modes: the row still appears while the user scrolls.
            NSRunLoop.Main.AddTimer(timer, NSRunLoopMode.Common);
        };
    }

    /// <summary>A card surface like the Windows brand promo (settings card fill, stroke, radius 6, padding 20).</summary>
    public static WinoSurfaceView Surface(UIView content, double padding = 20, double radius = 6)
    {
        var surface = new WinoSurfaceView
        {
            Fill = WinoSettingsStyle.GroupFill,
            Stroke = WinoSettingsStyle.CardStroke,
            CornerRadius = radius,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(content, surface, padding);
        return surface;
    }

    /// <summary>An accent text button (Windows HyperlinkButton) with a 44pt touch height.</summary>
    public static UIButton LinkButton(string title)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Title = title;
        configuration.BaseForegroundColor = WinoStyle.Accent;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(0, 0, 0, 0);
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        button.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        WinoLayout.Priorities(button, UILayoutConstraintAxis.Horizontal, 750, 750);
        WinoAccessibility.Label(button, title);
        button.AccessibilityTraits = UIAccessibilityTrait.Link;
        return button;
    }

    /// <summary>Opens a web page in the system browser.</summary>
    public static void OpenUrl(string url)
    {
        if (NSUrl.FromString(url) is { } address)
            _ = UIApplication.SharedApplication.OpenUrlAsync(address, new UIApplicationOpenUrlOptions());
    }

    /// <summary>A round check button (the AppKit checkbox) whose state the caller sets.</summary>
    public static UIButton CheckButton(string title)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Title = title;
        configuration.ImagePadding = 8;
        configuration.BaseForegroundColor = WinoStyle.PrimaryText;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(6, 0, 6, 0);
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        button.HorizontalAlignment = UIControlContentHorizontalAlignment.Leading;
        button.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        SetChecked(button, false);
        return button;
    }

    public static void SetChecked(UIButton button, bool value)
    {
        button.Selected = value;
        if (button.Configuration is not { } configuration) return;
        configuration.Image = WinoIcons.MonoImage(value ? WinoIconGlyph.CheckmarkCircleFilled : WinoIconGlyph.Circle, 20, value ? WinoStyle.Accent : WinoStyle.SecondaryText);
        button.Configuration = configuration;
        button.AccessibilityTraits = value ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
    }

    /// <summary>Removes and releases every arranged view of a stack.</summary>
    public static void Clear(UIStackView stack)
    {
        foreach (var view in stack.ArrangedSubviews) { stack.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
    }
}
