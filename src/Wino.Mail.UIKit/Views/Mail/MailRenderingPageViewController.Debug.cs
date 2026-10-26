#if DEBUG
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Models.Printing;
using Wino.Mail.UIKit.Infrastructure;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// Debug-bridge commands for the reader (Mac RegisterReaderDebugCommands), following the most recently loaded reader:
/// <c>readerbar</c> (the command bar and the shared command model), <c>reader-badges</c>, <c>reader-print</c> (presents
/// the print panel and returns so <c>snap</c> can capture it), <c>reader-pdf PATH</c> (a relative path lands in the
/// debug folder), <c>reader-view on|off</c>, <c>reader-recipients</c> and <c>reader-source</c>.
/// </summary>
public sealed partial class MailRenderingPageViewController
{
    private void RegisterReaderDebugCommands()
    {
        UIKitDebugBridge.Register("readerbar", _ => ReaderDebugAsync(() =>
            _commandBar.Dump() + $" | primary=[{string.Join(", ", _primaryCommands.Select(static command => command is null ? "|" : $"{command.Operation}{(command.IsEnabled ? string.Empty : "(off)")}"))}]" +
            $" overflow={_overflow.Length} menu={ViewModel.MenuItems.Count} showsBar={_showsCommandBar}"));
        UIKitDebugBridge.Register("reader-badges", _ => ReaderDebugAsync(() =>
            $"canUnsubscribe={ViewModel.CanUnsubscribe} signed={ViewModel.IsSmimeSigned} valid={ViewModel.SmimeSignaturesValid} " +
            $"encrypted={ViewModel.IsSmimeEncrypted} | {_statusRow.Dump()} | printPresenter={(ViewModel.PrintPresenter is null ? "none" : "set")}"));
        UIKitDebugBridge.Register("reader-print", _ => ReaderDebugAsync(() =>
        {
            if (_disposedReader || ViewModel.PrintPresenter is not { } presenter) return "no print presenter";
            Observe(presenter.PrintAsync(new MailPrintRequest(string.IsNullOrWhiteSpace(ViewModel.Subject) ? Translator.MailItemNoSubject : ViewModel.Subject)));
            return "print panel requested";
        }));
        UIKitDebugBridge.Register("reader-pdf", async args =>
        {
            if (args.Length == 0) return "usage: reader-pdf PATH";
            var path = string.Join(' ', args);
            if (!Path.IsPathRooted(path)) path = Path.Combine(UIKitDebugBridge.CommandFolder, path);
            IMailPrintPresenter? presenter = null;
            await Dispatcher.ExecuteOnUIThread(() => presenter = _disposedReader ? null : ViewModel.PrintPresenter);
            if (presenter is null) return "no print presenter";
            var result = await presenter.ExportPdfAsync(path);
            var length = File.Exists(path) ? new FileInfo(path).Length : 0;
            return $"{result.Status} {path} bytes={length} {result.ErrorMessage}".TrimEnd();
        });
        UIKitDebugBridge.Register("reader-view", args => ReaderDebugAsync(() =>
        {
            if (args.Length > 0 && (args[0] == "on") != ViewModel.IsReaderViewEnabled) ToggleReaderView();
            return $"readerView={ViewModel.IsReaderViewEnabled} rendered={!string.IsNullOrWhiteSpace(_currentRenderedHtml)}";
        }));
        UIKitDebugBridge.Register("reader-recipients", args => ReaderDebugAsync(() =>
        {
            if (args.Length > 0 && args[0] == "card" && _senderLink is not null) ShowContactCard(ViewModel.FromName, ViewModel.FromAddress, _senderLink);
            return _recipientRows.Dump() + $" card={_contactCard is not null} subject='{_subject.Text}' sender='{_senderName.Text}' date='{_date.Text}' " +
                $"attachments={_attachmentTiles.ArrangedSubviews.Length} body={_webHeight?.Constant}";
        }));
        UIKitDebugBridge.Register("reader-source", _ => ReaderDebugAsync(() =>
        {
            var item = ViewModel.MenuItems.OfType<MailOperationMenuItem>().FirstOrDefault(candidate => candidate.Operation == MailOperation.ViewMessageSource);
            if (item is null) return "no message source operation";
            Observe(ViewModel.OperationClickedCommand.ExecuteAsync(item));
            return "message source requested";
        }));
        // "reader-tap Move" runs a primary command as its bar button does, anchored to the command bar.
        UIKitDebugBridge.Register("reader-tap", args => ReaderDebugAsync(() =>
        {
            if (args.Length == 0 || !Enum.TryParse<MailOperation>(args[0], true, out var operation)) return "usage: reader-tap OPERATION";
            if (_primaryCommands.FirstOrDefault(command => command?.Operation == operation) is not { } command) return "no command " + operation;
            command.Invoke(_commandBar);
            return $"invoked {command.Title} enabled={command.IsEnabled}";
        }));
        // "reader-theme": the reader's appearance and what the document applied (theme, surface, inset, mail padding).
        UIKitDebugBridge.Register("reader-theme", async _ =>
        {
            var native = string.Empty;
            await Dispatcher.ExecuteOnUIThread(() => native = $"dark={_dark} appearanceDark={IsDarkAppearance()} webHeight={_webHeight?.Constant}");
            var result = new TaskCompletionSource<string>();
            await Dispatcher.ExecuteOnUIThread(() => _webView.EvaluateJavaScript(
                "(() => { const r = document.getElementById('wino-reader'); const h = r && r.shadowRoot && r.shadowRoot.querySelector('.wino-mail-html'); " +
                "return [document.documentElement.dataset.theme, document.documentElement.dataset.surface, r && r.hasAttribute('data-inset'), h ? getComputedStyle(h).paddingLeft : 'none'].join(' '); })()",
                (value, error) => result.TrySetResult(error is null ? value?.ToString() ?? "null" : "js error " + error.LocalizedDescription)));
            return native + " | " + await result.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });
        UIKitDebugBridge.Register("reader-bar", args => ReaderDebugAsync(() =>
        {
            if (args.Length > 0) ShowsCommandBar = args[0] == "on";
            return $"showsBar={_showsCommandBar}";
        }));
    }

    private async Task<string> ReaderDebugAsync(Func<string> read)
    {
        var result = string.Empty;
        await Dispatcher.ExecuteOnUIThread(() => result = read());
        return result;
    }
}
#endif
