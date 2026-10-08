using System.ComponentModel;
using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.Controls.AppKit.Intelligence;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Intelligence;
using Wino.Messaging.Client.Shell;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// Wino Intelligence header of the reader (Windows MailRenderingPage "Wino Intelligence" region).
/// The shared <see cref="WinoIntelligenceHeaderPresenter"/> owns the intelligence context, the
/// snapshot and every request; this partial copies its state onto the native
/// <see cref="WinoIntelligenceHeaderView"/>, routes the view's actions back to the presenter, and
/// renders the translated body when the presenter asks for it.
/// </summary>
public sealed partial class MailRenderingPageViewController
{
    private WinoIntelligenceHeaderPresenter? _intelligence;
    private WinoIntelligenceHeaderView? _intelligenceHeader;
    private bool _intelligenceDisposed;

    private partial void BuildIntelligenceHeader(NSView host)
    {
        var presenter = _services.GetRequiredService<WinoIntelligenceHeaderPresenter>();
        var header = new WinoIntelligenceHeaderView();
        WinoLayout.Fill(header, host);
        _intelligence = presenter;
        _intelligenceHeader = header;

        header.ProcessRequested += (_, _) => Observe(presenter.RequestProcessingCommand.ExecuteAsync(null));
        header.CopyCodeRequested += (_, _) => Observe(presenter.CopyVerificationCodeCommand.ExecuteAsync(null));
        header.SummaryRequested += (_, _) => Observe(presenter.RequestSummaryCommand.ExecuteAsync(null));
        header.SummaryCancelRequested += (_, _) => presenter.CancelSummary();
        header.TranslateRequested += (_, _) => Observe(presenter.TranslateCommand.ExecuteAsync(null));
        header.TranslationCancelRequested += (_, _) => presenter.CancelTranslation();
        header.SourceLanguageChanged += (_, code) => presenter.SelectedSourceLanguage = code;
        header.TargetLanguageChanged += (_, code) => presenter.SelectedTargetLanguage = code;

        presenter.PropertyChanged += IntelligencePropertyChanged;
        presenter.RerenderRequested += IntelligenceRerenderAsync;
        WeakReferenceMessenger.Default.Register<MailRenderingPageViewController, LanguageChanged>(this, static (controller, _) => controller._intelligence?.OnLanguageChanged());

        SyncIntelligenceHeader();
#if DEBUG
        RegisterIntelligenceDebugCommands();
#endif
    }

    private partial Task<string> ResolveActiveHtmlAsync(string html)
        => Task.FromResult(_intelligence is { } presenter && !_intelligenceDisposed ? presenter.ResolveActiveHtml(html) : html);

    private partial void IntelligenceBeginItem(MailItemViewModel? item)
    {
        if (_intelligence is not { } presenter || _intelligenceDisposed) return;
        presenter.Attach();
        // Everything after this point belongs to the new message, like the Windows reader's re-entry
        // (BeginMailItem also clears the previous context).
        presenter.ResetContent();
        presenter.BeginMailItem(item);
    }

    private partial Task IntelligenceHtmlRendered(string html)
        => _intelligence is { } presenter && !_intelligenceDisposed ? LoadIntelligenceContextAsync(presenter, html) : Task.CompletedTask;

    private async Task LoadIntelligenceContextAsync(WinoIntelligenceHeaderPresenter presenter, string html)
    {
        try
        {
            await presenter.OnHtmlRendered(html, ViewModel.Subject, ViewModel.FromAddress, ViewModel.CreationDate);
        }
        catch (Exception exception)
        {
            // The intelligence context is optional; a failure never blocks the message body.
            ReportError(exception);
        }
    }

    private partial void IntelligenceContentCleared()
    {
        if (_intelligence is { } presenter && !_intelligenceDisposed) presenter.ResetContent();
    }

    private partial void IntelligenceDispose()
    {
        if (_intelligenceDisposed) return;
        _intelligenceDisposed = true;
        WeakReferenceMessenger.Default.Unregister<LanguageChanged>(this);
        if (_intelligence is { } presenter)
        {
            presenter.PropertyChanged -= IntelligencePropertyChanged;
            presenter.RerenderRequested -= IntelligenceRerenderAsync;
            presenter.Dispose();
        }
    }

    private void IntelligencePropertyChanged(object? sender, PropertyChangedEventArgs args)
        => _ = Dispatcher.ExecuteOnUIThread(SyncIntelligenceHeader);

    /// <summary>The presenter applied or removed a translation: render the active body again.</summary>
    private async Task IntelligenceRerenderAsync()
    {
        Task render = Task.CompletedTask;
        await Dispatcher.ExecuteOnUIThread(() => render = RenderActiveContentAsync());
        await render;
    }

    private void SyncIntelligenceHeader()
    {
        if (_intelligenceDisposed || _intelligence is not { } presenter || _intelligenceHeader is not { } header) return;
        _intelligenceHost.Hidden = !presenter.IsVisible;
        header.Update(new WinoIntelligenceHeaderState(
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
}
