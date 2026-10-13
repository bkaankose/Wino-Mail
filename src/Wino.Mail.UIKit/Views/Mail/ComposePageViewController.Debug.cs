#if DEBUG
using Wino.Core.Domain;
using Wino.Mail.UIKit.Infrastructure;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// Debug-bridge commands for the active composer (DEBUG builds only, Mac compose-* commands). Nothing is sent or saved
/// unless the command says so:
/// <c>compose-dump</c> (state), <c>compose-focus-body</c>, <c>compose-focus-to</c>, <c>compose-toolbar [format|insert|options]</c>,
/// <c>compose-find [text]</c>, <c>compose-replace [text]</c>, <c>compose-find-close</c>, <c>compose-ccbcc</c>,
/// <c>compose-test ADDRESS SUBJECT…</c> (sets the only To recipient and the subject), <c>compose-send</c> (sends),
/// <c>compose-suggest to|cc|bcc QUERY</c>, <c>compose-attach-sample</c>, <c>compose-rewrite-state busy|result|error|off</c>,
/// <c>compose-syncfail [message|off]</c>, <c>compose-templates</c>, <c>compose-table</c>, <c>compose-link</c>,
/// <c>compose-hide-keyboard</c>.
/// </summary>
public sealed partial class ComposePageViewController
{
    private void RegisterComposeDebugCommands()
    {
        UIKitDebugBridge.Register("compose-dump", _ => OnUIAsync(DescribeState));
        // The bar's "Open in new window" (iPad, docked in the reader column).
        UIKitDebugBridge.Register("compose-popout", async _ =>
        {
            IReaderFrameHost? frame = null;
            await Dispatcher.ExecuteOnUIThread(() => frame = FrameHost);
            if (frame is not { CanOpenInNewWindow: true }) return "no frame host or no multiple scenes";
            Task<bool>? request = null;
            await Dispatcher.ExecuteOnUIThread(() => request = frame.OpenInNewWindowAsync(this));
            return request is not null && await request ? "requested" : "refused";
        });
        UIKitDebugBridge.Register("compose-focus-body", _ => OnUIAsync(() => { FocusBody(); return "ok"; }));
        UIKitDebugBridge.Register("compose-focus-to", _ => OnUIAsync(() => { ToField.Focus(); return "ok"; }));
        UIKitDebugBridge.Register("compose-hide-keyboard", _ => OnUIAsync(() => { View?.EndEditing(true); return "ok"; }));
        UIKitDebugBridge.Register("compose-toolbar", args => OnUIAsync(() =>
        {
            if (_formatToolbar is not { } toolbar) return "no toolbar";
            if (args.Length > 0) toolbar.SelectSection(args[0].ToLowerInvariant() switch { "insert" => 1, "options" => 2, _ => 0 });
            if (!_webView.IsFirstResponder) FocusBody();
            return toolbar.Describe();
        }));
        UIKitDebugBridge.Register("compose-find", async args =>
        {
            await OnUIAsync(() => { ShowFind(false, args.Length > 0 ? string.Join(' ', args) : null); return string.Empty; });
            await Task.Delay(400);
            return await OnUIAsync(() => $"counter='{_findBar?.CounterText}'");
        });
        UIKitDebugBridge.Register("compose-replace", async args =>
        {
            await OnUIAsync(() => { ShowFind(true, args.Length > 0 ? string.Join(' ', args) : null); return string.Empty; });
            await Task.Delay(400);
            return await OnUIAsync(() => $"counter='{_findBar?.CounterText}'");
        });
        UIKitDebugBridge.Register("compose-find-close", _ => OnUIAsync(() => { CloseFind(); return "ok"; }));
        UIKitDebugBridge.Register("compose-ccbcc", _ => OnUIAsync(() => { ViewModel.IsCCBCCVisible = true; return "ok"; }));
        UIKitDebugBridge.Register("compose-test", async args =>
        {
            if (args.Length == 0) return "usage: compose-test ADDRESS SUBJECT…";
            var contact = await ViewModel.GetAddressInformationAsync(args[0], ViewModel.ToItems);
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                ViewModel.ToItems.Clear();
                if (contact is not null) ViewModel.TryAddRecipient(ViewModel.ToItems, contact);
                _subjectField.Text = string.Join(' ', args.Skip(1));
                ViewModel.Subject = _subjectField.Text ?? string.Empty;
            });
            return $"to={ViewModel.ToItems.Count} subject='{ViewModel.Subject}' canSend={ViewModel.SendCommand.CanExecute(null)}";
        });
        UIKitDebugBridge.Register("compose-send", async _ => { await SendAsync(); return "ok"; });
        UIKitDebugBridge.Register("compose-suggest", async args =>
        {
            if (args.Length < 2) return "usage: compose-suggest to|cc|bcc QUERY";
            var field = args[0].ToLowerInvariant() switch { "cc" => CcField, "bcc" => BccField, _ => ToField };
            var query = string.Join(' ', args.Skip(1));
            var rows = await SuggestAsync(field, query);
            await OnUIAsync(() =>
            {
                if (!ReferenceEquals(field, ToField)) ViewModel.IsCCBCCVisible = true;
                ShowSuggestions(field, rows, query);
                return string.Empty;
            });
            return string.Join("\n", rows.Select(row => $"{row.Source} '{row.DisplayName}' {row.SecondaryText} canSuppress={row.CanSuppress} members={row.ListMembers?.Count ?? 0}"));
        });
        UIKitDebugBridge.Register("compose-attach-sample", _ => OnUIAsync(() =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("Wino debug attachment");
            ViewModel.AddAttachment(new Wino.Core.Domain.Models.Common.SharedFile(Path.Combine(Path.GetTempPath(), "wino-sample.txt"), bytes));
            return $"attachments={ViewModel.IncludedAttachments.Count} summary='{ViewModel.AttachmentsSummary}'";
        }));
        UIKitDebugBridge.Register("compose-rewrite-state", args => OnUIAsync(() =>
        {
            var state = args.Length > 0 ? args[0].ToLowerInvariant() : "busy";
            PreviewRewriteState(state);
            return $"state={state} available={ViewModel.RewriteSession.IsAvailable}";
        }));
        UIKitDebugBridge.Register("compose-syncfail", args => OnUIAsync(() =>
        {
            // Preview only: IsDraftSyncFailed is flipped locally; the draft and its error are not touched.
            var off = args.Length > 0 && args[0].Equals("off", StringComparison.OrdinalIgnoreCase);
            ViewModel.IsDraftSyncFailed = !off;
            if (!off && args.Length > 0) _syncFailedBar.Message = $"{ViewModel.DraftSyncErrorMessage}\n{string.Join(' ', args)}";
            return $"failed={ViewModel.IsDraftSyncFailed} visible={!_syncFailedHost.Hidden}";
        }));
        UIKitDebugBridge.Register("compose-templates", _ => OnUIAsync(() =>
        {
            Observe(ShowTemplatesAsync());
            return $"templates={ViewModel.AvailableEmailTemplates.Count}";
        }));
        UIKitDebugBridge.Register("compose-table", _ => OnUIAsync(() => { if (_formatToolbar is { } toolbar) Observe(toolbar.InsertTableAsync()); return "ok"; }));
        UIKitDebugBridge.Register("compose-link", _ => OnUIAsync(() => { if (_formatToolbar is { } toolbar) Observe(toolbar.EditLinkAsync()); return "ok"; }));
    }

    private string DescribeState()
    {
        var account = ViewModel.ComposingAccount;
        return string.Join(' ',
            $"active={IsActive}",
            $"sheetRoot={IsSheetRoot}",
            $"account={(account is null ? "none" : account.Address)}",
            $"draft={(ViewModel.CurrentMailDraftItem is null ? "none" : ViewModel.CurrentMailDraftItem.UniqueId.ToString())}",
            $"to={ViewModel.ToItems.Count} cc={ViewModel.CCItems.Count} bcc={ViewModel.BCCItems.Count} ccbcc={ViewModel.IsCCBCCVisible}",
            $"subject='{ViewModel.Subject}'",
            $"attachments={ViewModel.IncludedAttachments.Count}",
            $"canSend={ViewModel.SendCommand.CanExecute(null)} local={ViewModel.IsLocalDraft}",
            $"signature={ViewModel.SelectedSignature?.Name ?? "none"}/{ViewModel.AvailableSignatures.Count}",
            $"importance={ViewModel.SelectedMessageImportance} readReceipt={ViewModel.IsReadReceiptRequested}",
            $"smime={ViewModel.IsSmimeAvailable} rewrite={ViewModel.RewriteSession.IsAvailable}",
            $"editor={(_editor is null ? "none" : _editorDisposed ? "disposed" : "ready")} bodyFocus={_webView.IsFirstResponder}",
            $"find={IsFindVisible} suggestions={_suggestionList?.Items.Count ?? 0}",
            $"toolbar=[{_formatToolbar?.Describe()}]");
    }

    /// <summary>Paints a strip state without the session (the service is not called).</summary>
    private void PreviewRewriteState(string state)
    {
        if (state == "off")
        {
            _rewriteError = null;
            UpdateRewriteStrip();
            return;
        }
        var busy = state == "busy";
        var result = state == "result";
        var error = state == "error";
        _lastRewriteMode ??= "formal";
        var status = busy ? Translator.WinoIntelligence_Rewriting
            : result ? string.Format(Translator.WinoIntelligence_RewriteAppliedFormat, Translator.Composer_AiRewriteFormal)
            : Translator.WinoIntelligence_ActionFailed;
        ApplyRewriteState(busy, result, error, status, Translator.WinoIntelligence_ShowOriginal);
    }

    private async Task<string> OnUIAsync(Func<string> action)
    {
        if (Bindings.IsDisposed || !IsViewLoaded) return "no composer";
        var result = string.Empty;
        await Dispatcher.ExecuteOnUIThread(() => result = action());
        return result;
    }
}
#endif
