using CommunityToolkit.Mvvm.Messaging;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views.Mail.Compose;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.UI;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// Wino Intelligence rewrite (Mac Rewrite partial, design board ComposeAIRewrite): the "Rewrite" submenu of More with
/// the tones, and the status strip under the subject for the busy, result and error states. The command is hidden while
/// the account is not eligible (Windows parity).
/// </summary>
public sealed partial class ComposePageViewController
{
    private readonly UIView _rewriteHost = new() { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
    private readonly ComposeIntelligenceRing _rewriteRing = new();
    private readonly UIImageView _rewriteGlyph = new() { TranslatesAutoresizingMaskIntoConstraints = false, ContentMode = UIViewContentMode.Center };
    private readonly WinoIconView _rewriteWarning = new(WinoIconGlyph.Warning, 16, WinoStyle.Critical);
    private readonly UILabel _rewriteStatus = WinoStyle.Label(string.Empty, WinoStyle.Subheadline, WinoStyle.PrimaryText, 2);
    private UIButton? _rewriteCancel;
    private UIButton? _rewriteToggle;
    private UIButton? _rewriteRegenerate;
    private UIButton? _rewriteKeep;
    private UIButton? _rewriteRetry;
    private UIButton? _rewriteDismiss;
    private string? _rewriteError;
    private string? _lastRewriteMode;
    private string _announcedRewriteStatus = string.Empty;

    /// <summary>The tone menu (Mac ShowRewriteMenu) with the brand glyph.</summary>
    private UIMenu RewriteMenu()
    {
        var session = ViewModel.RewriteSession;
        var modes = session.Modes.Select(option => (UIMenuElement)EditorFormatToolbar.Action(option.Label, () => StartRewrite(option.Mode), enabled: !session.IsBusy)).ToArray();
        return EditorFormatToolbar.Submenu(Translator.Composer_AiRewrite, modes, ComposeBrand.GlyphImage(WinoIconGlyph.WinoIntelligence, 18, Translator.Composer_AiRewrite));
    }

    private void StartRewrite(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode) || !ViewModel.RewriteSession.IsAvailable || ViewModel.RewriteSession.IsBusy) return;
        _lastRewriteMode = mode;
        _rewriteError = null;
        UpdateRewriteStrip();
        Observe(ViewModel.RewriteSession.RewriteCommand.ExecuteAsync(mode));
    }

    private UIView BuildRewriteStrip()
    {
        _rewriteRing.AccessibilityLabel = Translator.WinoIntelligence_Rewriting;
        _rewriteGlyph.Image = ComposeBrand.GlyphImage(WinoIconGlyph.WinoIntelligence, 18);
        WinoLayout.Size(_rewriteGlyph, 22, 22);
        _rewriteStatus.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        _rewriteStatus.SetContentHuggingPriority(1, UILayoutConstraintAxis.Horizontal);

        _rewriteCancel = StripButton(Translator.Buttons_Cancel, () => ViewModel.RewriteSession.CancelCommand.Execute(null));
        _rewriteToggle = StripButton(Translator.WinoIntelligence_ShowOriginal, () => Observe(ViewModel.RewriteSession.ToggleOriginalCommand.ExecuteAsync(null)));
        _rewriteRegenerate = GlyphStripButton(WinoIconGlyph.ArrowClockwise, Translator.WinoIntelligence_Regenerate, () =>
        {
            _rewriteError = null;
            Observe(ViewModel.RewriteSession.RegenerateCommand.ExecuteAsync(null));
        });
        _rewriteKeep = StripButton(Translator.Composer_AiRewriteKeep, () => ViewModel.RewriteSession.KeepCommand.Execute(null), true);
        _rewriteRetry = StripButton(Translator.Buttons_Retry, () => StartRewrite(_lastRewriteMode));
        _rewriteDismiss = GlyphStripButton(WinoIconGlyph.Dismiss, Translator.Buttons_Close, () => { _rewriteError = null; UpdateRewriteStrip(); });

        var status = WinoLayout.HStack(8, _rewriteRing, _rewriteGlyph, _rewriteWarning, _rewriteStatus);
        var buttons = WinoLayout.HStack(6, WinoLayout.Spacer(), _rewriteCancel, _rewriteToggle, _rewriteRegenerate, _rewriteKeep, _rewriteRetry, _rewriteDismiss);
        // Narrow widths put the buttons under the status; wide ones keep a single row (the Mac 34pt strip).
        var layout = new UIStackView([status, buttons])
        {
            Axis = TraitCollection.HorizontalSizeClass == UIUserInterfaceSizeClass.Compact ? UILayoutConstraintAxis.Vertical : UILayoutConstraintAxis.Horizontal,
            Spacing = 6,
            Alignment = UIStackViewAlignment.Fill,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _rewriteHost.RegisterForTraitChanges([typeof(UITraitHorizontalSizeClass)], (environment, _) =>
        {
            if (environment is UIView view) layout.Axis = view.TraitCollection.HorizontalSizeClass == UIUserInterfaceSizeClass.Compact ? UILayoutConstraintAxis.Vertical : UILayoutConstraintAxis.Horizontal;
        });
        WinoLayout.Insets(layout, 6, 10, 6, 6);
        var surface = new WinoSurfaceView
        {
            Fill = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.04), WinoStyle.Hex(0xFFFFFF, 0.06)),
            Stroke = WinoStyle.ZoneStroke,
            CornerRadius = WinoStyle.ControlRadius
        };
        WinoLayout.Fill(layout, surface);
        surface.HeightAnchor.ConstraintGreaterThanOrEqualTo(40).Active = true;
        surface.AccessibilityLabel = Translator.Composer_AiRewrite;
        surface.ShouldGroupAccessibilityChildren = true;
        WinoLayout.Fill(surface, _rewriteHost, 6, 12, 6, 12);
        return _rewriteHost;
    }

    private static UIButton StripButton(string title, Action action, bool primary = false)
    {
        var configuration = primary ? UIButtonConfiguration.FilledButtonConfiguration : UIButtonConfiguration.GrayButtonConfiguration;
        configuration.Title = title;
        configuration.ButtonSize = UIButtonConfigurationSize.Small;
        if (primary) configuration.BaseBackgroundColor = WinoStyle.Accent;
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false, AccessibilityLabel = title };
        button.SetContentCompressionResistancePriority(1000, UILayoutConstraintAxis.Horizontal);
        button.PrimaryActionTriggered += (_, _) => action();
        return button;
    }

    private static UIButton GlyphStripButton(WinoIconGlyph glyph, string label, Action action)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.Image(glyph, 14, null, label);
        configuration.BaseForegroundColor = WinoStyle.SecondaryText;
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false, AccessibilityLabel = label };
        WinoLayout.Size(button, 36, 32);
        button.PrimaryActionTriggered += (_, _) => action();
        return button;
    }

    private void BindRewrite()
    {
        var session = ViewModel.RewriteSession;
        foreach (var property in new[] { nameof(ComposerRewriteSession.IsAvailable), nameof(ComposerRewriteSession.IsBusy), nameof(ComposerRewriteSession.HasResult),
                     nameof(ComposerRewriteSession.IsShowingRewrite), nameof(ComposerRewriteSession.StatusText) })
        {
            Bindings.Own(new PropertyBinding<ComposerRewriteSession, bool>(session, property, _ => true, _ => UpdateRewriteStrip(), Dispatcher, ReportError));
        }

        // Signing in or buying a pack changes eligibility while the composer is open.
        WeakReferenceMessenger.Default.Register<ComposePageViewController, WinoIntelligenceAccessChanged>(this,
            static (owner, message) => _ = owner.ViewModel.RefreshRewriteAvailabilityAsync());
        Bindings.Own(new ActionDisposable(() => WeakReferenceMessenger.Default.Unregister<WinoIntelligenceAccessChanged>(this)));
    }

    /// <summary>Rewrite failures appear inline in the strip (never a modal), with Retry for the last tone.</summary>
    private void ShowRewriteError(string error)
        => _ = Dispatcher.ExecuteOnUIThread(() =>
        {
            _rewriteError = string.IsNullOrWhiteSpace(error) ? Translator.WinoIntelligence_ActionFailed : error;
            UpdateRewriteStrip();
        });

    private void UpdateRewriteStrip()
    {
        if (Bindings.IsDisposed) return;
        var session = ViewModel.RewriteSession;
        var busy = session.IsAvailable && session.IsBusy;
        var result = session.IsAvailable && session.IsResultVisible;
        if (busy) _rewriteError = null;
        var error = !busy && _rewriteError is not null;
        ApplyRewriteState(busy, result, error, error ? _rewriteError ?? string.Empty : session.StatusText ?? string.Empty, session.ToggleText);
    }

    private void ApplyRewriteState(bool busy, bool result, bool error, string status, string? toggleText)
    {
        _rewriteHost.Hidden = !(busy || result || error);
        _rewriteRing.Hidden = !busy;
        _rewriteRing.IsAnimating = busy;
        _rewriteGlyph.Hidden = !result || error;
        _rewriteWarning.Hidden = !error;
        _rewriteStatus.Text = status;
        _rewriteStatus.TextColor = error ? WinoStyle.Critical : WinoStyle.PrimaryText;
        if (_rewriteCancel is not null) _rewriteCancel.Hidden = !busy;
        if (_rewriteToggle is not null)
        {
            _rewriteToggle.Hidden = !result || error;
            if (_rewriteToggle.Configuration is { } configuration && toggleText is not null)
            {
                configuration.Title = toggleText;
                _rewriteToggle.Configuration = configuration;
                _rewriteToggle.AccessibilityLabel = toggleText;
            }
        }
        if (_rewriteRegenerate is not null) _rewriteRegenerate.Hidden = !result || error;
        if (_rewriteKeep is not null) _rewriteKeep.Hidden = !result || error;
        if (_rewriteRetry is not null) _rewriteRetry.Hidden = !error || _lastRewriteMode is null;
        if (_rewriteDismiss is not null) _rewriteDismiss.Hidden = !error;

        // The strip is a polite live region: announce each new status once.
        var announced = _rewriteHost.Hidden ? string.Empty : status;
        if (announced.Length > 0 && announced != _announcedRewriteStatus) Announce(announced);
        _announcedRewriteStatus = announced;
    }

    private static void Announce(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        UIAccessibility.PostNotification(UIAccessibilityPostNotification.Announcement, new NSString(text));
    }
}
