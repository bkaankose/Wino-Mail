#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Api.Contracts.Voice;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Mail.Controls.Core.VoiceBubble;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels.Voice;

/// <summary>
/// The voice bubble flow on every platform: opens the bubble from the global shortcut or Settings, records the
/// microphone, streams it to the voice API, shows the live transcript, asks the plan's open questions, shows the
/// planned steps for confirmation and runs them. The platform supplies the window, shortcut and frame clock through
/// <see cref="IVoiceBubbleHost"/> and the steps that open app windows through <see cref="IVoicePlanHost"/>.
/// Everything except the audio callbacks runs on the host's UI thread.
/// </summary>
public sealed class VoiceBubbleController : IDisposable
{
    private const double SpeechLevel = 0.45;
    private const double QuietLevel = 0.3;
    private const double AutoSendQuietSeconds = 1.2;
    private const double NoSpeechTimeoutSeconds = 8;
    private const double MaxFrameSeconds = 0.05;
    // Stops just before the API's own cap (Voice:Limits:MaxAudioSeconds = 45), which commits the audio by itself.
    private const double MaxRecordingSeconds = 44.5;
    private const int SummaryDismissMilliseconds = 30000;
    private static readonly TimeSpan QuestionFilterDelay = TimeSpan.FromMilliseconds(150);

    private readonly IVoiceBubbleHost _host;
    private readonly IVoicePlanHost? _planHost;
    private readonly IPreferencesService _preferences;
    private readonly IWinoAccountIntelligenceSnapshotService _entitlementService;
    private readonly IMicrophoneService _microphone;
    private readonly IVoiceCommandService _voiceCommands;
    private readonly IVoicePlanPreviewService _planPreview;
    private readonly IVoicePlanExecutor _planExecutor;
    private readonly IVoiceBrainService _brain;
    private readonly IVoiceQuestionService _questions;
    private readonly IPictureStorageService _pictureStorage;
    private VoicePlan? _questionPlan;
    private List<VoiceQuestion>? _openQuestions;
    private readonly List<VoiceAnswerCandidate> _picked = [];
    private const string DoneChoiceKey = "done";
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
    private readonly VoiceBubbleLevelBands _bands = new();
    private readonly Lock _audioLock = new();
    private readonly List<ReadOnlyMemory<byte>> _pendingAudio = [];
    private bool _hasAccess;
    private CancellationTokenSource? _requestCancellation;
    private IVoiceCommandSession? _session;
    private IDisposable? _capture;
    private string _heard = string.Empty;

    // Typed text a voice request continues from (the user typed, then pressed the microphone), and the text of the
    // request being typed. The last mode and text let Try again repeat a typed request.
    private string _typedPrefix = string.Empty;
    private string _composed = string.Empty;
    private VoiceBubbleInputMode _lastInputMode = VoiceBubbleInputMode.Voice;

    // The spoken requests of this period are used up: voice opens as text, with a note, until a plan reports more.
    private bool _voiceExhausted;
    private double _micLevel;
    private bool _heardSpeech;
    private double _quietSeconds;
    private double _recordingSeconds;
    private bool _isRendering;
    private bool _disposed;

    public VoiceBubbleController(IVoiceBubbleHost host, IVoicePlanHost? planHost, IServiceProvider services)
    {
        _host = host;
        _planHost = planHost;
        _preferences = services.GetRequiredService<IPreferencesService>();
        _entitlementService = services.GetRequiredService<IWinoAccountIntelligenceSnapshotService>();
        _microphone = services.GetRequiredService<IMicrophoneService>();
        _voiceCommands = services.GetRequiredService<IVoiceCommandService>();
        _planPreview = services.GetRequiredService<IVoicePlanPreviewService>();
        _planExecutor = services.GetRequiredService<IVoicePlanExecutor>();
        _brain = services.GetRequiredService<IVoiceBrainService>();
        _questions = services.GetRequiredService<IVoiceQuestionService>();
        _pictureStorage = services.GetRequiredService<IPictureStorageService>();
        _hasAccess = _entitlementService.CurrentEntitlement.CanAccessSurfaces;

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
        bubble.ComposePlaceholder = Translator.VoiceBubble_ComposePlaceholder;
        bubble.ComposedTextChanged += BubbleComposedTextChanged;
        bubble.SwitchToVoiceRequested += BubbleSwitchToVoiceRequested;
        bubble.QuestionFilterChanged += BubbleQuestionFilterChanged;
        bubble.ChoiceSelected += BubbleChoiceSelected;
        bubble.SendRequested += BubbleSendRequested;
        bubble.CancelRequested += BubbleCancelRequested;
        bubble.ConfirmRequested += BubbleConfirmRequested;
        bubble.RetryRequested += BubbleRetryRequested;
        bubble.Dismissed += BubbleDismissed;
        bubble.ExitCompleted += BubbleExitCompleted;
        _host.HotKeyPressed += HostHotKeyPressed;
        _host.FrameRendered += HostFrameRendered;

        var messenger = WeakReferenceMessenger.Default;
        messenger.Register<VoiceBubbleController, PreferenceChangedMessage>(this, static (controller, message) => controller._host.Post(() => controller.PreferenceChanged(message.PropertyName)));
        messenger.Register<VoiceBubbleController, WinoIntelligenceEntitlementChanged>(this, static (controller, message) => controller._host.Post(() => controller.ApplyAccess(message.Entitlement.CanAccessSurfaces)));
        messenger.Register<VoiceBubbleController, VoiceBubbleTryRequested>(this, static (controller, _) => controller._host.Post(controller.TryOpen));

        ApplyHotKey();
        _ = RefreshAccessAsync();
    }

