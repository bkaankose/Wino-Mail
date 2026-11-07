using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Serilog;
using Windows.Graphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Api.Contracts.Voice;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Mail.Controls.Core.VoiceBubble;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;
using Wino.Mail.WinUI.Extensions;
using Wino.Mail.WinUI.Interfaces;

namespace Wino.Mail.WinUI.Services.Voice;

/// <summary>
/// Opens the voice bubble from the global shortcut or Settings, records the microphone, streams it to the voice API
/// and shows the live transcript and the planned steps. The plan is only shown: Yes and No close the bubble until the
/// app can run plans.
/// </summary>
internal sealed class VoiceBubbleService : IDisposable
{
    private const double SpeechLevel = 0.45;
    private const double QuietLevel = 0.3;
    private const double AutoSendQuietSeconds = 1.2;
    private const double NoSpeechTimeoutSeconds = 8;
    // Stops just before the API's own cap (Voice:Limits:MaxAudioSeconds = 45), which commits the audio by itself.
    private const double MaxRecordingSeconds = 44.5;
    private const int SummaryDismissMilliseconds = 30000;
    private static readonly TimeSpan QuestionFilterDelay = TimeSpan.FromMilliseconds(150);

    private readonly DispatcherQueue _dispatcher;
    private readonly INativeAppService _nativeAppService;
    private readonly INewThemeService _themeService;
    private readonly IUnderlyingThemeService _underlyingThemeService;
    private readonly Func<RectInt32?> _getAnchor;
    private readonly IPreferencesService _preferences;
    private readonly IWinoAccountIntelligenceSnapshotService _entitlementService;
    private readonly IMicrophoneService _microphone;
    private readonly IVoiceCommandService _voiceCommands;
    private readonly IVoicePlanPreviewService _planPreview;
    private readonly IVoicePlanExecutor _planExecutor;
    private readonly IVoicePlanHost? _planHost;
    private readonly IVoiceBrainService _brain;
    private readonly IVoiceQuestionService _questions;
    private VoicePlan? _questionPlan;
    private List<VoiceQuestion>? _openQuestions;
    private int _questionIndex;
    private bool _isPersonalPlan;
    private Dictionary<string, VoiceAnswerCandidate>? _candidates;
    private CancellationTokenSource? _candidateCancellation;
    private readonly int _defaultSuccessDismissMilliseconds;
    private VoicePlanResolution? _resolution;
    private double _planConfidence;
    private List<VoicePlanPreviewGroup>? _shownGroups;
    private VoiceBubbleStepGroup[]? _bubbleGroups;
    private IReadOnlyCollection<VoicePlanPreviewGroup>? _riskAcceptedGroups;
    private List<string>? _pendingNotes;
    private readonly VoiceBubbleHost _host;
    private readonly VoiceBubbleLevelBands _bands = new();
    private readonly Lock _audioLock = new();
    private readonly List<ReadOnlyMemory<byte>> _pendingAudio = [];
    private bool _hasAccess;
    private CancellationTokenSource? _requestCancellation;
    private IVoiceCommandSession? _session;
    private IDisposable? _capture;
    private string _heard = string.Empty;
    private double _micLevel;
    private bool _heardSpeech;
    private double _quietSeconds;
    private double _recordingSeconds;
    private TimeSpan _lastRenderTime;
    private bool _isRendering;
    private bool _disposed;

