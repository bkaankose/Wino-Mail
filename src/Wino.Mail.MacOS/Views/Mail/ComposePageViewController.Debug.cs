#if DEBUG
using Wino.Mail.MacOS.Infrastructure;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// Debug-bridge commands for the active composer (DEBUG builds only), so the extras can be shown for
/// screenshots: <c>compose-rewrite-menu</c> (tone menu; blocks until it closes),
/// <c>compose-rewrite-state busy|result|error|off</c> (strip preview without calling the service),
/// <c>compose-templates</c>, <c>compose-signature</c> (pull-down; blocks until it closes),
/// <c>compose-options</c> (Options tab), <c>compose-find [text]</c>, <c>compose-replace [text]</c>
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
            _formatToolbar.SelectTab(2);
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
        RegisterParityDebugCommands();
    }

    /// <summary>
    /// <c>compose-toolbar [format|insert|options]</c> (selects the tab; returns visible and overflowed items),
    /// <c>compose-toolbar-more</c> (overflow menu; blocks until it closes), <c>compose-suggest to|cc|bcc QUERY</c>
    /// (suggestion rows), <c>compose-syncfail [message|off]</c> (local preview of the upload failure bar;
    /// nothing is saved), <c>compose-attach-sample</c>, <c>compose-table</c>, <c>compose-link</c>, <c>compose-image-props</c>.
    /// </summary>
    private void RegisterParityDebugCommands()
    {
        MacDebugBridge.Register("compose-toolbar", args => OnUIAsync(() =>
        {
            if (args.Length > 0) _formatToolbar.SelectTab(args[0].ToLowerInvariant() switch { "insert" => 1, "options" => 2, _ => 0 });
            _formatToolbar.LayoutSubtreeIfNeeded();
            return _formatToolbar.Describe();
        }));
        MacDebugBridge.Register("compose-toolbar-more", _ => OnUIAsync(() =>
        {
            AppKit.NSApplication.SharedApplication.BeginInvokeOnMainThread(_formatToolbar.ShowOverflowMenu);
            return _formatToolbar.Describe();
        }));
        MacDebugBridge.Register("compose-suggest", async args =>
        {
            if (args.Length < 2) return "usage: compose-suggest to|cc|bcc QUERY";
            var field = args[0].ToLowerInvariant() switch { "cc" => _ccField, "bcc" => _bccField, _ => _toField };
            var query = string.Join(' ', args.Skip(1));
            var rows = await SuggestAsync(field, query);
            await OnUIAsync(() =>
            {
                if (field == _ccField || field == _bccField) ViewModel.IsCCBCCVisible = true;
                _suggestionPopup?.Show(field, rows, query);
                return string.Empty;
            });
            return string.Join("\n", rows.Select(row => $"{row.Source} '{row.DisplayName}' {row.SecondaryText} canSuppress={row.CanSuppress} members={row.ListMembers.Count}"));
        });
        MacDebugBridge.Register("compose-syncfail", args => OnUIAsync(() =>
        {
            // Preview only: IsDraftSyncFailed is flipped locally; the draft and its error are not touched.
            var off = args.Length > 0 && args[0].Equals("off", StringComparison.OrdinalIgnoreCase);
            ViewModel.IsDraftSyncFailed = !off;
            if (!off && args.Length > 0) _syncFailedBar.Message = $"{ViewModel.DraftSyncErrorMessage}\n{string.Join(' ', args)}";
            return $"failed={ViewModel.IsDraftSyncFailed} visible={!(_syncFailedBar.Superview?.Hidden ?? true)}";
        }));
        MacDebugBridge.Register("compose-attach-sample", _ => OnUIAsync(() =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("Wino debug attachment");
            ViewModel.AddAttachment(new Wino.Core.Domain.Models.Common.SharedFile(Path.Combine(Path.GetTempPath(), "wino-sample.txt"), bytes));
            return $"attachments={ViewModel.IncludedAttachments.Count} summary='{ViewModel.AttachmentsSummary}'";
        }));
        MacDebugBridge.Register("compose-table", _ => OnUIAsync(() => { Observe(_formatToolbar.InsertTableAsync()); return "ok"; }));
        MacDebugBridge.Register("compose-link", _ => OnUIAsync(() => { Observe(_formatToolbar.EditLinkAsync()); return "ok"; }));
        MacDebugBridge.Register("compose-image-props", _ => OnUIAsync(() =>
        {
            if (!_formatToolbar.CurrentState.IsImageSelected) return "no image selected";
            Observe(_formatToolbar.EditImagePropertiesAsync());
            return "ok";
        }));
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
