#if DEBUG
using Wino.Mail.MacOS.Infrastructure;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// Debug-bridge commands for the reader's Wino Intelligence header (DEBUG builds only):
/// <c>intel-header</c> (presenter and view state), <c>intel-summarize</c> (expands the header,
/// opens the summary panel and waits for the summary), <c>intel-translate [code]</c> (optionally
/// sets the target language, opens the translate panel and runs the translation or toggles it)
/// and <c>intel-process</c> (queues the message for processing). They only drive the header;
/// nothing signs in or changes an account.
/// </summary>
public sealed partial class MailRenderingPageViewController
{
    private void RegisterIntelligenceDebugCommands()
    {
        MacDebugBridge.Register("intel-header", _ => IntelligenceDebugAsync(() => Task.CompletedTask));

        MacDebugBridge.Register("intel-summarize", _ => IntelligenceDebugAsync(() =>
        {
            if (_intelligence is not { IsSummaryAvailable: true } presenter) return Task.CompletedTask;
            _intelligenceHeader?.ShowSummary();
            // ShowSummary raised the request when there was no summary yet; regenerate otherwise.
            return presenter.SummaryState == Wino.Mail.Controls.Core.IntelligenceHeader.WinoIntelligenceFeatureState.Done
                ? presenter.RequestSummaryCommand.ExecuteAsync(null)
                : WaitForSummaryAsync(presenter);
        }));

        MacDebugBridge.Register("intel-translate", args => IntelligenceDebugAsync(() =>
        {
            if (_intelligence is not { IsTranslateAvailable: true } presenter) return Task.CompletedTask;
            if (args.Length > 0) presenter.SelectedTargetLanguage = args[0];
            _intelligenceHeader?.ShowTranslation();
            return presenter.TranslateCommand.ExecuteAsync(null);
        }));

        MacDebugBridge.Register("intel-process", _ => IntelligenceDebugAsync(() =>
            _intelligence is { CanRequestProcessing: true } presenter
                ? presenter.RequestProcessingCommand.ExecuteAsync(null)
                : Task.CompletedTask));
    }

    private static async Task WaitForSummaryAsync(Wino.Mail.ViewModels.Intelligence.WinoIntelligenceHeaderPresenter presenter)
    {
        for (var attempt = 0; attempt < 120 && presenter.SummaryState == Wino.Mail.Controls.Core.IntelligenceHeader.WinoIntelligenceFeatureState.Busy; attempt++)
            await Task.Delay(500);
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread, waits for it, and reports the header state.</summary>
    private async Task<string> IntelligenceDebugAsync(Func<Task> action)
    {
        if (_intelligenceDisposed || _intelligence is null) return "no reader";
        Task operation = Task.CompletedTask;
        await Dispatcher.ExecuteOnUIThread(() => operation = action());
        await operation;
        var result = string.Empty;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            var presenter = _intelligence;
            result = presenter is null
                ? "no reader"
                : $"visible={presenter.IsVisible} key={presenter.ContentKey} canProcess={presenter.CanRequestProcessing} " +
                  $"showingTranslation={presenter.IsShowingTranslation} | {_intelligenceHeader?.Dump()}";
        });
        return result;
    }
}
#endif