    internal VoiceBubbleService(IServiceProvider services, DispatcherQueue dispatcher, INativeAppService nativeAppService, Func<RectInt32?> getAnchor)
    {
        _dispatcher = dispatcher;
        _nativeAppService = nativeAppService;
        _getAnchor = getAnchor;
        _themeService = services.GetRequiredService<INewThemeService>();
        _underlyingThemeService = services.GetRequiredService<IUnderlyingThemeService>();
        _preferences = services.GetRequiredService<IPreferencesService>();
        _entitlementService = services.GetRequiredService<IWinoAccountIntelligenceSnapshotService>();
        _microphone = services.GetRequiredService<IMicrophoneService>();
        _voiceCommands = services.GetRequiredService<IVoiceCommandService>();
        _planPreview = services.GetRequiredService<IVoicePlanPreviewService>();
        _planExecutor = services.GetRequiredService<IVoicePlanExecutor>();
        _brain = services.GetRequiredService<IVoiceBrainService>();
        _questions = services.GetRequiredService<IVoiceQuestionService>();
        _planHost = WinoApplication.Current is App app ? new VoicePlanHost(app) : null;
        _hasAccess = _entitlementService.CurrentEntitlement.CanAccessSurfaces;

        _host = new VoiceBubbleHost();
        var bubble = _host.Bubble;
        bubble.ShowsTranscript = true;
        _defaultSuccessDismissMilliseconds = bubble.SuccessDismissMilliseconds;
        bubble.ListeningText = Translator.VoiceBubble_Listening;
        bubble.ConfirmText = Translator.Buttons_Yes;
        bubble.DeclineText = Translator.Buttons_No;
        bubble.RetryText = Translator.Buttons_TryAgain;
        bubble.ConfirmSelectedText = Translator.VoiceBubble_ConfirmSelected;
        bubble.NoticeTitle = Translator.VoiceBubble_CantDo;
        bubble.QuestionSearchPlaceholder = Translator.VoiceQuestion_Search;
        bubble.QuestionEmptyText = Translator.VoiceQuestion_NoMatches;
        bubble.QuestionCancelText = Translator.Buttons_Cancel;
        bubble.QuestionFilterChanged += BubbleQuestionFilterChanged;
        bubble.ChoiceSelected += BubbleChoiceSelected;
        bubble.SendRequested += BubbleSendRequested;
        bubble.CancelRequested += BubbleCancelRequested;
        bubble.ConfirmRequested += BubbleConfirmRequested;
        bubble.RetryRequested += BubbleRetryRequested;
        bubble.Dismissed += BubbleDismissed;
        bubble.ExitCompleted += BubbleExitCompleted;
        _host.HotKeyPressed += HostHotKeyPressed;

        _themeService.ElementThemeChanged += ThemeServiceElementThemeChanged;
        ApplyTheme(_themeService.RootTheme);

        var messenger = WeakReferenceMessenger.Default;
        messenger.Register<VoiceBubbleService, PreferenceChangedMessage>(this, static (service, message) => service.OnUIThread(() => service.PreferenceChanged(message.PropertyName)));
        messenger.Register<VoiceBubbleService, WinoIntelligenceEntitlementChanged>(this, static (service, message) => service.OnUIThread(() => service.ApplyAccess(message.Entitlement.CanAccessSurfaces)));
        messenger.Register<VoiceBubbleService, VoiceBubbleTryRequested>(this, static (service, _) => service.OnUIThread(service.TryOpen));

        ApplyHotKey();
        _ = RefreshAccessAsync();
    }

    private VoiceBubbleState State => _host.Bubble.State;

    private bool IsUsable => _hasAccess && _preferences.IsVoiceControlEnabled;

    /// <summary>
    /// Tries a shortcut before Settings saves it. The shortcut is registered only while voice control is on and the
    /// Wino Account has Wino Intelligence; otherwise a valid shortcut is accepted and stays unregistered.
    /// </summary>
    internal bool TryConfigureHotKey(bool enabled, HotKeyGesture gesture)
    {
        if (_disposed || !VoiceBubbleHost.IsRegistrable(gesture.Normalize(), out _))
            return false;

        return _host.TrySetHotKey(enabled && IsUsable ? gesture : null);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _themeService.ElementThemeChanged -= ThemeServiceElementThemeChanged;
        _host.HotKeyPressed -= HostHotKeyPressed;
        var bubble = _host.Bubble;
        bubble.SendRequested -= BubbleSendRequested;
        bubble.CancelRequested -= BubbleCancelRequested;
        bubble.ConfirmRequested -= BubbleConfirmRequested;
        bubble.RetryRequested -= BubbleRetryRequested;
        bubble.Dismissed -= BubbleDismissed;
        bubble.ExitCompleted -= BubbleExitCompleted;
        bubble.QuestionFilterChanged -= BubbleQuestionFilterChanged;
        bubble.ChoiceSelected -= BubbleChoiceSelected;
        EndRequest();
        _host.Dispose();
    }

    private void ApplyHotKey()
    {
        if (_disposed)
            return;

        var gesture = new HotKeyGesture(_preferences.VoiceHotKeyKey, _preferences.VoiceHotKeyModifiers);
        var shouldRegister = IsUsable && _preferences.IsVoiceHotKeyEnabled;
        if (!_host.TrySetHotKey(shouldRegister ? gesture : null))
            Log.Warning("Could not register {Modifiers}+{Key} for the voice bubble. Another application may own it.", gesture.Modifiers, gesture.Key);
    }

    private async Task RefreshAccessAsync()
    {
        try
        {
            var entitlement = await _entitlementService.GetEntitlementAsync().ConfigureAwait(false);
            OnUIThread(() => ApplyAccess(entitlement.CanAccessSurfaces));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the Wino Intelligence entitlement for voice control.");
        }
    }

    private void ApplyAccess(bool hasAccess)
    {
        if (_disposed || _hasAccess == hasAccess)
            return;

        _hasAccess = hasAccess;
        if (!IsUsable)
            Close();

        ApplyHotKey();
    }

