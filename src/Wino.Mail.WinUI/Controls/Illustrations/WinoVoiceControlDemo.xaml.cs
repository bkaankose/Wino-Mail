using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using Wino.Core.Domain;
using Wino.Mail.Controls.Core.VoiceBubble;

namespace Wino.Controls.Illustrations;

/// <summary>
/// Plays the voice bubble through one command in a loop: the shortcut, listening, the step list, Yes and the
/// result. It runs only while loaded and visible; with animations turned off in Windows it shows the step list.
/// </summary>
public sealed partial class WinoVoiceControlDemo : Microsoft.UI.Xaml.Controls.UserControl
{
    private static readonly TimeSpan KeysDelay = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan KeysVisible = TimeSpan.FromMilliseconds(1100);
    private static readonly TimeSpan AnalyzeDelay = TimeSpan.FromMilliseconds(1400);
    private static readonly TimeSpan ConfirmationVisible = TimeSpan.FromMilliseconds(3400);
    private static readonly TimeSpan ExecuteDelay = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan LoopPause = TimeSpan.FromMilliseconds(1600);
    private const double AutoSendSilenceSeconds = 0.8;

    private readonly VoiceBubbleSimulator _voice = new();
    private readonly UISettings _uiSettings = new();
    private readonly DispatcherQueueTimer _timer;
    private Action? _next;
    private bool _isRendering;
    private bool _isRunning;
    private TimeSpan _lastRenderTime;

    public WinoVoiceControlDemo()
    {
        InitializeComponent();

        Bubble.ShowsTranscript = true;
        Bubble.IsCountdownVisible = true;
        Bubble.SuccessDismissMilliseconds = 2600;
        Bubble.ListeningText = Translator.VoiceBubble_Listening;
        Bubble.ConfirmText = Translator.Buttons_Yes;
        Bubble.DeclineText = Translator.Buttons_No;
        Bubble.RetryText = Translator.Buttons_TryAgain;
        Bubble.ConfirmationSteps = new VoiceBubbleStep[]
        {
            new(VoiceBubbleText.FromMarked(Translator.VoiceControlDemo_Step1), Translator.VoiceControlDemo_Step1_Detail),
            new(VoiceBubbleText.FromMarked(Translator.VoiceControlDemo_Step2)),
            new(VoiceBubbleText.FromMarked(Translator.VoiceControlDemo_Step3), Translator.VoiceControlDemo_Step3_Detail),
        };
        Bubble.Message = VoiceBubbleText.FromMarked(Translator.VoiceControlDemo_Success);
        Bubble.ExitCompleted += BubbleExitCompleted;

        _timer = DispatcherQueue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += TimerTick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateRunning());
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateRunning();

    private void OnUnloaded(object sender, RoutedEventArgs e) => Stop();

    private void UpdateRunning()
    {
        if (IsLoaded && Visibility == Visibility.Visible)
            Start();
        else
            Stop();
    }

    private void Start()
    {
        if (_isRunning)
            return;

        _isRunning = true;
        if (!_uiSettings.AnimationsEnabled)
        {
            Bubble.Transcript = Translator.VoiceControlDemo_Spoken;
            Bubble.State = VoiceBubbleState.Confirmation;
            return;
        }

        Restart();
    }

    private void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _timer.Stop();
        _next = null;
        StopRendering();
        KeyCaps.Opacity = 0;
        Bubble.State = VoiceBubbleState.Hidden;
    }

    private void Restart()
    {
        KeyCaps.Opacity = 0;
        Bubble.State = VoiceBubbleState.Hidden;
        Schedule(KeysDelay, () =>
        {
            KeyCaps.Opacity = 1;
            Schedule(KeysVisible, Listen);
        });
    }

    private void Listen()
    {
        KeyCaps.Opacity = 0;
        _voice.Speak(Translator.VoiceControlDemo_Spoken);
        Bubble.Transcript = null;
        Bubble.InterimTranscript = null;
        Bubble.State = VoiceBubbleState.Listening;
        StartRendering();
    }

    private void Send()
    {
        StopRendering();
        Bubble.Transcript = Translator.VoiceControlDemo_Spoken;
        Bubble.InterimTranscript = null;
        Bubble.State = VoiceBubbleState.Analyzing;
        Schedule(AnalyzeDelay, () =>
        {
            Bubble.State = VoiceBubbleState.Confirmation;
            Schedule(ConfirmationVisible, () =>
            {
                Bubble.State = VoiceBubbleState.Analyzing;
                Schedule(ExecuteDelay, () => Bubble.State = VoiceBubbleState.Success);
            });
        });
    }

    private void BubbleExitCompleted(object? sender, EventArgs e)
    {
        if (_isRunning && _uiSettings.AnimationsEnabled && Bubble.State == VoiceBubbleState.Hidden && _next is null)
            Schedule(LoopPause, Restart);
    }

    private void Schedule(TimeSpan delay, Action next)
    {
        _next = next;
        _timer.Interval = delay;
        _timer.Start();
    }

    private void TimerTick(DispatcherQueueTimer sender, object args)
    {
        var next = _next;
        _next = null;
        if (_isRunning)
            next?.Invoke();
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

        if (Bubble.State != VoiceBubbleState.Listening)
            return;

        _voice.Step(delta);
        Bubble.UpdateBands(_voice.Bands);
        Bubble.Transcript = _voice.HeardText;
        Bubble.InterimTranscript = _voice.InterimText;

        if (_voice.IsFinished && _voice.Silence > AutoSendSilenceSeconds)
            Send();
    }
}
