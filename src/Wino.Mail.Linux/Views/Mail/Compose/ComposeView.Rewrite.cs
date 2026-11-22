using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Messaging.UI;
using Wino.Mail.Controls.Linux.Intelligence;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail.Compose;

/// <summary>
/// Wino Intelligence rewrite (macOS ComposePageViewController.Rewrite, Windows ComposeRewritePanel): the "Rewrite" button
/// in the command row with the tone menu, and the status strip above the toolbar for the busy, result and error states.
/// The button is hidden while the account is not eligible. The shared session swaps the editor content through the
/// composer's body accessors; this part never touches the editor.
/// </summary>
internal sealed partial class ComposeView
{
    private readonly Gtk.Button _rewriteButton = Gtk.Button.New();
    private readonly Gtk.Box _rewriteStrip = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    private readonly WinoIntelligenceProgressRing _rewriteRing = new(18);
    private readonly Gtk.Widget _rewriteGlyph = new WinoIcon(WinoIconGlyph.WinoIntelligence, 16, WinoIconMode.Colorful).Root;
    private readonly Gtk.Label _rewriteWarning = WinoIcons.Label(WinoIconGlyph.Warning, 16);
    private readonly Gtk.Label _rewriteStatus = Gtk.Label.New(string.Empty);
    private readonly Gtk.Button _rewriteCancel = StripButton(Translator.Buttons_Cancel);
    private readonly Gtk.Button _rewriteToggle = StripButton(Translator.WinoIntelligence_ShowOriginal);
    private readonly Gtk.Button _rewriteRegenerate = WinoIcons.Button(WinoIconGlyph.ArrowClockwise, Translator.WinoIntelligence_Regenerate, 14);
    private readonly Gtk.Button _rewriteKeep = StripButton(Translator.Composer_AiRewriteKeep);
    private readonly Gtk.Button _rewriteRetry = StripButton(Translator.Buttons_Retry);
    private readonly Gtk.Button _rewriteDismiss = WinoIcons.Button(WinoIconGlyph.Dismiss, Translator.Buttons_Close, 12);
    private string? _rewriteError;
    private string? _lastRewriteMode;

    partial void InitializeRewrite()
    {
        var session = ViewModel.RewriteSession;
        var content = WinoIcons.ButtonContent(WinoIconGlyph.WinoIntelligence, Translator.Composer_AiRewriteShortTitle, 16);
        if (content.GetFirstChild() is Gtk.Widget glyph)
        {
            content.Remove(glyph);
            content.Prepend(new WinoIcon(WinoIconGlyph.WinoIntelligence, 16, WinoIconMode.Colorful).Root);
        }
        _rewriteButton.SetChild(content);
        _rewriteButton.AddCssClass("flat");
        _rewriteButton.SetValign(Gtk.Align.Center);
        _rewriteButton.SetTooltipText(Translator.Composer_AiRewrite);
        _rewriteButton.SetVisible(false);
        WinoA11y.Label(_rewriteButton, Translator.Composer_AiRewrite);
        WinoA11y.HasPopup(_rewriteButton);
        CommandSlot.Append(_rewriteButton);
        Bindings.Own(GtkSignals.OnClicked(_rewriteButton, ShowRewriteMenu));

        BuildRewriteStrip();
        ToolbarSlot.Prepend(_rewriteStrip);
        Bindings.Own(_rewriteRing);

        Bind.For(session).BindAny(static s => (s.IsAvailable, s.IsBusy, s.HasResult, s.IsShowingRewrite, s.StatusText), _ => UpdateRewriteStrip());
        ViewModel.RewriteErrorHandler = ShowRewriteError;
        // Signing in or buying a pack changes eligibility while the composer is open.
        Subscribe<WinoIntelligenceAccessChanged>(_ => Observe(ViewModel.RefreshRewriteAvailabilityAsync()));
        Observe(ViewModel.RefreshRewriteAvailabilityAsync());
    }

    partial void DeactivateRewrite()
    {
        if (ViewModel.RewriteErrorHandler == ShowRewriteError) ViewModel.RewriteErrorHandler = static _ => { };
        _rewriteRing.Active = false;
    }

