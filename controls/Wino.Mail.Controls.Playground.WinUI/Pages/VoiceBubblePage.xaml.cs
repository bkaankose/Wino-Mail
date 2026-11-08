using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Core;
using VirtualKey = Windows.System.VirtualKey;
using Wino.Mail.Controls.Core.VoiceBubble;
using Wino.Mail.Controls.Playground.Models;
using Wino.Mail.Controls.VoiceBubble;

namespace Wino.Mail.Controls.Playground.Pages;

/// <summary>
/// Drives the voice bubble through the flow a host would run: listen, send, analyze, then confirm,
/// succeed or fail. Timings stand in for the API round trip.
/// </summary>
public sealed partial class VoiceBubblePage : Page
{
    private static readonly TimeSpan AnalyzeDelay = TimeSpan.FromMilliseconds(1600);
    private static readonly TimeSpan ExecuteDelay = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan AutoSendSilence = TimeSpan.FromMilliseconds(800);

    private readonly VoiceBubbleSimulator _liveVoice = new();
    private readonly VoiceBubbleSimulator _sampleVoice = new();
    private DispatcherQueueTimer? _flowTimer;
    private Action? _flowStep;
    private TimeSpan _lastRenderTime;
    private int _samplePhrase;
    private bool _isRendering;

    public VoiceBubblePage()
    {
        InitializeComponent();

        foreach (var scenario in VoiceBubbleScenario.All)
        {
            ScenarioBox.Items.Add(scenario.Name);
        }

        FailureBox.Items.Add("From the command");
        foreach (var failure in VoiceBubbleFailure.All)
        {
            FailureBox.Items.Add(failure.Name);
        }

        ScenarioBox.SelectedIndex = 0;
        FailureBox.SelectedIndex = 0;
    }

    private VoiceBubbleScenario Scenario => VoiceBubbleScenario.All[Math.Max(0, ScenarioBox.SelectedIndex)];

    private VoiceBubbleFailure Failure => FailureBox.SelectedIndex > 0
        ? VoiceBubbleFailure.All[FailureBox.SelectedIndex - 1]
        : Scenario.Failure ?? VoiceBubbleFailure.All[0];

    private void PageLoaded(object sender, RoutedEventArgs e)
    {
        _flowTimer = DispatcherQueue.CreateTimer();
        _flowTimer.IsRepeating = false;
        _flowTimer.Tick += FlowTimerTick;

        LiveBubble.ShowsTranscript = LiveWordsToggle.IsOn;
        PresentSamples();
        _sampleVoice.Speak(VoiceBubbleScenario.All[0].Spoken);

        CompositionTarget.Rendering += OnRendering;
        _isRendering = true;
        Show(VoiceBubbleState.Listening);
    }

