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
using Wino.Agent.Runtime.Brain;
using Wino.Agent.Runtime.Clarify;
using Wino.Agent.Runtime.Plan;
using Wino.Agent.Runtime.Session;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.Voice;
using Wino.Core.Services.Assistant;
using Wino.Mail.Api.Contracts.Assistant;
using Wino.Mail.Api.Contracts.Common;
using Wino.Services.Assistant;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Mail.Controls.Core.VoiceBubble;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels.Voice;

/// <summary>
/// The assistant bubble flow on every platform: opens the bubble from the global shortcut or Settings, records the
/// microphone or takes typed text, and hands the request to the Wino Agent Runtime: the server reads the words, the
/// runtime resolves them on the device, asks its questions here, and returns a plan that runs after confirmation
/// through <see cref="AgentPlanExecutor"/>. The platform supplies the window, shortcut and frame clock through
/// <see cref="IVoiceBubbleHost"/> and the steps that open app windows through <see cref="IAgentPlanHost"/>.
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
    private readonly IPreferencesService _preferences;
    private readonly IWinoAccountIntelligenceSnapshotService _entitlementService;
    private readonly IMicrophoneService _microphone;
    private readonly IAssistantCommandService _commands;
    private readonly IAgentRuntimeHost _runtime;
    private readonly AgentPlanExecutor _executor;
    private readonly AssistantReadingClient _reader;
    private readonly IPictureStorageService _pictureStorage;
    private readonly IAccountService _accountService;
    private readonly ICalendarService _calendarService;
    private const string DoneChoiceKey = "done";
    private const string NewAddressPrefix = "new:";
    private readonly int _defaultSuccessDismissMilliseconds;

    // The runtime session lives as long as the controller: follow-ups ("archive them too") refer to earlier turns.
    private IAgentSession? _agentSession;

    // The question on screen, its options and the people picked so far for a many-person question.
    private Clarification? _clarification;
    private readonly List<ClarificationOption> _picked = [];
    private CancellationTokenSource? _candidateCancellation;
    private string? _questionFilter;

    // The plan on screen: its steps grouped by account, and the second confirmation for steps that can't be undone.
    private ResolvedPlan? _plan;
    private List<List<int>>? _groupSteps;
    private VoiceBubbleStepGroup[]? _bubbleGroups;
    private List<int>? _riskAcceptedSteps;
    private AgentFollowUp? _followUp;
    private string _requestText = string.Empty;
    private TaskCompletionSource<bool>? _proposalAnswer;

    private readonly VoiceBubbleLevelBands _bands = new();
    private readonly Lock _audioLock = new();
    private readonly List<ReadOnlyMemory<byte>> _pendingAudio = [];
    private bool _hasAccess;
    private CancellationTokenSource? _requestCancellation;
    private IAssistantCommandSession? _session;
    private IDisposable? _capture;
    private string _heard = string.Empty;

    // The items a context-aware request was opened for. They stay on the device; the server only gets their kind,
    // count and flags.
    private VoiceLocalSelection? _selection;
    private AgentSelection? _agentSelection;

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

    public VoiceBubbleController(IVoiceBubbleHost host, IAgentPlanHost? planHost, IServiceProvider services)
    {
        _host = host;
        _preferences = services.GetRequiredService<IPreferencesService>();
        _entitlementService = services.GetRequiredService<IWinoAccountIntelligenceSnapshotService>();
        _microphone = services.GetRequiredService<IMicrophoneService>();
        _commands = services.GetRequiredService<IAssistantCommandService>();
        _runtime = services.GetRequiredService<IAgentRuntimeHost>();
        _executor = services.GetRequiredService<AgentPlanExecutor>();
        _reader = services.GetRequiredService<AssistantReadingClient>();
        _pictureStorage = services.GetRequiredService<IPictureStorageService>();
        _accountService = services.GetRequiredService<IAccountService>();
        _calendarService = services.GetRequiredService<ICalendarService>();
        if (planHost is not null)
            _executor.Host = planHost;
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
        _runtime.Prompter.Handler = ProposeAsync;
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
        messenger.Register<VoiceBubbleController, VoiceSelectionRequested>(this, static (controller, message) => controller._host.Post(() => controller.OpenForSelection(message.Selection)));

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

        ClearSelection();

        if (mode == VoiceBubbleInputMode.Text)
            StartComposing(string.Empty, null);
        else if (_voiceExhausted)
            StartComposing(string.Empty, Translator.VoiceBubble_VoiceQuotaUsed);
        else
            StartListening();

        if (_voiceExhausted)
            _ = RecheckVoiceAllowanceAsync();
    }

    /// <summary>
    /// "Used up" is learned from a spoken request and typed requests never report it, so it would otherwise last until
    /// the app restarts: a new period, a purchase or a raised limit gives the microphone back once usage says so.
    /// </summary>
    private async Task RecheckVoiceAllowanceAsync()
    {
        try
        {
            var refreshed = await _entitlementService.RefreshAsync().ConfigureAwait(false);
            if (refreshed?.Snapshot.Usage?.Find(IntelligenceUsage.VoiceBucket) is not { IsExhausted: false })
                return;

            _host.Post(() =>
            {
                if (_disposed || !_voiceExhausted)
                    return;

                _voiceExhausted = false;
                var bubble = _host.Bubble;
                bubble.CanSwitchToVoice = true;
                if (bubble.InputNotice == Translator.VoiceBubble_VoiceQuotaUsed)
                    bubble.InputNotice = null;
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not recheck the spoken request allowance.");
        }
    }

    /// <summary>
    /// Opens the bubble for typing about <paramref name="selection"/>: the request carries a description of the items
    /// (kind, count, flags, hashed people) and the plan's commands on the selection act on these items.
    /// </summary>
    public void OpenForSelection(VoiceLocalSelection selection)
    {
        if (_disposed || !IsUsable || selection.Count == 0)
            return;

        StartComposing(string.Empty, null);
        _selection = selection;
        _agentSelection = null;
        _host.Bubble.ComposePlaceholder = SelectionPlaceholder(selection);
    }

    private static string SelectionPlaceholder(VoiceLocalSelection selection)
    {
        var count = selection.Count;
        return selection.Kind switch
        {
            AssistantSelectionKind.Mail => count == 1 ? Translator.VoiceBubble_AskAbout_Mail : string.Format(CultureInfo.CurrentCulture, Translator.VoiceBubble_AskAbout_Mails, count),
            AssistantSelectionKind.Event => count == 1 ? Translator.VoiceBubble_AskAbout_Event : string.Format(CultureInfo.CurrentCulture, Translator.VoiceBubble_AskAbout_Events, count),
            AssistantSelectionKind.Task => count == 1 ? Translator.VoiceBubble_AskAbout_Task : string.Format(CultureInfo.CurrentCulture, Translator.VoiceBubble_AskAbout_Tasks, count),
            AssistantSelectionKind.Contact => count == 1 ? Translator.VoiceBubble_AskAbout_Contact : string.Format(CultureInfo.CurrentCulture, Translator.VoiceBubble_AskAbout_Contacts, count),
            _ => Translator.VoiceBubble_AskAbout_Folder
        };
    }

    private void ClearSelection()
    {
        if (_selection is null)
            return;

        _selection = null;
        _agentSelection = null;
        _host.Bubble.ComposePlaceholder = Translator.VoiceBubble_ComposePlaceholder;
    }

    /// <summary>The selection as the runtime's ids and flags, built once per selection.</summary>
    private async Task<AgentSelection?> GetAgentSelectionAsync(CancellationToken cancellationToken)
    {
        if (_selection is not { } selection)
            return null;

        if (_agentSelection is null)
        {
            try
            {
                _agentSelection = await AgentSelectionBuilder.BuildAsync(selection, _accountService, _calendarService, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "Could not describe the selection for the assistant.");
                return null;
            }
        }

        return _agentSelection;
    }

    private async Task<IAgentSession> GetSessionAsync(CancellationToken cancellationToken)
    {
        await _runtime.EnsureInitializedAsync(cancellationToken);
        return _agentSession ??= _runtime.Runtime.OpenSession();
    }

    private HotKeyGesture OtherGesture(VoiceBubbleInputMode mode) => mode == VoiceBubbleInputMode.Voice
        ? new HotKeyGesture(_preferences.VoiceTextHotKeyKey, _preferences.VoiceTextHotKeyModifiers)
        : new HotKeyGesture(_preferences.VoiceHotKeyKey, _preferences.VoiceHotKeyModifiers);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_runtime.Prompter.Handler == ProposeAsync)
            _runtime.Prompter.Handler = null;
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

        // With the spoken requests used up the voice shortcut opens typing, so it also closes or sends like the typing
        // shortcut instead of "switching to voice", which only repeats the quota notice.
        if (_voiceExhausted)
            mode = VoiceBubbleInputMode.Text;

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

#if DEBUG
    /// <summary>Debug bridge: types <paramref name="text"/> into the bubble and sends it as the user would, about
    /// <paramref name="selection"/> when given (the mail list's "ask about these").</summary>
    public void DebugSendTyped(string text, VoiceLocalSelection? selection)
    {
        if (selection is not null) OpenForSelection(selection);
        else Open(VoiceBubbleInputMode.Text);
        _composed = text;
        _host.Bubble.ComposedText = text;
        SendTyped();
    }

    /// <summary>Debug bridge: the plan's Run button.</summary>
    public void DebugConfirm() => Confirm();
#endif

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
            var session = await GetSessionAsync(token);
            var selection = await GetAgentSelectionAsync(token);
            if (IsStale(cancellation))
                return;

            _requestText = text;
            var turn = await session.HandleAsync(new AgentRequest(text, AgentInputKind.Typed, selection, selection?.AccountId), token);
            if (_reader.LastMetrics?.VoiceRemaining is int voiceRemaining)
                _voiceExhausted = voiceRemaining <= 0;

            await ShowTurnAsync(turn, cancellation);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (AssistantReadException ex)
        {
            if (!IsStale(cancellation))
                Fail(GetErrorMessage(ex.Code), CanRetry(ex.Code));
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
    /// exists is held and sent when it does. The socket returns the transcript and the reading; the reading goes to the
    /// runtime as a pre-read request. Runs on the UI thread between awaits.
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
            var agentSession = await GetSessionAsync(token);
            var selection = await GetAgentSelectionAsync(token);
            var readRequest = agentSession.CreateReadRequest(new AgentRequest(_typedPrefix, AgentInputKind.Spoken, selection, selection?.AccountId));
            var sessionTask = _commands.StartAsync(readRequest, token);

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

                await HandleFrameAsync(frame, agentSession, selection, cancellation);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (AssistantReadException ex)
        {
            if (!IsStale(cancellation))
                Fail(GetErrorMessage(ex.Code), CanRetry(ex.Code));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "The spoken assistant request failed.");
            if (!IsStale(cancellation))
                Fail(GetErrorMessage(VoiceClientErrorCodes.ConnectionLost), canRetry: true);
        }
    }

    private bool IsStale(CancellationTokenSource cancellation)
        => _disposed || cancellation.IsCancellationRequested || !ReferenceEquals(cancellation, _requestCancellation);

    private static async Task DisposeSessionWhenStartedAsync(Task<AssistantCommandStartResult> sessionTask)
    {
        try
        {
            if ((await sessionTask).Session is { } session)
                await session.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not close an unused assistant session.");
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

    private async Task HandleFrameAsync(AssistantServerFrame frame, IAgentSession agentSession, AgentSelection? selection, CancellationTokenSource cancellation)
    {
        var bubble = _host.Bubble;
        switch (frame.Type)
        {
            case AssistantFrameTypes.TranscriptDelta when State == VoiceBubbleState.Listening:
                _heard += frame.Text;
                _heardSpeech |= !string.IsNullOrWhiteSpace(frame.Text);
                bubble.Transcript = JoinTyped(_typedPrefix, _heard);
                break;
            case AssistantFrameTypes.TranscriptFinal:
                if (!string.IsNullOrWhiteSpace(frame.Text))
                    bubble.Transcript = frame.Text.Trim();
                break;
            case AssistantFrameTypes.Status:
                if (State == VoiceBubbleState.Listening)
                    Send();
                break;
            case AssistantFrameTypes.Reading when frame.Reading is { } reading:
                StopRendering();
                StopCapture();
                if (frame.Metrics?.VoiceRemaining is int voiceRemaining)
                    _voiceExhausted = voiceRemaining <= 0;

                // The session's own socket is done; the runtime resolves the reading on the device.
                EndSession();
                var text = bubble.Transcript ?? JoinTyped(_typedPrefix, _heard);
                _requestText = text;
                var turn = await agentSession.HandleAsync(new AgentRequest(text, AgentInputKind.Spoken, selection, selection?.AccountId, reading), cancellation.Token);
                await ShowTurnAsync(turn, cancellation);
                break;
            case AssistantFrameTypes.Error:
                if (frame.Code == AssistantErrorCodes.Superseded)
                {
                    Close();
                }
                else if (frame.Code == AssistantErrorCodes.VoiceQuotaExceeded)
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
    /// read, so the bubble just closes.
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

    /// <summary>What the runtime returned: a question, a plan to confirm, a message, or nothing.</summary>
    private async Task ShowTurnAsync(AgentTurn turn, CancellationTokenSource cancellation)
    {
        if (IsStale(cancellation))
            return;

        switch (turn)
        {
            case AgentTurn.Clarify clarify:
                AskQuestion(clarify.Clarification);
                break;
            case AgentTurn.Plan plan:
                await ShowPlanAsync(plan.Resolved, cancellation);
                break;
            case AgentTurn.Message message:
                ShowMessage(message);
                break;
            default:
                Close();
                break;
        }
    }

    private void ShowMessage(AgentTurn.Message message)
    {
        var text = message.Kind switch
        {
            AgentMessageKind.Remembered => Translator.AssistantMessage_Remembered,
            AgentMessageKind.Forgotten => Translator.AssistantMessage_Forgotten,
            AgentMessageKind.ProposalSaved => Translator.AssistantMessage_ProposalSaved,
            AgentMessageKind.StandingRuleRefused => Translator.AssistantMessage_StandingRuleRefused,
            AgentMessageKind.OutOfScope => Translator.AssistantMessage_OutOfScope,
            AgentMessageKind.NeedsPreviousRequest => Translator.AssistantMessage_NeedsPreviousRequest,
            AgentMessageKind.NothingMatched => Translator.AssistantMessage_NothingMatched,
            AgentMessageKind.Undone => Translator.AssistantMessage_Undone,
            AgentMessageKind.Answer => Translator.AssistantMessage_Answer,
            AgentMessageKind.Unavailable => Translator.AssistantMessage_Unavailable,
            _ => Translator.VoiceBubble_Error_NotUnderstood,
        };

        // A refused standing rule can still run once now: the follow-up carries the reading without its condition.
        if (message.FollowUps.FirstOrDefault(static f => f.Kind == AgentFollowUpKind.RunOnce && f.Resubmit is not null) is { } runOnce)
        {
            _followUp = runOnce;
            var bubble = _host.Bubble;
            bubble.ConfirmationText = VoiceBubbleText.FromMarked(text);
            bubble.ConfirmationSteps = null;
            bubble.ConfirmationGroups = string.IsNullOrWhiteSpace(runOnce.Gloss)
                ? null
                : new[] { new VoiceBubbleStepGroup(VoiceBubbleStepGroupKind.None, null, null, Array.Empty<IAccountIconInfo>(), new[] { new VoiceBubbleStep(VoiceBubbleText.FromMarked(runOnce.Gloss)) }) };
            bubble.ConfirmText = Translator.AssistantPlan_RunOnce;
            bubble.DeclineText = Translator.Buttons_Cancel;
            Present(VoiceBubbleState.Confirmation);
            return;
        }

        if (message.Kind is AgentMessageKind.Remembered or AgentMessageKind.Forgotten or AgentMessageKind.ProposalSaved or AgentMessageKind.Undone)
        {
            var bubble = _host.Bubble;
            bubble.SuccessDismissMilliseconds = _defaultSuccessDismissMilliseconds;
            bubble.Message = VoiceBubbleText.FromMarked(string.IsNullOrWhiteSpace(message.Gloss) ? text : text + "\n" + message.Gloss);
            Present(VoiceBubbleState.Success);
            return;
        }

        Fail(text, canRetry: message.Kind is AgentMessageKind.NotUnderstood or AgentMessageKind.NeedsPreviousRequest);
    }

    private void AskQuestion(Clarification clarification)
    {
        EndQuestions();
        _clarification = clarification;
        var bubble = _host.Bubble;
        bubble.QuestionText = QuestionText(clarification);
        bubble.IsQuestionSearchable = clarification.Searchable || clarification.Options.Count > 8;
        bubble.QuestionCaption = clarification.Kind is ClarificationKind.PickPerson or ClarificationKind.PickPeople or ClarificationKind.PickAccount
            or ClarificationKind.PickContainer or ClarificationKind.PickSender
            ? Translator.AssistantQuestion_RememberCaption
            : null;
        bubble.ClearQuestionFilter();
        bubble.SetChoices([]);
        Present(VoiceBubbleState.Question);
        _questionFilter = clarification.Filter;
        _ = LoadChoicesAsync(null, TimeSpan.Zero);
    }

    private static string QuestionText(Clarification c)
    {
        var said = c.SaidAs?.Trim() ?? string.Empty;
        string With(string named, string unnamed) => said.Length == 0 ? unnamed : string.Format(CultureInfo.CurrentCulture, named, said);
        return c.Kind switch
        {
            ClarificationKind.PickPerson => With(Translator.AssistantQuestion_PickPerson, Translator.AssistantQuestion_PickPerson.Replace("“{0}”", "").Trim()),
            ClarificationKind.PickPeople => With(Translator.AssistantQuestion_PickPeople, Translator.AssistantQuestion_PickPeople),
            ClarificationKind.PickSender => With(Translator.AssistantQuestion_PickSender, Translator.AssistantQuestion_PickSender),
            ClarificationKind.PickAccount => With(Translator.AssistantQuestion_PickAccount, Translator.AssistantQuestion_PickAccount_Unnamed),
            ClarificationKind.PickContainer => With(Translator.AssistantQuestion_PickContainer, Translator.AssistantQuestion_PickContainer_Unnamed),
            ClarificationKind.ChooseKind => With(Translator.AssistantQuestion_ChooseKind, Translator.AssistantQuestion_ChooseKind),
            ClarificationKind.ChooseItem => With(Translator.AssistantQuestion_ChooseItem, Translator.AssistantQuestion_ChooseItem_Unnamed),
            ClarificationKind.ChooseAction => With(Translator.AssistantQuestion_ChooseAction, Translator.AssistantQuestion_ChooseAction),
            ClarificationKind.ConfirmTime => With(Translator.AssistantQuestion_ConfirmTime, Translator.AssistantQuestion_ConfirmTime),
            ClarificationKind.ConfirmForget => With(Translator.AssistantQuestion_ConfirmForget, Translator.AssistantQuestion_ConfirmForget),
            _ => said,
        };
    }

    private void BubbleQuestionFilterChanged(object? sender, string filter)
    {
        if (_clarification is null)
            return;

        _questionFilter = filter;
        _ = LoadChoicesAsync(filter, QuestionFilterDelay);
    }

    /// <summary>
    /// The question's choices, filtered on the device by what the user typed (title and detail, ignoring case and
    /// accents). A person question also offers a typed address. A newer filter replaces the one in flight.
    /// </summary>
    private async Task LoadChoicesAsync(string? filter, TimeSpan delay)
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

            if (_clarification is not { } question || token.IsCancellationRequested || State != VoiceBubbleState.Question)
                return;

            var text = filter?.Trim() ?? string.Empty;
            var compare = CultureInfo.CurrentCulture.CompareInfo;
            bool Matches(string? value) => !string.IsNullOrWhiteSpace(value) && compare.IndexOf(value, text, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
            var options = question.Options
                .Where(o => text.Length == 0 || Matches(o.Title) || Matches(o.Detail) || Matches(o.Group))
                .Where(o => !question.AllowsMany || !_picked.Exists(p => p.Key == o.Key))
                .ToList();

            var accounts = await _accountService.GetAccountsAsync() ?? [];
            if (token.IsCancellationRequested)
                return;

            var choices = new List<VoiceBubbleChoice>(options.Count + 4);
            if (question.AllowsMany && _picked.Count > 0)
            {
                choices.Add(new VoiceBubbleChoice(DoneChoiceKey,
                    string.Format(CultureInfo.CurrentCulture, Translator.AssistantQuestion_Done, _picked.Count),
                    string.Join(", ", _picked.Select(static p => p.Title)), null));
            }

            if (question.AllowsNewAddress && text.Contains('@') && text.Contains('.') && !text.Contains(' '))
                choices.Add(new VoiceBubbleChoice(NewAddressPrefix + text, string.Format(CultureInfo.CurrentCulture, Translator.AssistantQuestion_UseAddress, text), null, null));

            string? group = null;
            foreach (var option in options)
            {
                var account = option.Ref.AccountId is { } id ? accounts.FirstOrDefault(a => a.Id == id) : null;
                if (!string.IsNullOrWhiteSpace(option.Group))
                {
                    if (!string.Equals(group, option.Group, StringComparison.Ordinal))
                    {
                        group = option.Group;
                        choices.Add(new VoiceBubbleChoice("h:" + option.Key, option.Group, null, account is null ? null : ToIcon(account), isHeader: true));
                    }

                    choices.Add(new VoiceBubbleChoice(option.Key, option.Title, option.Detail, null));
                    continue;
                }

                choices.Add(new VoiceBubbleChoice(option.Key, option.Title, option.Detail,
                    option.Ref.Kind == ItemKind.Account && account is not null ? ToIcon(account) : null));
            }

            if (!token.IsCancellationRequested && State == VoiceBubbleState.Question)
                _host.Bubble.SetChoices(choices);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load the choices for an assistant question.");
        }
    }

    /// <summary>An answer goes back to the runtime, which asks its next question or returns the plan.</summary>
    private void BubbleChoiceSelected(object? sender, VoiceBubbleChoice choice)
    {
        if (_clarification is not { } question || choice.Key.StartsWith("h:", StringComparison.Ordinal))
            return;

        ClarificationAnswer answer;
        if (choice.Key.StartsWith(NewAddressPrefix, StringComparison.Ordinal))
        {
            answer = new ClarificationAnswer(question.Id, [], choice.Key[NewAddressPrefix.Length..]);
        }
        else if (question.AllowsMany && choice.Key != DoneChoiceKey)
        {
            if (question.Options.FirstOrDefault(o => o.Key == choice.Key) is { } member)
                _picked.Add(member);

            _host.Bubble.ClearQuestionFilter();
            _ = LoadChoicesAsync(null, TimeSpan.Zero);
            return;
        }
        else if (question.AllowsMany)
        {
            if (_picked.Count == 0)
                return;

            answer = new ClarificationAnswer(question.Id, _picked.Select(static p => p.Key).ToList());
        }
        else
        {
            answer = ClarificationAnswer.Pick(question.Id, choice.Key);
        }

        EndQuestions();
        if (_requestCancellation is not { } cancellation || _agentSession is not { } session)
        {
            Close();
            return;
        }

        Present(VoiceBubbleState.Analyzing);
        _ = AnswerAsync(session, answer, cancellation);
    }

    private async Task AnswerAsync(IAgentSession session, ClarificationAnswer answer, CancellationTokenSource cancellation)
    {
        try
        {
            var turn = await session.AnswerAsync(answer, cancellation.Token);
            await ShowTurnAsync(turn, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Continuing an assistant request after its question failed.");
            if (!IsStale(cancellation))
                Fail(Translator.VoiceBubble_Error_Unavailable, canRetry: true);
        }
    }

    private void EndQuestions()
    {
        _candidateCancellation?.Cancel();
        _candidateCancellation?.Dispose();
        _candidateCancellation = null;
        _clarification = null;
        _questionFilter = null;
        _picked.Clear();
    }

    /// <summary>
    /// Shows the resolved plan, its steps grouped by account. A request about a selection, or auto-confirm, runs
    /// without Yes unless a step needs confirmation; steps that can't be undone get a second confirmation either way.
    /// </summary>
    private async Task ShowPlanAsync(ResolvedPlan plan, CancellationTokenSource cancellation)
    {
        StopRendering();
        StopCapture();
        if (plan.Steps.Count == 0)
        {
            Close();
            return;
        }

        var accounts = await _accountService.GetAccountsAsync() ?? [];
        if (IsStale(cancellation))
            return;

        _plan = plan;
        _followUp = null;
        _riskAcceptedSteps = null;
        var groups = plan.Steps.GroupBy(step => AccountOf(step)).ToList();
        _groupSteps = groups.Select(static g => g.Select(static step => step.N).ToList()).ToList();
        _bubbleGroups = groups.Select((g, index) => ToBubbleGroup(g.Key is { } id ? accounts.FirstOrDefault(a => a.Id == id) : null, g.ToList(),
            index == 0 && plan.MayHaveMisunderstood ? [Translator.AssistantPlan_Misunderstood] : [])).ToArray();

        if ((_preferences.IsVoiceAutoConfirmEnabled || _selection is not null) && !plan.NeedsConfirmation && !plan.MayHaveMisunderstood)
        {
            _ = RunPlanAsync(plan.Steps.Select(static step => step.N).ToList());
            return;
        }

        var bubble = _host.Bubble;
        bubble.ConfirmText = Translator.Buttons_Yes;
        bubble.DeclineText = Translator.Buttons_No;
        bubble.ConfirmationText = null;
        bubble.ConfirmationSteps = null;
        bubble.ConfirmationGroups = _bubbleGroups;
        Present(VoiceBubbleState.Confirmation);
    }

    private static Guid? AccountOf(ResolvedStep step) => step.Target switch
    {
        StepTarget.Mails m => m.AccountId,
        StepTarget.Events e => e.AccountId,
        StepTarget.Tasks t => t.AccountId,
        StepTarget.Folder f => f.AccountId,
        StepTarget.TaskListTarget l => l.AccountId,
        StepTarget.MailItems m when m.Items.Count > 0 => m.Items[0].AccountId,
        StepTarget.EventItems e when e.Items.Count > 0 => e.Items[0].AccountId,
        StepTarget.TaskItems t when t.Items.Count > 0 => t.Items[0].AccountId,
        _ => step.Args switch
        {
            StepArgs.DraftArgs d => d.AccountId,
            StepArgs.FolderArgs f => f.AccountId,
            StepArgs.SystemArgs s => s.AccountId,
            _ => null,
        },
    };

    /// <summary>
    /// Yes or Run once: runs the accepted accounts' steps (or the refused rule once), after a second confirmation when
    /// a step can't be undone easily. Cancelling the second confirmation drops the whole plan.
    /// </summary>
    private void Confirm()
    {
        if (_followUp is { Resubmit: { } resubmit })
        {
            _followUp = null;
            if (_requestCancellation is not { } cancellation || _agentSession is not { } session)
            {
                Close();
                return;
            }

            Present(VoiceBubbleState.Analyzing);
            _ = RunOnceAsync(session, resubmit, cancellation);
            return;
        }

        if (_proposalAnswer is { } proposal)
        {
            // Like "No", the answer ends the question; a further proposal shows the bubble again.
            _proposalAnswer = null;
            proposal.TrySetResult(true);
            if (State == VoiceBubbleState.Confirmation)
                Present(VoiceBubbleState.Hidden);
            return;
        }

        if (_plan is not { } plan || _groupSteps is null || _bubbleGroups is null)
        {
            Close();
            return;
        }

        if (_riskAcceptedSteps is { } confirmed)
        {
            _riskAcceptedSteps = null;
            _ = RunPlanAsync(confirmed);
            return;
        }

        var accepted = _bubbleGroups
            .Select((group, index) => (Group: group, Index: index))
            .Where(static entry => entry.Group.CanSelect && entry.Group.IsSelected)
            .SelectMany(entry => _groupSteps[entry.Index])
            .ToList();
        if (accepted.Count == 0)
        {
            Close();
            return;
        }

        var risky = plan.Steps.Where(step => accepted.Contains(step.N) && step.Risk == RiskLevel.High).ToList();
        if (risky.Count == 0)
        {
            _ = RunPlanAsync(accepted);
            return;
        }

        _riskAcceptedSteps = accepted;
        var bubble = _host.Bubble;
        var steps = risky.Select(static step => new VoiceBubbleStep(VoiceBubbleText.FromMarked(step.Gloss ?? step.Action.ToString()),
            step.Permanent ? Translator.AssistantPlan_Permanent : step.EstimatedCount is int count ? EstimatedCountText(count) : null)).ToArray();
        bubble.ConfirmationText = VoiceBubbleText.FromMarked(Translator.AssistantPlan_ConfirmHighRisk);
        bubble.ConfirmationGroups = new[] { new VoiceBubbleStepGroup(VoiceBubbleStepGroupKind.None, null, null, Array.Empty<IAccountIconInfo>(), steps) };
        bubble.ConfirmText = Translator.VoiceGuard_Continue;
        bubble.DeclineText = Translator.Buttons_Cancel;
        Present(VoiceBubbleState.Confirmation);
    }

    private async Task RunOnceAsync(IAgentSession session, AssistantReading reading, CancellationTokenSource cancellation)
    {
        try
        {
            var selection = await GetAgentSelectionAsync(cancellation.Token);
            var turn = await session.HandleAsync(new AgentRequest(_requestText, AgentInputKind.Typed, selection, selection?.AccountId, reading), cancellation.Token);
            await ShowTurnAsync(turn, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Running a refused rule once failed.");
            if (!IsStale(cancellation))
                Fail(Translator.VoiceBubble_Error_Unavailable, canRetry: false);
        }
    }

    /// <summary>
    /// Confirms the accepted steps with the runtime, runs the plan through the executor, reports the outcome back (the
    /// conversation context and the "Remember this?" proposals use it) and shows what happened.
    /// </summary>
    private async Task RunPlanAsync(IReadOnlyList<int> accepted)
    {
        var cancellation = _requestCancellation;
        if (cancellation is null || _agentSession is not { } session || _plan is not { } plan || _executor.Host is null)
        {
            Fail(Translator.VoiceBubble_Error_Unavailable, canRetry: false);
            return;
        }

        Present(VoiceBubbleState.Analyzing);

        PlanOutcome outcome;
        ResolvedPlan toRun;
        try
        {
            var confirmed = await session.ConfirmAsync(new PlanConfirmation(plan.Id, accepted), cancellation.Token);
            if (confirmed is not AgentTurn.Plan { Resolved: { } ready })
            {
                Close();
                return;
            }

            toRun = ready;
            outcome = await _executor.ExecuteAsync(toRun, new Progress<StepOutcome>(), cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Running an assistant plan failed.");
            if (!IsStale(cancellation))
                Fail(Translator.VoiceBubble_Error_Unavailable, canRetry: false);
            return;
        }

        if (IsStale(cancellation))
            return;

        _plan = null;
        var lines = new List<string>();
        var failed = false;
        foreach (var step in outcome.Steps)
        {
            var gloss = toRun.Steps.FirstOrDefault(s => s.N == step.N)?.Gloss ?? string.Empty;
            switch (step.Status)
            {
                case StepStatus.Done:
                    if (gloss.Length > 0) lines.Add(gloss);
                    break;
                case StepStatus.NothingMatched:
                    lines.Add(gloss.Length > 0 ? gloss + ": " + Translator.AssistantMessage_NothingMatched : Translator.AssistantMessage_NothingMatched);
                    break;
                case StepStatus.Failed:
                    failed = true;
                    lines.Add(string.Format(CultureInfo.CurrentCulture, Translator.AssistantPlan_StepFailed, step.N) + (step.Detail is { Length: > 0 } detail ? " " + SentenceStart(detail) : string.Empty));
                    break;
            }
        }

        var bubble = _host.Bubble;
        bubble.SuccessDismissMilliseconds = _defaultSuccessDismissMilliseconds;
        if (failed)
        {
            Fail(string.Join("\n", lines), canRetry: false);
        }
        else
        {
            bubble.Message = VoiceBubbleText.FromMarked(string.Join("\n", lines));
            Present(VoiceBubbleState.Success);
        }

        // Reported after the result is on screen: proposals ("Remember this?") may follow from here.
        try
        {
            await session.ReportAsync(outcome, cancellation.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Could not report an assistant outcome to the runtime.");
        }
    }

    /// <summary>The runtime's "Remember this?": asked in the bubble while it is open; dismissing it declines.</summary>
    private async Task<bool> ProposeAsync(BrainProposal proposal, CancellationToken ct)
    {
        var text = await ProposalTextAsync(proposal).ConfigureAwait(false);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Post(() =>
        {
            if (_disposed || State == VoiceBubbleState.Hidden)
            {
                answer.TrySetResult(false);
                return;
            }

            _proposalAnswer = answer;
            var bubble = _host.Bubble;
            bubble.ConfirmationText = VoiceBubbleText.FromMarked(text);
            bubble.ConfirmationGroups = null;
            bubble.ConfirmationSteps = null;
            bubble.ConfirmText = Translator.Buttons_Yes;
            bubble.DeclineText = Translator.Buttons_No;
            Present(VoiceBubbleState.Confirmation);
        });
        using var registration = ct.Register(() => answer.TrySetCanceled(ct));
        return await answer.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// A default's value is a device key ("calendar=Calendar:…"); the question names the calendar, list or account
    /// it points at instead.
    /// </summary>
    private async Task<string> ProposalTextAsync(BrainProposal proposal)
    {
        if (proposal.Kind != BrainEntryKind.Default)
        {
            var who = proposal.Subject.Name ?? proposal.Subject.Address ?? proposal.Subject.Text ?? proposal.Value;
            return string.Format(CultureInfo.CurrentCulture, Translator.AssistantProposal_Remember, proposal.SaidAs, who);
        }

        var separator = proposal.Value.IndexOf('=');
        var use = separator > 0 ? proposal.Value[..separator] : proposal.Value;
        var target = separator > 0 ? ItemRef.Parse(proposal.Value[(separator + 1)..]) : null;
        var name = proposal.SaidAs;
        if (target is { Kind: ItemKind.Account, AccountId: { } accountId })
        {
            try
            {
                var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);
                if (account is not null)
                    name = GetAccountTitle(account);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not read the account an assistant proposal names.");
            }
        }

        var format = use switch
        {
            "calendar" => Translator.AssistantProposal_DefaultCalendar,
            "task" or "taskList" => Translator.AssistantProposal_DefaultTaskList,
            _ => Translator.AssistantProposal_DefaultMailAccount,
        };
        return string.Format(CultureInfo.CurrentCulture, format, name);
    }

    private VoiceBubbleStepGroup ToBubbleGroup(MailAccount? account, IReadOnlyList<ResolvedStep> steps, IReadOnlyList<string> notices)
    {
        var bubbleSteps = steps
            .Select(static step => new VoiceBubbleStep(VoiceBubbleText.FromMarked(step.Gloss ?? step.Action.ToString()),
                step.Permanent ? Translator.AssistantPlan_Permanent
                    : step.EstimatedCount is int count ? EstimatedCountText(count) : null,
                step.N))
            .ToArray();
        if (account is null)
            return new VoiceBubbleStepGroup(VoiceBubbleStepGroupKind.None, null, null, Array.Empty<IAccountIconInfo>(), bubbleSteps, notices);

        var title = GetAccountTitle(account);
        return new VoiceBubbleStepGroup(
            VoiceBubbleStepGroupKind.Account,
            title,
            string.Equals(title, account.Address, StringComparison.OrdinalIgnoreCase) ? null : account.Address,
            new[] { ToIcon(account) },
            bubbleSteps,
            notices);
    }

    // Step details are written as fragments ("the mail isn't downloaded yet"); after "Step 1 failed." they start a sentence.
    private static string SentenceStart(string text)
        => char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];

    private static string EstimatedCountText(int count)
        => count == 1 ? Translator.AssistantPlan_EstimatedCount_One : string.Format(CultureInfo.CurrentCulture, Translator.AssistantPlan_EstimatedCount, count);

    private static string GetAccountTitle(MailAccount account)
        => !string.IsNullOrWhiteSpace(account.Name) ? account.Name
            : !string.IsNullOrWhiteSpace(account.SenderName) ? account.SenderName
            : account.Address ?? string.Empty;

    private IAccountIconInfo ToIcon(MailAccount account) => MailAccountIconInfoFactory.Create(account, _pictureStorage);

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
        if (_proposalAnswer is { } proposal)
        {
            // "No" on "Remember this?" only declines the proposal; the result already shown stays.
            _proposalAnswer = null;
            proposal.TrySetResult(false);
            if (State == VoiceBubbleState.Confirmation)
            {
                Present(VoiceBubbleState.Hidden);
                return;
            }
        }

        EndRequest();
        ClearSelection();
        if (State == VoiceBubbleState.Hidden)
            _host.Hide();
        else
            Present(VoiceBubbleState.Hidden);
    }

    /// <summary>Cancels the request in flight, closes the microphone and the socket.</summary>
    private void EndRequest()
    {
        EndQuestions();
        _plan = null;
        _riskAcceptedSteps = null;
        _followUp = null;

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
        IAssistantCommandSession? session;
        lock (_audioLock)
        {
            session = _session;
            _session = null;
            _pendingAudio.Clear();
        }

        if (session is not null)
            _ = DisposeQuietlyAsync(session);
    }

    private static async Task DisposeQuietlyAsync(IAssistantCommandSession session)
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

    private void BubbleDismissed(object? sender, EventArgs e)
    {
        EndRequest();
        ClearSelection();
    }

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
        AssistantErrorCodes.QuotaExceeded or
        VoiceClientErrorCodes.AccessDenied);

    private static string GetErrorMessage(string? code) => code switch
    {
        WinoAccountClientErrorCodes.SignInRequired or ApiErrorCodes.SessionInvalid => Translator.VoiceBubble_Error_SignIn,
        ApiErrorCodes.AiPackRequired => Translator.VoiceBubble_Error_IntelligenceRequired,
        ApiErrorCodes.IntelligenceConsentRequired or ApiErrorCodes.IntelligenceConsentVersionOutdated => Translator.VoiceBubble_Error_Consent,
        ApiErrorCodes.AiQuotaExceeded or AssistantErrorCodes.QuotaExceeded => Translator.VoiceBubble_Error_Quota,
        VoiceClientErrorCodes.AccessDenied => Translator.VoiceBubble_Error_AccessDenied,
        ApiErrorCodes.RateLimited => Translator.VoiceBubble_Error_RateLimited,
        AssistantErrorCodes.TranscriptionFailed => Translator.VoiceBubble_Error_NotHeard,
        AssistantErrorCodes.ReaderFailed => Translator.VoiceBubble_Error_PlanFailed,
        AssistantErrorCodes.Timeout => Translator.VoiceBubble_Error_Timeout,
        _ => Translator.VoiceBubble_Error_Unavailable
    };

    private static string GetMicrophoneMessage(MicrophoneAccess access) => access switch
    {
        MicrophoneAccess.Denied => Translator.VoiceControlSettings_Microphone_AccessDenied,
        MicrophoneAccess.NoDevice => Translator.VoiceControlSettings_Microphone_NoDevice,
        _ => Translator.VoiceControlSettings_Microphone_Unavailable
    };
}