    private VoiceBubbleState State => _host.Bubble.State;

    private bool IsUsable => _hasAccess && _preferences.IsVoiceControlEnabled;

    /// <summary>
    /// Tries a shortcut before Settings saves it. The shortcut is registered only while voice control is on and the
    /// Wino Account has Wino Intelligence; otherwise a valid shortcut is accepted and stays unregistered.
    /// </summary>
    public bool TryConfigureHotKey(bool enabled, HotKeyGesture gesture) => TryConfigureHotKey(VoiceBubbleInputMode.Voice, enabled, gesture);

    /// <summary>
    /// Tries the shortcut that opens the bubble in <paramref name="mode"/>. The voice and text shortcuts must differ.
    /// </summary>
    public bool TryConfigureHotKey(VoiceBubbleInputMode mode, bool enabled, HotKeyGesture gesture)
    {
        var normalized = gesture.Normalize();
        if (_disposed || !_host.CanRegisterHotKey(normalized) || normalized == OtherGesture(mode).Normalize())
            return false;

        return _host.TrySetHotKey(mode, enabled && IsUsable ? gesture : null);
    }

    /// <summary>Opens the bubble for speaking or typing; voice opens for typing when the spoken requests are used up.</summary>
    public void Open(VoiceBubbleInputMode mode)
    {
        if (_disposed || !IsUsable)
            return;

        if (mode == VoiceBubbleInputMode.Text)
            StartComposing(string.Empty, null);
        else if (_voiceExhausted)
            StartComposing(string.Empty, Translator.VoiceBubble_VoiceQuotaUsed);
        else
            StartListening();
    }