    private void PageUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isRendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isRendering = false;
        }

        if (_flowTimer is not null)
        {
            _flowTimer.Stop();
            _flowTimer.Tick -= FlowTimerTick;
            _flowTimer = null;
        }

        _flowStep = null;
    }

    private void PresentSamples()
    {
        foreach (var sample in new[] { ListeningSample, AnalyzingSample, InlineConfirmationSample, StepsConfirmationSample, SuccessSample, FailedSample, LiveWordsListeningSample, LiveWordsAnalyzingSample })
        {
            sample.SuccessDismissMilliseconds = 0;
            sample.FailureDismissMilliseconds = 0;
            sample.IsCountdownVisible = false;
        }

        var delete = VoiceBubbleScenario.All[1];
        var steps = VoiceBubbleScenario.All[2];

        ListeningSample.State = VoiceBubbleState.Listening;
        AnalyzingSample.State = VoiceBubbleState.Analyzing;

        InlineConfirmationSample.ConfirmationText = delete.Confirmation;
        InlineConfirmationSample.State = VoiceBubbleState.Confirmation;

        StepsConfirmationSample.ConfirmationText = steps.Confirmation;
        StepsConfirmationSample.ConfirmationSteps = steps.Steps;
        StepsConfirmationSample.State = VoiceBubbleState.Confirmation;

        SuccessSample.Message = VoiceBubbleScenario.All[0].Success;
        SuccessSample.State = VoiceBubbleState.Success;

        FailedSample.Message = VoiceBubbleFailure.All[0].Message;
        FailedSample.State = VoiceBubbleState.Failed;

        LiveWordsListeningSample.State = VoiceBubbleState.Listening;
        LiveWordsAnalyzingSample.Transcript = steps.Spoken;
        LiveWordsAnalyzingSample.State = VoiceBubbleState.Analyzing;
    }

    private void Show(VoiceBubbleState state)
    {
        CancelFlowStep();
        var scenario = Scenario;

        LiveBubble.ConfirmationText = scenario.Confirmation;
        LiveBubble.ConfirmationSteps = scenario.Steps;
        LiveBubble.ConfirmationGroups = scenario.Groups.Length > 0 ? scenario.Groups : null;

        switch (state)
        {
            case VoiceBubbleState.Listening:
                _liveVoice.Speak(scenario.Spoken);
                LiveBubble.Transcript = null;
                LiveBubble.InterimTranscript = null;
                break;
            case VoiceBubbleState.Analyzing when LiveBubble.State == VoiceBubbleState.Listening:
                LiveBubble.InterimTranscript = null;
                break;
            case VoiceBubbleState.Success:
                LiveBubble.Message = scenario.Success ?? VoiceBubbleText.FromMarked("Done");
                break;
            case VoiceBubbleState.Failed:
                var failure = Failure;
                LiveBubble.Message = failure.Message;
                LiveBubble.CanRetry = failure.CanRetry;
                break;
        }

        if (LiveBubble.State == state)
        {
            LiveBubble.Refresh();
        }
        else
        {
            LiveBubble.State = state;
        }

        UpdateStatus();
    }

    private void Hide()
    {
        CancelFlowStep();
        LiveBubble.State = VoiceBubbleState.Hidden;
        UpdateStatus();
    }

    private void Send()
    {
        if (LiveBubble.State != VoiceBubbleState.Listening)
        {
            return;
        }

        Show(VoiceBubbleState.Analyzing);
        ScheduleFlowStep(AnalyzeDelay, () =>
        {
            var scenario = Scenario;
            Show(scenario.Failure is not null
                ? VoiceBubbleState.Failed
                : scenario.NeedsConfirmation ? VoiceBubbleState.Confirmation : VoiceBubbleState.Success);
        });
    }

    private void Confirm()
    {
        if (LiveBubble.State != VoiceBubbleState.Confirmation)
        {
            return;
        }

        Show(VoiceBubbleState.Analyzing);
        ScheduleFlowStep(ExecuteDelay, () => Show(VoiceBubbleState.Success));
    }

    private void Shortcut()
    {
        switch (LiveBubble.State)
        {
            case VoiceBubbleState.Hidden:
                Show(VoiceBubbleState.Listening);
                break;
            case VoiceBubbleState.Listening:
                Send();
                break;
            case VoiceBubbleState.Confirmation:
                Confirm();
                break;
            case VoiceBubbleState.Success:
            case VoiceBubbleState.Failed:
                Hide();
                break;
        }
    }

    private void ScheduleFlowStep(TimeSpan delay, Action step)
    {
        if (_flowTimer is null)
        {
            return;
        }

        _flowStep = step;
        _flowTimer.Interval = delay;
        _flowTimer.Start();
    }

    private void CancelFlowStep()
    {
        _flowTimer?.Stop();
        _flowStep = null;
    }

    private void FlowTimerTick(DispatcherQueueTimer sender, object args)
    {
        var step = _flowStep;
        _flowStep = null;
        step?.Invoke();
    }

    [WinRT.DynamicWindowsRuntimeCast(typeof(RenderingEventArgs))]
    private void OnRendering(object? sender, object e)
    {
        var now = e is RenderingEventArgs rendering ? rendering.RenderingTime : _lastRenderTime;
        var delta = Math.Clamp((now - _lastRenderTime).TotalSeconds, 0, 0.05);
        _lastRenderTime = now;

        if (LiveBubble.State == VoiceBubbleState.Listening)
        {
            _liveVoice.Step(delta);
            LiveBubble.UpdateBands(_liveVoice.Bands);
            LiveBubble.Transcript = _liveVoice.HeardText;
            LiveBubble.InterimTranscript = _liveVoice.InterimText;

            if (AutoSendToggle.IsOn && _liveVoice.IsFinished && _liveVoice.Silence > AutoSendSilence.TotalSeconds)
            {
                Send();
            }
        }

        _sampleVoice.Step(delta);
        ListeningSample.UpdateBands(_sampleVoice.Bands);
        LiveWordsListeningSample.UpdateBands(_sampleVoice.Bands);
        LiveWordsListeningSample.Transcript = _sampleVoice.HeardText;
        LiveWordsListeningSample.InterimTranscript = _sampleVoice.InterimText;
        if (_sampleVoice.IsFinished && _sampleVoice.Silence > 0.6)
        {
            _samplePhrase = (_samplePhrase + 1) % VoiceBubbleScenario.All.Length;
            _sampleVoice.Speak(VoiceBubbleScenario.All[_samplePhrase].Spoken);
        }
    }

    private void UpdateStatus()
    {
        var state = LiveBubble.State;
        StatusText.Text = $"State: {state}";
        HintText.Visibility = state == VoiceBubbleState.Hidden ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            CancelFlow();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Space && IsDown(VirtualKey.Control) && IsDown(VirtualKey.Menu))
        {
            Shortcut();
            e.Handled = true;
        }
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void CancelFlow()
    {
        if (LiveBubble.State is not VoiceBubbleState.Hidden)
        {
            Hide();
        }
    }

    private void ScenarioChanged(object sender, SelectionChangedEventArgs e)
    {
        SpokenText.Text = $"“{Scenario.Spoken}”";
        if (IsLoaded)
        {
            Show(VoiceBubbleState.Listening);
        }
    }

    private void FailureChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && LiveBubble.State == VoiceBubbleState.Failed)
        {
            Show(VoiceBubbleState.Failed);
        }
    }

    private void ExitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LiveBubble is null)
        {
            return;
        }

        LiveBubble.ExitAnimation = ExitBox.SelectedIndex switch
        {
            1 => VoiceBubbleExitAnimation.Slide,
            2 => VoiceBubbleExitAnimation.Fade,
            _ => VoiceBubbleExitAnimation.Collapse,
        };
    }

    private void CountdownToggled(object sender, RoutedEventArgs e)
    {
        if (LiveBubble is not null)
        {
            LiveBubble.IsCountdownVisible = CountdownToggle.IsOn;
        }
    }

    private void LiveWordsToggled(object sender, RoutedEventArgs e)
    {
        if (LiveBubble is not null)
        {
            LiveBubble.ShowsTranscript = LiveWordsToggle.IsOn;
        }
    }

    private void ThemeToggled(object sender, RoutedEventArgs e)
    {
        if (SampleSurface is not null)
        {
            SampleSurface.RequestedTheme = ThemeToggle.IsOn ? ElementTheme.Dark : ElementTheme.Light;
        }
    }

    private void ListeningClicked(object sender, RoutedEventArgs e) => Show(VoiceBubbleState.Listening);

    private void AnalyzingClicked(object sender, RoutedEventArgs e) => Show(VoiceBubbleState.Analyzing);

    private void ConfirmationClicked(object sender, RoutedEventArgs e)
    {
        if (!Scenario.NeedsConfirmation)
        {
            ScenarioBox.SelectionChanged -= ScenarioChanged;
            ScenarioBox.SelectedIndex = Array.FindIndex(VoiceBubbleScenario.All, static scenario => scenario.NeedsConfirmation);
            SpokenText.Text = $"“{Scenario.Spoken}”";
            ScenarioBox.SelectionChanged += ScenarioChanged;
        }

        Show(VoiceBubbleState.Confirmation);
    }

    private void SuccessClicked(object sender, RoutedEventArgs e) => Show(VoiceBubbleState.Success);

    private void FailedClicked(object sender, RoutedEventArgs e) => Show(VoiceBubbleState.Failed);

    private void ShortcutClicked(object sender, RoutedEventArgs e) => Shortcut();

    private void CancelClicked(object sender, RoutedEventArgs e) => CancelFlow();

    private void LiveSendRequested(object? sender, EventArgs e) => Send();

    private void LiveCancelRequested(object? sender, EventArgs e) => Hide();

    private void LiveConfirmRequested(object? sender, EventArgs e) => Confirm();

    private void LiveRetryRequested(object? sender, EventArgs e) => Show(VoiceBubbleState.Listening);

    private void LiveDismissed(object? sender, EventArgs e)
    {
        CancelFlowStep();
        UpdateStatus();
    }
}