    private void BuildRewriteStrip()
    {
        _rewriteStrip.AddCssClass("wino-rewrite-strip");
        _rewriteStrip.SetVisible(false);
        WinoA11y.Label(_rewriteStrip, Translator.Composer_AiRewrite);
        WinoA11y.Label(_rewriteRing.Root, Translator.WinoIntelligence_Rewriting);
        _rewriteWarning.AddCssClass("error");
        _rewriteStatus.SetXalign(0);
        _rewriteStatus.SetHexpand(true);
        _rewriteStatus.SetEllipsize(Pango.EllipsizeMode.End);
        _rewriteRegenerate.SetTooltipText(Translator.WinoIntelligence_Regenerate);
        _rewriteDismiss.SetTooltipText(Translator.Buttons_Close);
        _rewriteKeep.AddCssClass("suggested-action");
        foreach (var part in (Gtk.Widget[])[_rewriteRing.Root, _rewriteGlyph, _rewriteWarning, _rewriteStatus, _rewriteCancel, _rewriteToggle,
                     _rewriteRegenerate, _rewriteKeep, _rewriteRetry, _rewriteDismiss])
        {
            part.SetValign(Gtk.Align.Center);
            _rewriteStrip.Append(part);
        }
        var session = ViewModel.RewriteSession;
        Bindings.Own(GtkSignals.OnClicked(_rewriteCancel, () => session.CancelCommand.Execute(null)));
        Bindings.Own(GtkSignals.OnClicked(_rewriteToggle, () => Observe(session.ToggleOriginalCommand.ExecuteAsync(null))));
        Bindings.Own(GtkSignals.OnClicked(_rewriteRegenerate, () =>
        {
            _rewriteError = null;
            Observe(session.RegenerateCommand.ExecuteAsync(null));
        }));
        Bindings.Own(GtkSignals.OnClicked(_rewriteKeep, () => session.KeepCommand.Execute(null)));
        Bindings.Own(GtkSignals.OnClicked(_rewriteRetry, () => StartRewrite(_lastRewriteMode)));
        Bindings.Own(GtkSignals.OnClicked(_rewriteDismiss, () =>
        {
            _rewriteError = null;
            UpdateRewriteStrip();
        }));
    }

    private void ShowRewriteMenu()
    {
        var session = ViewModel.RewriteSession;
        if (!session.IsAvailable || session.IsBusy) return;
        var builder = WinoMenus.Create().Separator(Translator.Composer_AiRewriteMode);
        foreach (var option in session.Modes)
        {
            var mode = option.Mode;
            builder.Item(option.Label, () => StartRewrite(mode));
        }
        ContextMenu.Show(_rewriteButton, builder.Build());
    }

    private void StartRewrite(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode) || IsReleased) return;
        _lastRewriteMode = mode;
        _rewriteError = null;
        UpdateRewriteStrip();
        Observe(ViewModel.RewriteSession.RewriteCommand.ExecuteAsync(mode));
    }

    /// <summary>Rewrite failures appear in the strip (never a dialog), with Retry for the last tone.</summary>
    private void ShowRewriteError(string error) => RunOnUI(() =>
    {
        _rewriteError = string.IsNullOrWhiteSpace(error) ? Translator.WinoIntelligence_ActionFailed : error;
        UpdateRewriteStrip();
    });

    private void UpdateRewriteStrip()
    {
        if (IsReleased) return;
        var session = ViewModel.RewriteSession;
        var busy = session.IsAvailable && session.IsBusy;
        var result = session.IsAvailable && session.IsResultVisible;
        if (busy) _rewriteError = null;
        var error = !busy && _rewriteError is not null;

        _rewriteButton.SetVisible(session.IsAvailable);
        _rewriteButton.SetSensitive(!session.IsBusy);
        _rewriteStrip.SetVisible(busy || result || error);
        _rewriteRing.Root.SetVisible(busy);
        _rewriteRing.Active = busy;
        _rewriteGlyph.SetVisible(result && !error);
        _rewriteWarning.SetVisible(error);
        _rewriteStatus.SetText(error ? _rewriteError ?? string.Empty : session.StatusText ?? string.Empty);
        if (error) _rewriteStatus.AddCssClass("error");
        else _rewriteStatus.RemoveCssClass("error");
        _rewriteCancel.SetVisible(busy);
        _rewriteToggle.SetVisible(result && !error);
        _rewriteToggle.SetLabel(session.ToggleText);
        WinoA11y.Rename(_rewriteToggle, session.ToggleText);
        _rewriteRegenerate.SetVisible(result && !error);
        _rewriteKeep.SetVisible(result && !error);
        _rewriteRetry.SetVisible(error && _lastRewriteMode is not null);
        _rewriteDismiss.SetVisible(error);
    }

    private static Gtk.Button StripButton(string title)
    {
        var button = Gtk.Button.NewWithLabel(title);
        button.AddCssClass("wino-rewrite-button");
        WinoA11y.Label(button, title);
        return button;
    }

#if DEBUG
    internal string DescribeRewrite()
    {
        var session = ViewModel.RewriteSession;
        return $"available={session.IsAvailable} busy={session.IsBusy} result={session.HasResult} showingRewrite={session.IsShowingRewrite} status='{session.StatusText}' "
            + $"error='{_rewriteError}' button={_rewriteButton.GetVisible()} strip={_rewriteStrip.GetVisible()} ring={_rewriteRing.IsTicking} "
            + $"modes={string.Join(",", session.Modes.Select(static m => m.Mode))}";
    }

    internal void DebugStartRewrite(string mode) => StartRewrite(mode);
#endif
}