    private void PreferenceChanged(string propertyName)
    {
        if (_disposed)
            return;

        switch (propertyName)
        {
            case nameof(IPreferencesService.IsVoiceControlEnabled):
                if (!IsUsable)
                    Close();
                ApplyHotKey();
                break;
            case nameof(IPreferencesService.IsVoiceHotKeyEnabled):
            case nameof(IPreferencesService.VoiceHotKeyKey):
            case nameof(IPreferencesService.VoiceHotKeyModifiers):
                ApplyHotKey();
                break;
        }
    }

    private void TryOpen()
    {
        if (_disposed || !IsUsable)
            return;

        if (State == VoiceBubbleState.Hidden)
            StartListening();
    }

    private void HostHotKeyPressed(object? sender, EventArgs e)
    {
        if (_disposed || !IsUsable)
            return;

        switch (State)
        {
            case VoiceBubbleState.Hidden:
                StartListening();
                break;
            case VoiceBubbleState.Listening:
                Send();
                break;
            default:
                Close();
                break;
        }
    }

    private void StartListening()
    {
        EndRequest();

        _heard = string.Empty;
        _micLevel = 0;
        _heardSpeech = false;
        _quietSeconds = 0;
        _recordingSeconds = 0;
        _bands.Reset();

        var bubble = _host.Bubble;
        bubble.Transcript = null;
        bubble.InterimTranscript = null;
        bubble.CanRetry = true;
        _host.Show(_getAnchor(), _nativeAppService.GetTaskbarPosition(), _preferences.VoiceBubblePosition);
        Present(VoiceBubbleState.Listening);
        StartRendering();

        var cancellation = new CancellationTokenSource();
        _requestCancellation = cancellation;
        _ = RunRequestAsync(cancellation);
    }