    private HotKeyGesture OtherGesture(VoiceBubbleInputMode mode) => mode == VoiceBubbleInputMode.Voice
        ? new HotKeyGesture(_preferences.VoiceTextHotKeyKey, _preferences.VoiceTextHotKeyModifiers)
        : new HotKeyGesture(_preferences.VoiceHotKeyKey, _preferences.VoiceHotKeyModifiers);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _host.HotKeyPressed -= HostHotKeyPressed;
        _host.FrameRendered -= HostFrameRendered;
        var bubble = _host.Bubble;
        bubble.SendRequested -= BubbleSendRequested;
        bubble.CancelRequested -= BubbleCancelRequested;
        bubble.ConfirmRequested -= BubbleConfirmRequested;
        bubble.RetryRequested -= BubbleRetryRequested;
        bubble.Dismissed -= BubbleDismissed;
        bubble.ExitCompleted -= BubbleExitCompleted;
        bubble.QuestionFilterChanged -= BubbleQuestionFilterChanged;
        bubble.ChoiceSelected -= BubbleChoiceSelected;
        bubble.ComposedTextChanged -= BubbleComposedTextChanged;
        bubble.SwitchToVoiceRequested -= BubbleSwitchToVoiceRequested;
        EndRequest();
    }

    private void ApplyHotKey()
    {
        if (_disposed)
            return;

        RegisterHotKey(VoiceBubbleInputMode.Voice, _preferences.IsVoiceHotKeyEnabled, new HotKeyGesture(_preferences.VoiceHotKeyKey, _preferences.VoiceHotKeyModifiers));
        RegisterHotKey(VoiceBubbleInputMode.Text, _preferences.IsVoiceTextHotKeyEnabled, new HotKeyGesture(_preferences.VoiceTextHotKeyKey, _preferences.VoiceTextHotKeyModifiers));
    }

    private void RegisterHotKey(VoiceBubbleInputMode mode, bool enabled, HotKeyGesture gesture)
    {
        // The text shortcut yields when it equals the voice one.
        var clashes = mode == VoiceBubbleInputMode.Text && gesture.Normalize() == OtherGesture(mode).Normalize();
        var shouldRegister = IsUsable && enabled && !clashes;
        if (!_host.TrySetHotKey(mode, shouldRegister ? gesture : null))
            Log.Warning("Could not register {Modifiers}+{Key} for the assistant ({Mode}). Another application may own it.", gesture.Modifiers, gesture.Key, mode);
    }

    private async Task RefreshAccessAsync()
    {
        try
        {
            var entitlement = await _entitlementService.GetEntitlementAsync().ConfigureAwait(false);
            _host.Post(() => ApplyAccess(entitlement.CanAccessSurfaces));
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
            case nameof(IPreferencesService.IsVoiceTextHotKeyEnabled):
            case nameof(IPreferencesService.VoiceTextHotKeyKey):
            case nameof(IPreferencesService.VoiceTextHotKeyModifiers):
                ApplyHotKey();
                break;
        }
    }

    private void TryOpen()
    {
        if (_disposed || !IsUsable)
            return;

        if (State == VoiceBubbleState.Hidden)
            Open(VoiceBubbleInputMode.Voice);
    }

    private void HostHotKeyPressed(object? sender, VoiceBubbleInputMode mode)
    {
        if (_disposed || !IsUsable)
            return;

        switch (State)
        {
            case VoiceBubbleState.Hidden:
                Open(mode);
                break;
            case VoiceBubbleState.Listening when mode == VoiceBubbleInputMode.Voice:
                Send();
                break;
            case VoiceBubbleState.Composing when mode == VoiceBubbleInputMode.Voice:
                SwitchToVoice();
                break;
            case VoiceBubbleState.Composing when !string.IsNullOrWhiteSpace(_composed):
                Send();
                break;
            default:
                Close();
                break;
        }
    }

    /// <summary>Shows the bubble for typing, keeping <paramref name="text"/> (typed or heard so far).</summary>
    private void StartComposing(string text, string? notice)
    {
        EndRequest();

        _lastInputMode = VoiceBubbleInputMode.Text;
        _composed = text;
        var bubble = _host.Bubble;
        bubble.InputMode = VoiceBubbleInputMode.Text;
        bubble.Transcript = null;
        bubble.InterimTranscript = null;
        bubble.ComposedText = text;
        bubble.InputNotice = notice;
        bubble.CanSwitchToVoice = !_voiceExhausted;
        bubble.CanRetry = true;
        if (State == VoiceBubbleState.Hidden)
            _host.Show(_preferences.VoiceBubblePosition);
        Present(VoiceBubbleState.Composing);
    }

    /// <summary>The microphone button: the typed text stays, and what the user says is appended to it.</summary>
    private void SwitchToVoice()
    {
        if (State != VoiceBubbleState.Composing)
            return;

        if (_voiceExhausted)
        {
            _host.Bubble.InputNotice = Translator.VoiceBubble_VoiceQuotaUsed;
            return;
        }

        StartListening(_composed.Trim());
    }

    /// <summary>Sends the typed request: no microphone, no transcription.</summary>
    private void SendTyped()
    {
        var text = _composed.Trim();
        if (text.Length == 0)
        {
            Close();
            return;
        }

        EndRequest();
        _lastInputMode = VoiceBubbleInputMode.Text;
        var bubble = _host.Bubble;
        bubble.InputNotice = null;
        bubble.Transcript = text;
        Present(VoiceBubbleState.Analyzing);

        var cancellation = new CancellationTokenSource();
        _requestCancellation = cancellation;
        _ = RunTypedRequestAsync(text, cancellation);
    }

    private async Task RunTypedRequestAsync(string text, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            _brain.NotifyVoiceStarting();
            var start = await _voiceCommands.StartAsync(VoiceInputKind.Text, null, token);
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
                _session = session;

            session.SendText(text);
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
            Log.Error(ex, "The typed assistant request failed.");
            if (!IsStale(cancellation))
                Fail(GetErrorMessage(VoiceClientErrorCodes.ConnectionLost), canRetry: true);
        }
    }

    private void StartListening(string typedPrefix = "")
    {
        EndRequest();

        _lastInputMode = VoiceBubbleInputMode.Voice;
        _typedPrefix = typedPrefix;
        _heard = string.Empty;
        _micLevel = 0;
        _heardSpeech = false;
        _quietSeconds = 0;
        _recordingSeconds = 0;
        _bands.Reset();

        var bubble = _host.Bubble;
        bubble.InputMode = VoiceBubbleInputMode.Voice;
        bubble.InputNotice = null;
        bubble.Transcript = _typedPrefix.Length > 0 ? _typedPrefix : null;
        bubble.InterimTranscript = null;
        bubble.CanRetry = true;
        if (State == VoiceBubbleState.Hidden)
            _host.Show(_preferences.VoiceBubblePosition);
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
            var sessionTask = _voiceCommands.StartAsync(VoiceInputKind.Audio, _typedPrefix, token);
            _brain.NotifyVoiceStarting();

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
                bubble.Transcript = JoinTyped(_typedPrefix, _heard);
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
                if (frame.Metrics?.VoiceRemaining is int voiceRemaining)
                    _voiceExhausted = voiceRemaining <= 0;

                await ShowPlanAsync(plan, frame.Metrics?.Mode ?? _brain.CurrentMode, cancellation);
                break;
            case VoiceFrameTypes.Error:
                if (frame.Code == VoiceErrorCodes.Superseded)
                {
                    Close();
                }
                else if (frame.Code == VoiceErrorCodes.VoiceQuotaExceeded)
                {
                    // The request can still be typed: keep what was typed and heard, and switch to typing.
                    _voiceExhausted = true;
                    StartComposing(JoinTyped(_typedPrefix, _heard), Translator.VoiceBubble_VoiceQuotaUsed);
                }
                else
                {
                    Fail(GetErrorMessage(frame.Code), CanRetry(frame.Code));
                }
                break;
        }
    }

    /// <summary>
    /// Stops recording and tells the API the user finished speaking. Without any transcribed words there is nothing to
    /// plan, so the bubble just closes.
    /// </summary>
    private void Send()
    {
        if (State == VoiceBubbleState.Composing)
        {
            SendTyped();
            return;
        }

        if (State != VoiceBubbleState.Listening)
            return;

        if (_session is null || string.IsNullOrWhiteSpace(_heard))
        {
            // Nothing was said after typing: back to the typed text instead of losing it.
            if (_typedPrefix.Length > 0)
                StartComposing(_typedPrefix, null);
            else
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

        // Names the plan leaves to the device (Private mode, and names Personal mode's brain doesn't know) become
        // people or questions here; Personal mode then answers what the device can tell on its own.
        plan = await _questions.AddLocalQuestionsAsync(plan, cancellation.Token);
        if (IsStale(cancellation))
            return;

        if (mode == VoiceMode.Personal)
        {
            var known = await _questions.AnswerKnownQuestionsAsync(plan, cancellation.Token);
            if (IsStale(cancellation))
                return;

            plan = known.Plan;
            foreach (var fact in known.Facts)
                _ = Task.Run(() => _brain.AddFactAsync(fact));
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
        _picked.Clear();
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
                if (!question.AllowsMany)
                    return ToChoices(candidates);

                // A group: the people already picked leave the list, and "Done" on top finishes once there is one.
                var picked = _picked.ToList();
                var (choices, byKey) = ToChoices(candidates.Where(candidate => !picked.Exists(p => p.Key == candidate.Key)).ToList());
                if (picked.Count > 0)
                {
                    choices.Insert(0, new VoiceBubbleChoice(DoneChoiceKey,
                        string.Format(CultureInfo.CurrentCulture, Translator.VoiceQuestion_GroupDone, picked.Count),
                        string.Join(", ", picked.Select(static p => p.Title)), null));
                }

                return (choices, byKey);
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
    private (List<VoiceBubbleChoice> Choices, Dictionary<string, VoiceAnswerCandidate> ByKey) ToChoices(IReadOnlyList<VoiceAnswerCandidate> candidates)
    {
        var choices = new List<VoiceBubbleChoice>(candidates.Count + 4);
        var byKey = new Dictionary<string, VoiceAnswerCandidate>(StringComparer.Ordinal);
        string? group = null;

        foreach (var candidate in candidates)
        {
            if (!byKey.TryAdd(candidate.Key, candidate))
                continue;

            var icon = candidate.Account is { } account ? ToIcon(account) : null;
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
        if (_questionPlan is not { } plan || _openQuestions is not { } questions || _questionIndex >= questions.Count || _candidates is null)
            return;

        var question = questions[_questionIndex];
        VoiceAnswerCandidate? answer;
        if (question.AllowsMany)
        {
            if (choice.Key != DoneChoiceKey)
            {
                if (_candidates.TryGetValue(choice.Key, out var member) && _picked.Count < BrainFact.MaxGroupMembers)
                    _picked.Add(member);

                _host.Bubble.ClearQuestionFilter();
                _ = LoadChoicesAsync(question, null, TimeSpan.Zero);
                return;
            }

            if (_picked.Count == 0)
                return;

            answer = VoiceAnswerCandidate.Group(_picked.ToList());
        }
        else if (!_candidates.TryGetValue(choice.Key, out answer))
        {
            return;
        }

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
        _picked.Clear();
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

    private VoiceBubbleStepGroup ToBubbleGroup(VoicePlanPreviewGroup group)
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
                    string.Format(CultureInfo.CurrentCulture, Translator.VoiceBubble_AllAccounts_Count, group.Candidates.Count),
                    ToIcons(group.Candidates),
                    steps,
                    notices);
            case VoicePlanAccountKind.Unresolved:
                var subtitle = group.Candidates.Count > 1
                    ? string.Format(CultureInfo.CurrentCulture, Translator.VoiceBubble_UnresolvedAccount_Candidates, string.Join(", ", group.Candidates.Select(GetAccountTitle)))
                    : string.IsNullOrWhiteSpace(group.SpokenAccount)
                        ? Translator.VoiceBubble_UnresolvedAccount_Unnamed
                        : string.Format(CultureInfo.CurrentCulture, Translator.VoiceBubble_UnresolvedAccount_Said, group.SpokenAccount);
                return new VoiceBubbleStepGroup(VoiceBubbleStepGroupKind.Unresolved, Translator.VoiceBubble_UnresolvedAccount, subtitle, Array.Empty<IAccountIconInfo>(), steps, notices);
            default:
                return new VoiceBubbleStepGroup(VoiceBubbleStepGroupKind.None, null, null, Array.Empty<IAccountIconInfo>(), steps, notices);
        }
    }

    private static string GetAccountTitle(MailAccount account)
        => !string.IsNullOrWhiteSpace(account.Name) ? account.Name
            : !string.IsNullOrWhiteSpace(account.SenderName) ? account.SenderName
            : account.Address ?? string.Empty;

    private IAccountIconInfo ToIcon(MailAccount account) => MailAccountIconInfoFactory.Create(account, _pictureStorage);

    private IAccountIconInfo[] ToIcons(IEnumerable<MailAccount> accounts) => accounts.Select(ToIcon).ToArray();

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

    private void Present(VoiceBubbleState state) => _host.Bubble.Present(state);

    private void BubbleSendRequested(object? sender, EventArgs e) => Send();

    private void BubbleCancelRequested(object? sender, EventArgs e) => Close();

    private void BubbleConfirmRequested(object? sender, EventArgs e) => Confirm();

    private void BubbleRetryRequested(object? sender, EventArgs e)
    {
        if (_lastInputMode == VoiceBubbleInputMode.Text)
            StartComposing(_composed, null);
        else
            StartListening();
    }

    private void BubbleComposedTextChanged(object? sender, string text) => _composed = text ?? string.Empty;

    private void BubbleSwitchToVoiceRequested(object? sender, EventArgs e) => SwitchToVoice();

    private static string JoinTyped(string typed, string heard)
    {
        var spoken = heard.Trim();
        return typed.Length == 0 ? spoken : spoken.Length == 0 ? typed : typed + " " + spoken;
    }

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

        _isRendering = true;
        _host.StartFrames();
    }

    private void StopRendering()
    {
        if (!_isRendering)
            return;

        _isRendering = false;
        _host.StopFrames();
    }

    private void HostFrameRendered(object? sender, double elapsedSeconds)
    {
        if (!_isRendering || State != VoiceBubbleState.Listening)
            return;

        var delta = Math.Clamp(elapsedSeconds, 0, MaxFrameSeconds);
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
        {
            // Nothing was said after switching from typing: keep the typed request instead of closing it.
            if (_typedPrefix.Length > 0)
                StartComposing(_typedPrefix, null);
            else
                Close();
        }
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
}
