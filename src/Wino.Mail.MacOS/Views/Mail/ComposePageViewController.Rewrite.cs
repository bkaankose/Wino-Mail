using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views.Mail.Compose;
using Wino.Mail.MacOS.Views.Settings;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.UI;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// Wino Intelligence rewrite (design board ComposeAIRewrite): the "Rewrite" command-row button with the
/// tone menu, and the 34pt status strip under the subject for the busy, result and error states.
/// The button is hidden while the account is not eligible (Windows parity).
/// </summary>
public sealed partial class ComposePageViewController
{
    private NSButton _rewriteButton = null!;
    private NSView _rewriteHost = null!;
    private ComposeIntelligenceRing _rewriteRing = null!;
    private BrandIconView _rewriteGlyph = null!;
    private WinoIconView _rewriteWarning = null!;
    private NSTextField _rewriteStatus = null!;
    private NSButton _rewriteCancel = null!;
    private NSButton _rewriteToggle = null!;
    private NSButton _rewriteRegenerate = null!;
    private NSButton _rewriteKeep = null!;
    private NSButton _rewriteRetry = null!;
    private NSButton _rewriteDismiss = null!;
    private string? _rewriteError;
    private string? _lastRewriteMode;
    private string _announcedRewriteStatus = string.Empty;

    private NSButton BuildRewriteButton()
    {
        _rewriteButton = new NSButton
        {
            Title = Translator.Composer_AiRewriteShortTitle,
            Bordered = false,
            BezelStyle = NSBezelStyle.Inline,
            Image = BrandGlyphImage(WinoIconGlyph.WinoIntelligence, 16),
            ImagePosition = NSCellImagePosition.ImageLeading,
            ToolTip = Translator.Composer_AiRewrite,
            Hidden = true,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _rewriteButton.HeightAnchor.ConstraintEqualTo(30).Active = true;
        _rewriteButton.Activated += (_, _) => ShowRewriteMenu();
        WinoAccessibility.Label(_rewriteButton, Translator.Composer_AiRewrite);
        _rewriteButton.AccessibilityRole = NSAccessibilityRoles.MenuButtonRole;
        return _rewriteButton;
    }

    /// <summary>A Wino glyph filled with the brand gradient, redrawn for the current appearance.</summary>
    private static NSImage BrandGlyphImage(WinoIconGlyph glyph, double size)
    {
        var text = WinoIcons.Glyph(glyph);
        var image = NSImage.ImageWithSize(new CGSize(size, size), false, rect =>
        {
            if (NSGraphicsContext.CurrentContext?.CGContext is not { } context) return false;
            var appearance = NSAppearance.CurrentDrawingAppearance;
            context.BeginTransparencyLayer();
            WinoIcons.Draw(text, rect, NSColor.Black, appearance, false);
            context.SetBlendMode(CGBlendMode.SourceIn);
            WinoIntelligenceBrand.FillGradient(context, rect, WinoIcons.IsDark(appearance), false);
            context.EndTransparencyLayer();
            return true;
        });
        image.AccessibilityDescription = Translator.Composer_AiRewrite;
        return image;
    }

    private void ShowRewriteMenu()
    {
        var session = ViewModel.RewriteSession;
        if (!session.IsAvailable || session.IsBusy) return;
        var menu = new NSMenu(Translator.Composer_AiRewriteMode) { AutoEnablesItems = false };
        var header = new NSMenuItem(Translator.Composer_AiRewriteMode) { Enabled = false };
        header.AttributedTitle = new NSAttributedString(Translator.Composer_AiRewriteMode, new NSStringAttributes
        {
            Font = NSFont.SystemFontOfSize(11, NSFontWeight.Semibold),
            ForegroundColor = WinoStyle.SecondaryText
        });
        menu.AddItem(header);
        foreach (var mode in session.Modes)
        {
            var option = mode;
            var item = new NSMenuItem(option.Label, (_, _) => StartRewrite(option.Mode));
            item.AccessibilityIdentifier = $"ComposeRewriteMode_{option.Mode}";
            menu.AddItem(item);
        }
        _rewriteButton.Highlight(true);
        menu.PopUpMenu(null, new CGPoint(0, _rewriteButton.IsFlipped ? _rewriteButton.Bounds.Height + 4 : -4), _rewriteButton);
        _rewriteButton.Highlight(false);
    }

    private void StartRewrite(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return;
        _lastRewriteMode = mode;
        _rewriteError = null;
        UpdateRewriteStrip();
        Observe(ViewModel.RewriteSession.RewriteCommand.ExecuteAsync(mode));
    }

    private NSView BuildRewriteStrip()
    {
        _rewriteRing = new ComposeIntelligenceRing();
        WinoAccessibility.Label(_rewriteRing, Translator.WinoIntelligence_Rewriting);
        _rewriteGlyph = new BrandIconView(WinoIconGlyph.WinoIntelligence, 16);
        _rewriteWarning = new WinoIconView(WinoIconGlyph.Warning, 16, WinoStyle.Hex(0xC42B1C));
        _rewriteStatus = WinoStyle.Label(string.Empty, WinoStyle.Body);
        _rewriteStatus.LineBreakMode = NSLineBreakMode.TruncatingTail;
        _rewriteStatus.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        _rewriteCancel = StripButton(Translator.Buttons_Cancel, () => ViewModel.RewriteSession.CancelCommand.Execute(null));
        _rewriteToggle = StripButton(Translator.WinoIntelligence_ShowOriginal, () => Observe(ViewModel.RewriteSession.ToggleOriginalCommand.ExecuteAsync(null)));
        _rewriteRegenerate = new NSButton
        {
            Bordered = false,
            BezelStyle = NSBezelStyle.Inline,
            Title = string.Empty,
            Image = WinoIcons.Image(WinoIconGlyph.ArrowClockwise, 14, null, Translator.WinoIntelligence_Regenerate),
            ImagePosition = NSCellImagePosition.ImageOnly,
            ContentTintColor = WinoStyle.SecondaryText,
            ToolTip = Translator.WinoIntelligence_Regenerate,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Size(_rewriteRegenerate, 28, 24);
        _rewriteRegenerate.Activated += (_, _) => { _rewriteError = null; Observe(ViewModel.RewriteSession.RegenerateCommand.ExecuteAsync(null)); };
        WinoAccessibility.Label(_rewriteRegenerate, Translator.WinoIntelligence_Regenerate);
        _rewriteKeep = StripButton(Translator.Composer_AiRewriteKeep, () => ViewModel.RewriteSession.KeepCommand.Execute(null));
        _rewriteKeep.BezelColor = WinoStyle.Accent;
        _rewriteRetry = StripButton(Translator.Buttons_Retry, () => StartRewrite(_lastRewriteMode));
        _rewriteDismiss = new NSButton
        {
            Bordered = false,
            BezelStyle = NSBezelStyle.Inline,
            Title = string.Empty,
            Image = WinoIcons.Image(WinoIconGlyph.Dismiss, 12, null, Translator.Buttons_Close),
            ImagePosition = NSCellImagePosition.ImageOnly,
            ContentTintColor = WinoStyle.SecondaryText,
            ToolTip = Translator.Buttons_Close,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Size(_rewriteDismiss, 24, 24);
        _rewriteDismiss.Activated += (_, _) => { _rewriteError = null; UpdateRewriteStrip(); };
        WinoAccessibility.Label(_rewriteDismiss, Translator.Buttons_Close);

        var row = WinoLayout.HStack(8, _rewriteRing, _rewriteGlyph, _rewriteWarning, _rewriteStatus, WinoLayout.Spacer(),
            _rewriteCancel, _rewriteToggle, _rewriteRegenerate, _rewriteKeep, _rewriteRetry, _rewriteDismiss);
        row.EdgeInsets = new NSEdgeInsets(0, 8, 0, 6);
        var surface = new WinoSurfaceView
        {
            Fill = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.04), WinoStyle.Hex(0xFFFFFF, 0.06)),
            Stroke = WinoStyle.ZoneStroke,
            CornerRadius = 6,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Fill(row, surface);
        surface.HeightAnchor.ConstraintEqualTo(34).Active = true;
        surface.AccessibilityElement = true;
        surface.AccessibilityRole = NSAccessibilityRoles.GroupRole;
        WinoAccessibility.Label(surface, Translator.Composer_AiRewrite);

        _rewriteHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        WinoLayout.Fill(surface, _rewriteHost, 6, 16, 6, 16);
        return _rewriteHost;
    }

    private static NSButton StripButton(string title, Action action)
    {
        var button = new NSButton { Title = title, BezelStyle = NSBezelStyle.Push, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false };
        button.Activated += (_, _) => action();
        WinoAccessibility.Label(button, title);
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
        if (_rewriteHost is null || Bindings.IsDisposed) return;
        var session = ViewModel.RewriteSession;
        var busy = session.IsAvailable && session.IsBusy;
        var result = session.IsAvailable && session.IsResultVisible;
        if (busy) _rewriteError = null;
        var error = !busy && _rewriteError is not null;

        _rewriteButton.Hidden = !session.IsAvailable;
        _rewriteButton.Enabled = !session.IsBusy;

        _rewriteHost.Hidden = !(busy || result || error);
        _rewriteRing.Hidden = !busy;
        _rewriteRing.IsAnimating = busy;
        _rewriteGlyph.Hidden = !result || error;
        _rewriteWarning.Hidden = !error;
        _rewriteStatus.StringValue = error ? _rewriteError! : session.StatusText ?? string.Empty;
        _rewriteStatus.TextColor = error ? WinoStyle.Hex(0xC42B1C) : WinoStyle.PrimaryText;
        _rewriteCancel.Hidden = !busy;
        _rewriteToggle.Hidden = !result || error;
        _rewriteToggle.Title = session.ToggleText;
        WinoAccessibility.Label(_rewriteToggle, session.ToggleText);
        _rewriteRegenerate.Hidden = !result || error;
        _rewriteKeep.Hidden = !result || error;
        _rewriteRetry.Hidden = !error || _lastRewriteMode is null;
        _rewriteDismiss.Hidden = !error;

        // The strip is a polite live region: announce each new status once.
        var status = _rewriteHost.Hidden ? string.Empty : _rewriteStatus.StringValue;
        if (status.Length > 0 && status != _announcedRewriteStatus) Announce(status);
        _announcedRewriteStatus = status;
    }

    private void Announce(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        NSAccessibility.PostNotification(View.Window ?? (NSObject)View, new NSString("AXAnnouncementRequested"),
            NSDictionary.FromObjectAndKey(new NSString(text), NSAccessibilityNotificationUserInfoKeys.AnnouncementKey));
    }
}
