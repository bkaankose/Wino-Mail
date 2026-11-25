#if DEBUG
namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-reader-intelligence</c> <c>as</c> on the current reader: <c>['state', path]</c> writes the presenter's values
/// and the header; <c>['process']</c>, <c>['summary']</c> and <c>['translate', code]</c> run the presenter's real
/// commands (without an entitlement they end in the shared unavailable state); <c>['expand']</c> opens the header body.
/// </summary>
internal static partial class ReaderDebugActions
{
    static partial void RegisterIntelligence(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-reader-intelligence", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) =>
        {
            try
            {
                var parts = args.Parameter?.GetStrv(out _) ?? [];
                var view = Current();
                var presenter = view.DebugIntelligence ?? throw new InvalidOperationException("The reader has no intelligence presenter.");
                var value = parts.Length > 1 ? parts[1] : string.Empty;
                switch (parts.Length > 0 ? parts[0] : string.Empty)
                {
                    case "state":
                        Write(value, $"visible={presenter.IsVisible} key='{presenter.ContentKey}' processing={presenter.ProcessingState} available={presenter.IsProcessingAvailable} "
                            + $"canRequest={presenter.CanRequestProcessing} summaryAvailable={presenter.IsSummaryAvailable} summary={presenter.SummaryState} translateAvailable={presenter.IsTranslateAvailable} "
                            + $"translation=busy:{presenter.IsTranslationBusy},applied:{presenter.IsTranslationApplied},status:'{presenter.TranslationStatusText}' "
                            + $"briefing='{presenter.BriefingFactText}' deadline='{presenter.DeadlineText}' code={(string.IsNullOrEmpty(presenter.VerificationCode) ? "none" : "yes")}\n"
                            + (view.DebugIntelligenceHeader?.Describe() ?? "no header"));
                        break;
                    case "process": Observe(presenter.RequestProcessingCommand.ExecuteAsync(null)); break;
                    case "summary": Observe(presenter.RequestSummaryCommand.ExecuteAsync(null)); break;
                    case "translate":
                        if (value.Length > 0) presenter.SelectedTargetLanguage = value;
                        Observe(presenter.TranslateCommand.ExecuteAsync(null));
                        break;
                    case "expand":
                        if (view.DebugIntelligenceHeader is { } header) header.IsExpanded = true;
                        break;
                    default: throw new ArgumentException($"Unknown debug-reader-intelligence operation '{string.Join(' ', parts)}'.");
                }
                Serilog.Log.Information("Debug action debug-reader-intelligence {Operation} done.", string.Join(' ', parts));
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-reader-intelligence failed."); }
        };
        application.AddAction(action);
    }
}
#endif
