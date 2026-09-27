#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Ai;
using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// Rewrite for the draft being composed. The draft is replaced on screen, and the original is kept
/// so the user can switch between the two, regenerate, or keep the result.
/// </summary>
/// <remarks>
/// A tone is always applied to the original, never to an earlier rewrite, so trying several tones
/// does not drift. Before switching versions the one on screen is read back from the editor, so
/// edits made to either version survive the switch. Keep ends the session with whatever is shown.
/// </remarks>
public sealed partial class ComposerRewriteSession : ObservableObject
{
    private readonly IWinoIntelligenceCoordinator? _coordinator;
    private readonly Func<Task<string?>> _readDraftHtml;
    private readonly Func<string, Task> _renderDraftHtml;
    private readonly Func<Guid?> _accountId;
    private readonly Action<string> _reportError;

    private Guid? _requestId;
    private int _generation;
    private string? _originalHtml;
    private string? _rewrittenHtml;
    private string? _lastMode;

    public ComposerRewriteSession(
        IWinoIntelligenceCoordinator? coordinator,
        Func<Task<string?>> readDraftHtml,
        Func<string, Task> renderDraftHtml,
        Func<Guid?> accountId,
        Action<string> reportError)
    {
        _coordinator = coordinator;
        _readDraftHtml = readDraftHtml;
        _renderDraftHtml = renderDraftHtml;
        _accountId = accountId;
        _reportError = reportError;
    }

    /// <summary>The composer offers every mode the API accepts.</summary>
    public IReadOnlyList<AiRewriteModeOption> Modes { get; } = AiActionCatalog.GetRewriteModeOptions();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelVisible))]
    public partial bool IsAvailable { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelVisible), nameof(IsResultVisible))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanelVisible), nameof(IsResultVisible))]
    public partial bool HasResult { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleText))]
    public partial bool IsShowingRewrite { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>The status strip under the subject is shown while a rewrite runs or can be undone.</summary>
    public bool IsPanelVisible => IsAvailable && (IsBusy || HasResult);

    public bool IsResultVisible => HasResult && !IsBusy;

    public string ToggleText => IsShowingRewrite ? Translator.WinoIntelligence_ShowOriginal : Translator.Composer_AiRewriteShowRewrite;

    /// <summary>Re-evaluates eligibility with the same gate the reader uses.</summary>
    public async Task RefreshAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        var accountId = _accountId();
        var available = _coordinator is not null && accountId is { } id &&
                        await _coordinator.IsDraftRewriteAvailableAsync(id, cancellationToken);
        IsAvailable = available;
        if (!available)
            Reset();
    }

    [RelayCommand]
    private Task RewriteAsync(string? mode) => RunAsync(mode);

    [RelayCommand]
    private Task RegenerateAsync() => RunAsync(_lastMode);

    [RelayCommand]
    private async Task ToggleOriginalAsync()
    {
        if (!HasResult || IsBusy || _originalHtml is null || _rewrittenHtml is null)
            return;

        var generation = _generation;
        var current = await _readDraftHtml();
        if (generation != _generation)
            return;

        if (IsShowingRewrite)
        {
            _rewrittenHtml = current ?? _rewrittenHtml;
            await _renderDraftHtml(_originalHtml);
            IsShowingRewrite = false;
        }
        else
        {
            _originalHtml = current ?? _originalHtml;
            await _renderDraftHtml(_rewrittenHtml);
            IsShowingRewrite = true;
        }
    }

    /// <summary>Keeps whatever is on screen and ends the session.</summary>
    [RelayCommand]
    private void Keep() => Reset();

    [RelayCommand]
    private void Cancel()
    {
        if (_requestId is not { } requestId)
            return;

        _coordinator?.CancelRequest(requestId);
        _requestId = null;
        IsBusy = false;
        RestoreStatus();
    }

    /// <summary>Drops every snapshot and any request in flight, for example when the draft changes.</summary>
    public void Reset()
    {
        _generation++;
        if (_requestId is { } requestId)
            _coordinator?.CancelRequest(requestId);
        _requestId = null;
        _originalHtml = null;
        _rewrittenHtml = null;
        _lastMode = null;
        IsBusy = false;
        HasResult = false;
        IsShowingRewrite = false;
        StatusText = string.Empty;
    }

    private async Task RunAsync(string? mode)
    {
        if (_coordinator is null || !IsAvailable || IsBusy || string.IsNullOrWhiteSpace(mode) || _accountId() is not { } accountId)
            return;

        var generation = _generation;

        // Read the draft first: it is the original on the first run, and when the original is on
        // screen it may have been edited since.
        if (!HasResult || !IsShowingRewrite)
        {
            var current = await _readDraftHtml();
            if (generation != _generation)
                return;
            if (!HasResult || !IsShowingRewrite)
                _originalHtml = current ?? string.Empty;
        }

        var source = _originalHtml ?? string.Empty;
        var requestId = Guid.NewGuid();
        _requestId = requestId;
        IsBusy = true;
        StatusText = Translator.WinoIntelligence_Rewriting;

        WinoIntelligenceOperationResult<string> result;
        try
        {
            result = await _coordinator.RewriteDraftAsync(accountId, requestId, source, mode);
        }
        finally
        {
            if (_requestId == requestId)
            {
                _requestId = null;
                IsBusy = false;
            }
        }

        if (generation != _generation || result.IsCanceled)
            return;

        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Value))
        {
            RestoreStatus();
            if (!HasResult)
                _originalHtml = null;
            _reportError(result.Error ?? Translator.WinoIntelligence_ActionFailed);
            return;
        }

        await _renderDraftHtml(result.Value);
        if (generation != _generation)
            return;

        // Keep what the editor made of the result, so a later switch compares like with like.
        _rewrittenHtml = await _readDraftHtml() ?? result.Value;
        _lastMode = mode;
        HasResult = true;
        IsShowingRewrite = true;
        RestoreStatus();
    }

    private void RestoreStatus()
    {
        var label = Modes.FirstOrDefault(x => string.Equals(x.Mode, _lastMode, StringComparison.OrdinalIgnoreCase))?.Label;
        StatusText = _lastMode is null
            ? string.Empty
            : string.Format(Translator.WinoIntelligence_RewriteAppliedFormat, label ?? _lastMode);
    }
}
