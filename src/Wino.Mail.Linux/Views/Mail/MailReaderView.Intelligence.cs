using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Messaging.Client.Shell;
using Wino.Mail.Controls.Core.IntelligenceHeader;
using Wino.Mail.Controls.Linux.Intelligence;
using Wino.Mail.ViewModels.Intelligence;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The Wino Intelligence header above the body (Windows MailRenderingPage "Wino Intelligence" region, macOS
/// MailRenderingPageViewController.Intelligence). The shared <see cref="WinoIntelligenceHeaderPresenter"/> owns the
/// context, the snapshot and every request; this part copies its state onto <see cref="WinoIntelligenceHeader"/>, routes
/// the header's actions back, and renders the translated body when the presenter asks. Each reader (pane, pop-out, file)
/// has its own presenter.
/// </summary>
internal sealed partial class MailReaderView
{
    private WinoIntelligenceHeaderPresenter? _intelligence;
    private WinoIntelligenceHeader? _intelligenceHeader;
    private bool _intelligenceDisposed;

    partial void BuildIntelligence()
    {
        var presenter = _intelligence = _provider.GetRequiredService<WinoIntelligenceHeaderPresenter>();
        var header = _intelligenceHeader = new WinoIntelligenceHeader();
        _intelligenceSlot.SetChild(header.Root);
        Bindings.Own(header);

        header.ProcessRequested += () => Observe(presenter.RequestProcessingCommand.ExecuteAsync(null));
        header.CopyCodeRequested += () => Observe(presenter.CopyVerificationCodeCommand.ExecuteAsync(null));
        header.SummaryRequested += () => Observe(presenter.RequestSummaryCommand.ExecuteAsync(null));
        header.SummaryCancelRequested += presenter.CancelSummary;
        header.TranslateRequested += () => Observe(presenter.TranslateCommand.ExecuteAsync(null));
        header.TranslationCancelRequested += presenter.CancelTranslation;
        header.SourceLanguageChanged += code => presenter.SelectedSourceLanguage = code;
        header.TargetLanguageChanged += code => presenter.SelectedTargetLanguage = code;

        PropertyChangedEventHandler changed = (_, _) => RunOnUI(SyncIntelligenceHeader);
        Func<Task> rerender = IntelligenceRerenderAsync;
        presenter.PropertyChanged += changed;
        presenter.RerenderRequested += rerender;
        Bindings.Own(new ActionDisposable(() =>
        {
            _intelligenceDisposed = true;
            presenter.PropertyChanged -= changed;
            presenter.RerenderRequested -= rerender;
            presenter.Dispose();
        }));
        SyncIntelligenceHeader();
    }

    partial void InitializeIntelligence()
    {
        if (_intelligence is not { } presenter) return;
        ResolveActiveHtml = html => Task.FromResult(_intelligenceDisposed ? html : presenter.ResolveActiveHtml(html));
        Subscribe<LanguageChanged>(_ =>
        {
            if (!_intelligenceDisposed) presenter.OnLanguageChanged();
        });
        WhenRenavigating(() => IntelligenceBeginItem(presenter));
        WhenRendered(html => _intelligenceDisposed ? Task.CompletedTask : LoadIntelligenceContextAsync(presenter, html));
        WhenCleared(() =>
        {
            if (!_intelligenceDisposed) presenter.ResetContent();
        });
        IntelligenceBeginItem(presenter);
#if DEBUG
        WhenDescribing(() => $"intelligence visible={_intelligenceSlot.GetVisible()} key='{presenter.ContentKey}' {_intelligenceHeader?.Describe()}");
#endif
    }

    /// <summary>Everything after this belongs to the new message, as in the Windows reader's re-entry.</summary>
    private void IntelligenceBeginItem(WinoIntelligenceHeaderPresenter presenter)
    {
        if (_intelligenceDisposed) return;
        presenter.Attach();
        presenter.ResetContent();
        presenter.BeginMailItem(_currentItem);
    }

    private async Task LoadIntelligenceContextAsync(WinoIntelligenceHeaderPresenter presenter, string html)
    {
        var viewModel = ViewModel;
        try { await presenter.OnHtmlRendered(html, viewModel.Subject, viewModel.FromAddress, viewModel.CreationDate); }
        // The intelligence context is optional; a failure never blocks the message body.
        catch (Exception exception) { ReportError(exception); }
    }

    /// <summary>The presenter applied or removed a translation: render the active body again.</summary>
    private async Task IntelligenceRerenderAsync()
    {
        var render = Task.CompletedTask;
        await Dispatcher.ExecuteOnUIThread(() =>
        {
            if (!IsReleased) render = RenderActiveContentAsync();
        });
        await render;
    }

    private void SyncIntelligenceHeader()
    {
        if (_intelligenceDisposed || _intelligence is not { } presenter || _intelligenceHeader is not { } header) return;
        _intelligenceSlot.SetVisible(presenter.IsVisible);
        header.Apply(new WinoIntelligenceHeaderState(
            presenter.ContentKey ?? string.Empty,
            presenter.ProcessingState,
            presenter.IsSummaryAvailable,
            presenter.IsTranslateAvailable,
            presenter.IsProcessingAvailable,
            presenter.BriefingFactText ?? string.Empty,
            presenter.DeadlineText ?? string.Empty,
            presenter.DeadlineDetailText ?? string.Empty,
            presenter.VerificationCode ?? string.Empty,
            presenter.IntelligenceTiles ?? [],
            presenter.SummaryText ?? string.Empty,
            presenter.SummaryState,
            presenter.TranslationLanguages,
            presenter.SelectedSourceLanguage ?? string.Empty,
            presenter.SelectedTargetLanguage ?? string.Empty,
            presenter.IsTranslationBusy,
            presenter.HasTranslationResult,
            presenter.IsTranslationApplied,
            presenter.TranslationStatusText ?? string.Empty));
    }

#if DEBUG
    internal WinoIntelligenceHeaderPresenter? DebugIntelligence => _intelligence;

    internal WinoIntelligenceHeader? DebugIntelligenceHeader => _intelligenceHeader;
#endif
}
