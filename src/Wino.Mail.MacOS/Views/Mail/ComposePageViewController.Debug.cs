#if DEBUG
using Wino.Mail.MacOS.Infrastructure;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// Debug-bridge commands for the active composer (DEBUG builds only), so the extras can be shown for
/// screenshots: <c>compose-rewrite-menu</c> (tone menu; blocks until it closes),
/// <c>compose-rewrite-state busy|result|error|off</c> (strip preview without calling the service),
/// <c>compose-templates</c>, <c>compose-signature</c> (pull-down; blocks until it closes),
/// <c>compose-options</c> (Options segment), <c>compose-find [text]</c>, <c>compose-replace [text]</c>
/// and <c>compose-find-close</c>. Nothing is sent or saved.
/// </summary>
public sealed partial class ComposePageViewController
{
    private void RegisterComposeDebugCommands()
    {
        MacDebugBridge.Register("compose-rewrite-menu", _ => OnUIAsync(() =>
        {
            var available = ViewModel.RewriteSession.IsAvailable;
            if (!available) _rewriteButton.Hidden = false;
            // The menu tracks modally; open it after this command returns so the bridge can snapshot.
            AppKit.NSApplication.SharedApplication.BeginInvokeOnMainThread(ShowRewriteMenuForDebug);
            return available ? "ok" : "ok (rewrite unavailable for this account; button shown for preview)";
        }));
        MacDebugBridge.Register("compose-rewrite-state", args => OnUIAsync(() =>
        {
            var state = args.Length > 0 ? args[0].ToLowerInvariant() : "busy";
            PreviewRewriteState(state);
            return $"state={state}";
        }));
        MacDebugBridge.Register("compose-templates", _ => OnUIAsync(() =>
        {
            Observe(ShowTemplatesAsync());
            return $"templates={ViewModel.AvailableEmailTemplates.Count}";
        }));
        MacDebugBridge.Register("compose-signature", _ => OnUIAsync(() =>
        {
            AppKit.NSApplication.SharedApplication.BeginInvokeOnMainThread(() => _signaturePopup.PerformClick(null));
            return $"signatures={ViewModel.AvailableSignatures.Count} selected={ViewModel.SelectedSignature?.Name ?? "none"}";
        }));
        MacDebugBridge.Register("compose-options", _ => OnUIAsync(() =>
        {
            _toolbarTabs.SelectedSegment = 2;
            ShowToolbarGroup();
            return $"smimeAvailable={ViewModel.IsSmimeAvailable} certificates={ViewModel.AvailableCertificates.Count} sign={ViewModel.IsSmimeSignatureEnabled} encrypt={ViewModel.IsSmimeEncryptionEnabled} readReceipt={ViewModel.IsReadReceiptRequested}";
        }));
        MacDebugBridge.Register("compose-find", async args =>
        {
            await OnUIAsync(() => { ShowFind(false, args.Length > 0 ? string.Join(' ', args) : null); return string.Empty; });
            await Task.Delay(400);
            return await OnUIAsync(() => $"counter='{_findBar.CounterText}'");
        });
        MacDebugBridge.Register("compose-replace", async args =>
        {
            await OnUIAsync(() => { ShowFind(true, args.Length > 0 ? string.Join(' ', args) : null); return string.Empty; });
            await Task.Delay(400);
            return await OnUIAsync(() => $"counter='{_findBar.CounterText}'");
        });
        MacDebugBridge.Register("compose-find-close", _ => OnUIAsync(() => { CloseFind(); return "ok"; }));
    }

    private void ShowRewriteMenuForDebug()
    {
        var session = ViewModel.RewriteSession;
        if (session.IsAvailable) { ShowRewriteMenu(); return; }
        // Preview only: the real menu refuses while the account is not eligible.
        var menu = new AppKit.NSMenu();
        menu.AddItem(new AppKit.NSMenuItem(Wino.Core.Domain.Translator.Composer_AiRewriteMode) { Enabled = false });
        foreach (var mode in session.Modes) menu.AddItem(new AppKit.NSMenuItem(mode.Label));
        menu.PopUpMenu(null, new CoreGraphics.CGPoint(0, -4), _rewriteButton);
    }

    /// <summary>Paints a strip state without the session (the service is not called).</summary>
    private void PreviewRewriteState(string state)
    {
        _rewriteButton.Hidden = false;
        _rewriteError = null;
        if (state == "off")
        {
            UpdateRewriteStrip();
            return;
        }
        var busy = state == "busy";
        var result = state == "result";
        var error = state == "error";
        if (error) _rewriteError = Wino.Core.Domain.Translator.WinoIntelligence_ActionFailed;
        _lastRewriteMode ??= "formal";
        _rewriteHost.Hidden = false;
        _rewriteRing.Hidden = !busy;
        _rewriteRing.IsAnimating = busy;
        _rewriteGlyph.Hidden = !result;
        _rewriteWarning.Hidden = !error;
        _rewriteStatus.StringValue = busy ? Wino.Core.Domain.Translator.WinoIntelligence_Rewriting
            : result ? string.Format(Wino.Core.Domain.Translator.WinoIntelligence_RewriteAppliedFormat, Wino.Core.Domain.Translator.Composer_AiRewriteFormal)
            : _rewriteError ?? string.Empty;
        _rewriteStatus.TextColor = error ? Wino.Presentation.AppKit.WinoStyle.Hex(0xC42B1C) : Wino.Presentation.AppKit.WinoStyle.PrimaryText;
        _rewriteCancel.Hidden = !busy;
        _rewriteToggle.Hidden = !result;
        _rewriteToggle.Title = Wino.Core.Domain.Translator.WinoIntelligence_ShowOriginal;
        _rewriteRegenerate.Hidden = !result;
        _rewriteKeep.Hidden = !result;
        _rewriteRetry.Hidden = !error;
        _rewriteDismiss.Hidden = !error;
    }

    private async Task<string> OnUIAsync(Func<string> action)
    {
        if (Bindings.IsDisposed || !ViewLoaded) return "no composer";
        var result = string.Empty;
        await Dispatcher.ExecuteOnUIThread(() => result = action());
        return result;
    }
}
#endif