    /// <summary>
    /// Opens the microphone and the socket together so the first words are not lost: audio recorded before the socket
    /// exists is held and sent when it does. Runs on the UI thread between awaits.
    /// </summary>
    private async Task RunRequestAsync(CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            var captureTask = _microphone.StartCaptureAsync(
                _preferences.VoiceMicrophoneDeviceId,
                VoiceAudioFormat.SampleRate,
                OnAudioAvailable,
                level => Volatile.Write(ref _micLevel, level),
                token);
            var sessionTask = _voiceCommands.StartAsync(token);

            var capture = await captureTask;
            if (IsStale(cancellation))
            {
                capture.Session?.Dispose();
                await DisposeSessionWhenStartedAsync(sessionTask);
                return;
            }

            _capture = capture.Session;
            if (capture.Access != MicrophoneAccess.Allowed)
            {
                await DisposeSessionWhenStartedAsync(sessionTask);
                Fail(GetMicrophoneMessage(capture.Access), canRetry: capture.Access != MicrophoneAccess.Denied);
                return;
            }

            var start = await sessionTask;
            if (IsStale(cancellation))
            {
                if (start.Session is { } staleSession)
                    await staleSession.DisposeAsync();
                return;
            }

            if (start.Session is not { } session)
            {
                Fail(GetErrorMessage(start.ErrorCode), CanRetry(start.ErrorCode));
                return;
            }

            lock (_audioLock)
            {
                _session = session;
                foreach (var chunk in _pendingAudio)
                    session.SendAudio(chunk);
                _pendingAudio.Clear();
            }

            await foreach (var frame in session.Frames.ReadAllAsync(token))
            {
                if (IsStale(cancellation))
                    return;

                await HandleFrameAsync(frame, cancellation);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "The voice request failed.");
            if (!IsStale(cancellation))
                Fail(GetErrorMessage(VoiceClientErrorCodes.ConnectionLost), canRetry: true);
        }
    }

    private bool IsStale(CancellationTokenSource cancellation)
        => _disposed || cancellation.IsCancellationRequested || !ReferenceEquals(cancellation, _requestCancellation);

    private static async Task DisposeSessionWhenStartedAsync(Task<VoiceCommandStartResult> sessionTask)
    {
        try
        {
            if ((await sessionTask).Session is { } session)
                await session.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not close an unused voice session.");
        }
    }

    private void OnAudioAvailable(ReadOnlyMemory<byte> chunk)
    {
        lock (_audioLock)
        {
            if (_session is { } session)
                session.SendAudio(chunk);
            else
                _pendingAudio.Add(chunk);
        }
    }

    private async Task HandleFrameAsync(VoiceServerFrame frame, CancellationTokenSource cancellation)
    {
        var bubble = _host.Bubble;
        switch (frame.Type)
        {
            case VoiceFrameTypes.TranscriptDelta when State == VoiceBubbleState.Listening:
                _heard += frame.Text;
                _heardSpeech |= !string.IsNullOrWhiteSpace(frame.Text);
                bubble.Transcript = _heard.Trim();
                break;
            case VoiceFrameTypes.TranscriptFinal:
                if (!string.IsNullOrWhiteSpace(frame.Text))
                    bubble.Transcript = frame.Text.Trim();
                break;
            case VoiceFrameTypes.Status:
                if (State == VoiceBubbleState.Listening)
                    Send();
                break;
            case VoiceFrameTypes.Plan when frame.Plan is { } plan:
                if (frame.Metrics?.Mode is { } mode)
                    _brain.ApplyReportedMode(mode);

                await ShowPlanAsync(plan, frame.Metrics?.Mode ?? _brain.CurrentMode, cancellation);
                break;
            case VoiceFrameTypes.Error:
                if (frame.Code == VoiceErrorCodes.Superseded)
                    Close();
                else
                    Fail(GetErrorMessage(frame.Code), CanRetry(frame.Code));
                break;
        }
    }

    /// <summary>
    /// Stops recording and tells the API the user finished speaking. Without any transcribed words there is nothing to
    /// plan, so the bubble just closes.
    /// </summary>
    private void Send()
    {
        if (State != VoiceBubbleState.Listening)
            return;

        if (_session is null || string.IsNullOrWhiteSpace(_heard))
        {
            Close();
            return;
        }

        StopRendering();
        StopCapture();
        _session.Commit();

        _host.Bubble.InterimTranscript = null;
        Present(VoiceBubbleState.Analyzing);
    }

    private async Task ShowPlanAsync(VoicePlan plan, VoiceMode mode, CancellationTokenSource cancellation)
    {
        StopRendering();
        StopCapture();
        var bubble = _host.Bubble;
        if (!string.IsNullOrWhiteSpace(plan.Transcript))
            bubble.Transcript = plan.Transcript;

        var commands = plan.Commands ?? [];
        if (VoicePlan.IsCanceledByUser(plan) || commands.Count == 0)
        {
            Close();
            return;
        }

        if (mode != VoiceMode.Personal)
        {
            plan = await _questions.AddLocalQuestionsAsync(plan, cancellation.Token);
            if (IsStale(cancellation))
                return;
        }

        // Every open question is asked before anything runs, auto-confirm included.
        var questions = _questions.GetQuestions(plan);
        if (questions.Count > 0)
        {
            _questionPlan = plan;
            _openQuestions = questions.ToList();
            _questionIndex = 0;
            _isPersonalPlan = mode == VoiceMode.Personal;
            AskQuestion();
            return;
        }

        await ResolveAndShowPlanAsync(plan, cancellation);
    }

    private void AskQuestion()
    {
        if (_openQuestions is not { } questions || _questionIndex >= questions.Count)
            return;

        var question = questions[_questionIndex];
        var bubble = _host.Bubble;
        bubble.QuestionText = question.Text;
        bubble.IsQuestionSearchable = question.IsSearchable;
        bubble.QuestionCaption = _isPersonalPlan ? Translator.VoiceQuestion_RememberCaption : null;
        bubble.ClearQuestionFilter();
        _candidates = null;
        bubble.SetChoices([]);
        Present(VoiceBubbleState.Question);
        _ = LoadChoicesAsync(question, null, TimeSpan.Zero);
    }

    private void BubbleQuestionFilterChanged(object? sender, string filter)
    {
        if (_openQuestions is { } questions && _questionIndex < questions.Count)
            _ = LoadChoicesAsync(questions[_questionIndex], filter, QuestionFilterDelay);
    }

    /// <summary>
    /// Loads a question's choices off the UI thread. A newer filter cancels the load in flight, so only the latest
    /// result reaches the list, in one batch.
    /// </summary>
    private async Task LoadChoicesAsync(VoiceQuestion question, string? filter, TimeSpan delay)
    {
        _candidateCancellation?.Cancel();
        _candidateCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _candidateCancellation = cancellation;
        var token = cancellation.Token;

        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, token);

            var (choices, byKey) = await Task.Run(async () =>
            {
                var candidates = await _questions.GetCandidatesAsync(question, filter, token).ConfigureAwait(false);
                return ToChoices(candidates);
            }, token);

            if (token.IsCancellationRequested || State != VoiceBubbleState.Question)
                return;

            _candidates = byKey;
            _host.Bubble.SetChoices(choices);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load the choices for a voice question.");
        }
    }

    /// <summary>Choices in the order given, with an account header above each run of choices from the same account.</summary>
    private static (List<VoiceBubbleChoice> Choices, Dictionary<string, VoiceAnswerCandidate> ByKey) ToChoices(IReadOnlyList<VoiceAnswerCandidate> candidates)
    {
        var choices = new List<VoiceBubbleChoice>(candidates.Count + 4);
        var byKey = new Dictionary<string, VoiceAnswerCandidate>(StringComparer.Ordinal);
        string? group = null;

        foreach (var candidate in candidates)
        {
            if (!byKey.TryAdd(candidate.Key, candidate))
                continue;

            var icon = candidate.Account is { } account ? Wino.Helpers.XamlHelpers.GetAccountIconInfo(account) : null;
            if (!string.IsNullOrWhiteSpace(candidate.GroupTitle))
            {
                if (!string.Equals(group, candidate.GroupTitle, StringComparison.Ordinal))
                {
                    group = candidate.GroupTitle;
                    choices.Add(new VoiceBubbleChoice("h:" + candidate.Key, candidate.GroupTitle, null, icon, isHeader: true));
                }

                choices.Add(new VoiceBubbleChoice(candidate.Key, candidate.Title, candidate.Detail, null));
                continue;
            }

            choices.Add(new VoiceBubbleChoice(candidate.Key, candidate.Title, candidate.Detail, icon));
        }

        return (choices, byKey);
    }

    /// <summary>
    /// Puts the answer into the plan, saves it as a learned fact in Personal mode (silently; a failure only logs), and
    /// asks the next question or resolves the plan.
    /// </summary>
    private void BubbleChoiceSelected(object? sender, VoiceBubbleChoice choice)
    {
        if (_questionPlan is not { } plan || _openQuestions is not { } questions || _questionIndex >= questions.Count
            || _candidates is null || !_candidates.TryGetValue(choice.Key, out var answer))
            return;

        var question = questions[_questionIndex];
        plan = _questions.ApplyAnswer(plan, question, answer);
        _questionPlan = plan;

        if (_isPersonalPlan && _questions.CreateFact(question, answer) is { } fact)
            _ = Task.Run(() => _brain.AddFactAsync(fact));

        _questionIndex++;
        if (_questionIndex < questions.Count)
        {
            AskQuestion();
            return;
        }

        EndQuestions();
        if (_requestCancellation is not { } cancellation)
        {
            Close();
            return;
        }

        Present(VoiceBubbleState.Analyzing);
        _ = ResolveAfterQuestionsAsync(plan, cancellation);
    }

    private async Task ResolveAfterQuestionsAsync(VoicePlan plan, CancellationTokenSource cancellation)
    {
        try
        {
            await ResolveAndShowPlanAsync(plan, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Resolving a voice plan after its questions failed.");
            if (!IsStale(cancellation))
                Fail(Translator.VoiceBubble_Error_Unavailable, canRetry: true);
        }
    }

    private void EndQuestions()
    {
        _candidateCancellation?.Cancel();
        _candidateCancellation?.Dispose();
        _candidateCancellation = null;
        _questionPlan = null;
        _openQuestions = null;
        _candidates = null;
        _questionIndex = 0;
    }

    private async Task ResolveAndShowPlanAsync(VoicePlan plan, CancellationTokenSource cancellation)
    {
        var bubble = _host.Bubble;

        var resolution = await _planExecutor.ResolveAsync(await _planPreview.CreateAsync(plan), cancellation.Token);
        if (IsStale(cancellation))
            return;

        var preview = resolution.Preview;

        // Nothing can run (every part is a banner, for example an account that didn't match): there is nothing to confirm.
        if (preview.Groups.All(static group => group.Steps.Count == 0))
        {
            var issue = preview.Groups.SelectMany(static group => group.Issues).FirstOrDefault();
            Fail(issue?.Message ?? Translator.VoiceBubble_Error_NotUnderstood, canRetry: true);
            return;
        }

        _resolution = resolution;
        _planConfidence = plan.Confidence;
        _shownGroups = preview.Groups.ToList();
        _bubbleGroups = preview.Groups.Select(ToBubbleGroup).ToArray();
        _riskAcceptedGroups = null;

        _pendingNotes = null;

        if (_preferences.IsVoiceAutoConfirmEnabled)
        {
            AutoConfirm(resolution, preview);
            return;
        }

        bubble.ConfirmText = Translator.Buttons_Yes;
        bubble.DeclineText = Translator.Buttons_No;
        bubble.ConfirmationText = null;
        bubble.ConfirmationSteps = null;
        bubble.ConfirmationGroups = _bubbleGroups;
        Present(VoiceBubbleState.Confirmation);
    }

    /// <summary>
    /// Runs every account's steps without Yes. Risky steps still get the second confirmation, and the parts Wino
    /// can't do are listed with the result, since the user never saw the plan.
    /// </summary>
    private void AutoConfirm(VoicePlanResolution resolution, VoicePlanPreview preview)
    {
        var accepted = preview.Groups.Where(static group => group.Steps.Count > 0).ToList();
        _pendingNotes = preview.Groups
            .SelectMany(static group => group.Issues)
            .Select(static issue => $"**{Translator.VoiceBubble_CantDo}** {issue.Message}")
            .ToList();

        var risks = _planExecutor.GetRisks(resolution, accepted);
        if (risks.Count == 0)
        {
            _ = RunPlanAsync(resolution, accepted);
            return;
        }

        ShowRisks(accepted, risks);
    }

    /// <summary>
    /// Yes or Confirm: runs the accepted accounts' steps, after a second confirmation when a step is risky (a large or
    /// permanent delete, emptying a folder, a large archive). Cancelling the second confirmation drops the whole plan.
    /// </summary>
    private void Confirm()
    {
        if (_resolution is not { } resolution || _shownGroups is null || _bubbleGroups is null)
        {
            Close();
            return;
        }

        if (_riskAcceptedGroups is { } confirmed)
        {
            _riskAcceptedGroups = null;
            _ = RunPlanAsync(resolution, confirmed);
            return;
        }

        var accepted = _bubbleGroups
            .Select((group, index) => (Group: group, Index: index))
            .Where(static entry => entry.Group.CanSelect && entry.Group.IsSelected)
            .Select(entry => _shownGroups[entry.Index])
            .ToList();
        if (accepted.Count == 0)
        {
            Close();
            return;
        }

        var risks = _planExecutor.GetRisks(resolution, accepted);
        if (risks.Count == 0)
        {
            _ = RunPlanAsync(resolution, accepted);
            return;
        }

        ShowRisks(accepted, risks);
    }

    private void ShowRisks(IReadOnlyCollection<VoicePlanPreviewGroup> accepted, IReadOnlyList<VoicePlanRisk> risks)
    {
        _riskAcceptedGroups = accepted;
        var bubble = _host.Bubble;
        var steps = risks.Select(static risk => new VoiceBubbleStep(VoiceBubbleText.FromMarked(risk.MarkedText))).ToArray();
        bubble.ConfirmationGroups = new[] { new VoiceBubbleStepGroup(VoiceBubbleStepGroupKind.None, null, null, Array.Empty<IAccountIconInfo>(), steps) };
        bubble.ConfirmText = risks.Count == 1 ? risks[0].ButtonText : Translator.VoiceGuard_Continue;
        bubble.DeclineText = Translator.Buttons_Cancel;
        Present(VoiceBubbleState.Confirmation);
    }

    private async Task RunPlanAsync(VoicePlanResolution resolution, IReadOnlyCollection<VoicePlanPreviewGroup> accepted)
    {
        var cancellation = _requestCancellation;
        if (cancellation is null || _planHost is null)
        {
            Fail(Translator.VoiceBubble_Error_Unavailable, canRetry: false);
            return;
        }

        Present(VoiceBubbleState.Analyzing);

        VoicePlanExecutionResult result;
        try
        {
            result = await _planExecutor.ExecuteAsync(resolution, accepted, _planConfidence, _planHost, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Running a voice plan failed.");
            if (!IsStale(cancellation))
                Fail(Translator.VoiceBubble_Error_Unavailable, canRetry: false);
            return;
        }

        if (IsStale(cancellation))
            return;

        _resolution = null;
        var notes = _pendingNotes ?? [];
        _pendingNotes = null;
        var lines = result.Completed.Concat(result.Failed).Concat(notes).ToList();
        if (result.Failed.Count > 0)
        {
            Fail(string.Join("\n", lines), canRetry: false);
            return;
        }

        var bubble = _host.Bubble;
        var message = string.Join("\n", result.Completed.Concat(notes));
        if (!string.IsNullOrWhiteSpace(result.Summary))
            message = string.IsNullOrWhiteSpace(message) ? result.Summary : message + "\n\n" + result.Summary;

        // A summary is read, not glanced at, so it stays until the user closes it or a long timeout passes.
        bubble.SuccessDismissMilliseconds = string.IsNullOrWhiteSpace(result.Summary) ? _defaultSuccessDismissMilliseconds : SummaryDismissMilliseconds;
        bubble.Message = VoiceBubbleText.FromMarked(message);
        Present(VoiceBubbleState.Success);
        EndSession();
    }

    private static VoiceBubbleStepGroup ToBubbleGroup(VoicePlanPreviewGroup group)
    {
        var steps = group.Steps
            .Select(static step => new VoiceBubbleStep(VoiceBubbleText.FromMarked(step.MarkedText), step.Detail, step.Number))
            .ToArray();
        var notices = group.Issues.Select(static issue => issue.Message).ToArray();

        switch (group.Kind)
        {
            case VoicePlanAccountKind.Account when group.Account is { } account:
                return new VoiceBubbleStepGroup(
                    VoiceBubbleStepGroupKind.Account,
                    GetAccountTitle(account),
                    string.Equals(GetAccountTitle(account), account.Address, StringComparison.OrdinalIgnoreCase) ? null : account.Address,
                    ToIcons(new[] { account }),
                    steps,
                    notices);
            case VoicePlanAccountKind.AllAccounts:
                return new VoiceBubbleStepGroup(
                    VoiceBubbleStepGroupKind.Accounts,
                    group.Candidates.Count <= 3 ? string.Join(", ", group.Candidates.Select(GetAccountTitle)) : Translator.VoiceBubble_AllAccounts,
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, Translator.VoiceBubble_AllAccounts_Count, group.Candidates.Count),
                    ToIcons(group.Candidates),
                    steps,
                    notices);
            case VoicePlanAccountKind.Unresolved:
                var subtitle = group.Candidates.Count > 1
                    ? string.Format(System.Globalization.CultureInfo.CurrentCulture, Translator.VoiceBubble_UnresolvedAccount_Candidates, string.Join(", ", group.Candidates.Select(GetAccountTitle)))
                    : string.IsNullOrWhiteSpace(group.SpokenAccount)
                        ? Translator.VoiceBubble_UnresolvedAccount_Unnamed
                        : string.Format(System.Globalization.CultureInfo.CurrentCulture, Translator.VoiceBubble_UnresolvedAccount_Said, group.SpokenAccount);
                return new VoiceBubbleStepGroup(VoiceBubbleStepGroupKind.Unresolved, Translator.VoiceBubble_UnresolvedAccount, subtitle, Array.Empty<IAccountIconInfo>(), steps, notices);
            default:
                return new VoiceBubbleStepGroup(VoiceBubbleStepGroupKind.None, null, null, Array.Empty<IAccountIconInfo>(), steps, notices);
        }
    }

    private static string GetAccountTitle(MailAccount account)
        => !string.IsNullOrWhiteSpace(account.Name) ? account.Name
            : !string.IsNullOrWhiteSpace(account.SenderName) ? account.SenderName
            : account.Address ?? string.Empty;

    private static IAccountIconInfo[] ToIcons(IEnumerable<MailAccount> accounts)
        => accounts
            .Select(static account => Wino.Helpers.XamlHelpers.GetAccountIconInfo(account))
            .OfType<IAccountIconInfo>()
            .ToArray();

    private void Fail(string message, bool canRetry)
    {
        StopRendering();
        StopCapture();
        var bubble = _host.Bubble;
        bubble.Message = VoiceBubbleText.FromMarked(message);
        bubble.CanRetry = canRetry;
        Present(VoiceBubbleState.Failed);
        EndSession();
    }

    private void Close()
    {
        EndRequest();
        if (State == VoiceBubbleState.Hidden)
            _host.Hide();
        else
            Present(VoiceBubbleState.Hidden);
    }

    /// <summary>Cancels the request in flight, closes the microphone and the socket.</summary>
    private void EndRequest()
    {
        EndQuestions();
        _resolution = null;
        _riskAcceptedGroups = null;
        _pendingNotes = null;

        var cancellation = _requestCancellation;
        _requestCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();

        StopRendering();
        StopCapture();
        EndSession();
    }

    private void EndSession()
    {
        IVoiceCommandSession? session;
        lock (_audioLock)
        {
            session = _session;
            _session = null;
            _pendingAudio.Clear();
        }

        if (session is not null)
            _ = DisposeQuietlyAsync(session);
    }

    private static async Task DisposeQuietlyAsync(IVoiceCommandSession session)
    {
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not close the voice session.");
        }
    }

    private void StopCapture()
    {
        var capture = _capture;
        _capture = null;

        try
        {
            // Disposing delivers the last partial chunk, so it still reaches the session before the commit.
            capture?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not close the microphone.");
        }
    }

    private void Present(VoiceBubbleState state)
    {
        var bubble = _host.Bubble;
        if (bubble.State == state)
            bubble.Refresh();
        else
            bubble.State = state;
    }

    private void BubbleSendRequested(object? sender, EventArgs e) => Send();

    private void BubbleCancelRequested(object? sender, EventArgs e) => Close();

    private void BubbleConfirmRequested(object? sender, EventArgs e) => Confirm();

    private void BubbleRetryRequested(object? sender, EventArgs e) => StartListening();

    private void BubbleDismissed(object? sender, EventArgs e) => EndRequest();

    private void BubbleExitCompleted(object? sender, EventArgs e)
    {
        if (State == VoiceBubbleState.Hidden)
            _host.Hide();
    }

    private void StartRendering()
    {
        if (_isRendering)
            return;

        _lastRenderTime = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
        _isRendering = true;
    }

    private void StopRendering()
    {
        if (!_isRendering)
            return;

        CompositionTarget.Rendering -= OnRendering;
        _isRendering = false;
    }

    [WinRT.DynamicWindowsRuntimeCast(typeof(RenderingEventArgs))]
    private void OnRendering(object? sender, object e)
    {
        var now = e is RenderingEventArgs rendering ? rendering.RenderingTime : _lastRenderTime;
        var delta = _lastRenderTime == TimeSpan.Zero ? 0 : Math.Clamp((now - _lastRenderTime).TotalSeconds, 0, 0.05);
        _lastRenderTime = now;

        if (State != VoiceBubbleState.Listening)
            return;

        var level = Volatile.Read(ref _micLevel);
        _bands.Step(level, delta);
        _host.Bubble.UpdateBands(_bands.Bands);

        _recordingSeconds += delta;
        if (level >= SpeechLevel)
            _heardSpeech = true;
        _quietSeconds = level <= QuietLevel ? _quietSeconds + delta : 0;

        var hasWords = !string.IsNullOrWhiteSpace(_heard);
        if ((hasWords && _quietSeconds >= AutoSendQuietSeconds) || _recordingSeconds >= MaxRecordingSeconds)
            Send();
        else if (!_heardSpeech && _recordingSeconds >= NoSpeechTimeoutSeconds)
            Close();
    }

    private static bool CanRetry(string? code) => code is not (
        WinoAccountClientErrorCodes.SignInRequired or
        ApiErrorCodes.AiPackRequired or
        ApiErrorCodes.IntelligenceConsentRequired or
        ApiErrorCodes.IntelligenceConsentVersionOutdated or
        ApiErrorCodes.AiQuotaExceeded or
        VoiceErrorCodes.QuotaExceeded or
        VoiceClientErrorCodes.AccessDenied);

    private static string GetErrorMessage(string? code) => code switch
    {
        WinoAccountClientErrorCodes.SignInRequired or ApiErrorCodes.SessionInvalid => Translator.VoiceBubble_Error_SignIn,
        ApiErrorCodes.AiPackRequired => Translator.VoiceBubble_Error_IntelligenceRequired,
        ApiErrorCodes.IntelligenceConsentRequired or ApiErrorCodes.IntelligenceConsentVersionOutdated => Translator.VoiceBubble_Error_Consent,
        ApiErrorCodes.AiQuotaExceeded or VoiceErrorCodes.QuotaExceeded => Translator.VoiceBubble_Error_Quota,
        VoiceClientErrorCodes.AccessDenied => Translator.VoiceBubble_Error_AccessDenied,
        ApiErrorCodes.RateLimited => Translator.VoiceBubble_Error_RateLimited,
        VoiceErrorCodes.TranscriptionFailed => Translator.VoiceBubble_Error_NotHeard,
        VoiceErrorCodes.PlannerFailed => Translator.VoiceBubble_Error_PlanFailed,
        VoiceErrorCodes.Timeout => Translator.VoiceBubble_Error_Timeout,
        _ => Translator.VoiceBubble_Error_Unavailable
    };

    private static string GetMicrophoneMessage(MicrophoneAccess access) => access switch
    {
        MicrophoneAccess.Denied => Translator.VoiceControlSettings_Microphone_AccessDenied,
        MicrophoneAccess.NoDevice => Translator.VoiceControlSettings_Microphone_NoDevice,
        _ => Translator.VoiceControlSettings_Microphone_Unavailable
    };

    private void OnUIThread(Action action)
    {
        if (_dispatcher.HasThreadAccess)
            action();
        else
            _dispatcher.TryEnqueue(() => action());
    }

    private void ThemeServiceElementThemeChanged(object? sender, ApplicationElementTheme theme)
        => OnUIThread(() => ApplyTheme(theme));

    private void ApplyTheme(ApplicationElementTheme theme)
    {
        if (_disposed)
            return;

        _host.ApplyTheme(theme == ApplicationElementTheme.Default
            ? _underlyingThemeService.IsUnderlyingThemeDark() ? ElementTheme.Dark : ElementTheme.Light
            : theme.ToWindowsElementTheme());
    }
}
