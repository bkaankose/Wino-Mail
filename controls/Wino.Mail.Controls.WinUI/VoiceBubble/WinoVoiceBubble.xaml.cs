using System.Numerics;
using CommunityToolkit.WinUI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI.ViewManagement;
using Wino.Mail.Controls.AccountIcon;
using Wino.Mail.Controls.Core.VoiceBubble;

namespace Wino.Mail.Controls.VoiceBubble;

/// <summary>
/// The Dock pill for voice commands: the Wino icon with live voice bars while listening, three dots
/// while the plan is prepared, then a confirmation, a success summary or a failure in the same pill.
///
/// The control only presents. The host owns the microphone, the request and the flow: it sets
/// <see cref="State"/> and the text for it, feeds <see cref="UpdateBands"/> while listening, and reacts
/// to the request events. Success and failure dismiss themselves after their delay and raise
/// <see cref="Dismissed"/>.
/// </summary>
public sealed partial class WinoVoiceBubble : UserControl, IVoiceBubbleView
{
    private const double BarRestHeight = 3;
    private const double BarRange = 20;
    private const double BarFullHeight = 23;
    private const double PillBorder = 2;
    private const float PillElevation = 16f;
    private const int CompactBarCount = 12;
    private const int WideBarCount = 40;
    private const double CompactBarSpacing = 2.33;
    private const double WideBarSpacing = 3.4;
    private const double DimmedTranscriptOpacity = 0.65;
    private const double AccountIconSize = 24;

    private static readonly TimeSpan TranscriptScrollDuration = TimeSpan.FromMilliseconds(280);

    private static readonly TimeSpan MorphDuration = TimeSpan.FromMilliseconds(340);
    private static readonly KeySpline DecelerateSpline = new() { ControlPoint1 = new Point(0.1, 0.9), ControlPoint2 = new Point(0.2, 1) };
    private static readonly KeySpline AccelerateSpline = new() { ControlPoint1 = new Point(0.7, 0), ControlPoint2 = new Point(1, 0.5) };

    private readonly UISettings _uiSettings = new();
    private readonly Rectangle[] _bars;
    private Storyboard? _morphStoryboard;
    private Storyboard? _transitionStoryboard;
    private Storyboard? _countdownStoryboard;
    private Storyboard? _transcriptScrollStoryboard;
    private int _visibleBarCount;
    private VoiceBubbleState _presentedState = VoiceBubbleState.Hidden;
    private int _version;
    private bool _isLoaded;
    private bool _isShown;
    private bool _isPointerOver;
    private bool _isDotsRunning;

    /// <summary>The state the bubble shows. <see cref="VoiceBubbleState.Hidden"/> plays the exit animation.</summary>
    [GeneratedDependencyProperty(DefaultValue = VoiceBubbleState.Hidden)]
    public partial VoiceBubbleState State { get; set; }

    /// <summary>How a success leaves. Every other state fades out.</summary>
    [GeneratedDependencyProperty(DefaultValue = VoiceBubbleExitAnimation.Collapse)]
    public partial VoiceBubbleExitAnimation ExitAnimation { get; set; }

    /// <summary>Whether a thin line along the bottom shows the time left before success or failure dismisses.</summary>
    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsCountdownVisible { get; set; }

    /// <summary>How long a success stays, in milliseconds. Zero keeps it until the host hides it.</summary>
    [GeneratedDependencyProperty(DefaultValue = 3000)]
    public partial int SuccessDismissMilliseconds { get; set; }

    /// <summary>How long a failure stays, in milliseconds. Zero keeps it until the host hides it.</summary>
    [GeneratedDependencyProperty(DefaultValue = 6000)]
    public partial int FailureDismissMilliseconds { get; set; }

    /// <summary>Whether a failure offers to listen again.</summary>
    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool CanRetry { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "Yes")]
    public partial string ConfirmText { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "No")]
    public partial string DeclineText { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "Try again")]
    public partial string RetryText { get; set; }

    /// <summary>The confirm button when several accounts are accepted one by one.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Confirm")]
    public partial string ConfirmSelectedText { get; set; }

    /// <summary>The bold lead of a banner for a part of the request that can't be done.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Can't do")]
    public partial string NoticeTitle { get; set; }

    /// <summary>
    /// Whether listening shows a card with a wide waveform over two lines of the words heard so far.
    /// The card keeps one size while words arrive; older lines scroll up. The words stay, dimmed, while analyzing.
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool ShowsTranscript { get; set; }

    /// <summary>The final words heard so far. The host updates it as the live transcription arrives.</summary>
    [GeneratedDependencyProperty]
    public partial string? Transcript { get; set; }

    /// <summary>Words the transcription may still correct, shown faded after <see cref="Transcript"/>.</summary>
    [GeneratedDependencyProperty]
    public partial string? InterimTranscript { get; set; }

    /// <summary>Shown in place of the transcript until the first words arrive.</summary>
    [GeneratedDependencyProperty(DefaultValue = "Listening…")]
    public partial string ListeningText { get; set; }

    /// <summary>The question <see cref="VoiceBubbleState.Question"/> asks.</summary>
    [GeneratedDependencyProperty]
    public partial string? QuestionText { get; set; }

    /// <summary>A muted line under the choices.</summary>
    [GeneratedDependencyProperty]
    public partial string? QuestionCaption { get; set; }

    /// <summary>Whether the question has a filter box above its choices.</summary>
    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsQuestionSearchable { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "Search")]
    public partial string QuestionSearchPlaceholder { get; set; }

    /// <summary>Shown when the question has no choices, or none match the filter.</summary>
    [GeneratedDependencyProperty(DefaultValue = "No matches")]
    public partial string QuestionEmptyText { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "Cancel")]
    public partial string QuestionCancelText { get; set; }

    /// <summary>The success summary or failure reason.</summary>
    public VoiceBubbleText? Message { get; set; }

    /// <summary>The one-line confirmation question, used when there are no <see cref="ConfirmationSteps"/>.</summary>
    public VoiceBubbleText? ConfirmationText { get; set; }

    /// <summary>The plan's steps. Any step replaces the one-line question with a card that lists them.</summary>
    public IReadOnlyList<VoiceBubbleStep>? ConfirmationSteps { get; set; }

    /// <summary>
    /// The plan's steps grouped by the account they act on, each group under a header with the account's picture and
    /// name. When set it is shown instead of <see cref="ConfirmationSteps"/>.
    /// </summary>
    public IReadOnlyList<VoiceBubbleStepGroup>? ConfirmationGroups { get; set; }

    /// <summary>The user clicked the icon while listening.</summary>
    public event EventHandler? SendRequested;

    /// <summary>The user cancelled with the corner badge, No or Escape.</summary>
    public event EventHandler? CancelRequested;

    /// <summary>The user confirmed the plan.</summary>
    public event EventHandler? ConfirmRequested;

    /// <summary>The user asked to listen again after a failure.</summary>
    public event EventHandler? RetryRequested;

    /// <summary>The bubble hid itself: its countdown ran out or the user closed it.</summary>
    public event EventHandler? Dismissed;

    /// <summary>Raised when the exit animation has finished and nothing is drawn, so a host window can hide.</summary>
    public event EventHandler? ExitCompleted;

    /// <summary>The user typed in the question's filter box. The host debounces it and calls <see cref="SetChoices"/>.</summary>
    public event EventHandler<string>? QuestionFilterChanged;

    /// <summary>The user picked an answer to the question with a click, Enter or Space.</summary>
    public event EventHandler<VoiceBubbleChoice>? ChoiceSelected;

    /// <summary>
    /// Raised when the bubble starts moving to a new size, with that final size including the shadow margin,
    /// so a host window can fit the bubble. The size change animates for <see cref="ResizeDuration"/>.
    /// </summary>
    public event EventHandler<Size>? TargetSizeChanged;

    public static TimeSpan ResizeDuration => MorphDuration;

    private string _questionFilter = string.Empty;
    private int _lastQuestionIndex = -1;
    private List<VoiceBubbleChoice>? _choices;

    public WinoVoiceBubble()
    {
        InitializeComponent();
        var barStyle = (Style)Resources["WinoVoiceBubbleBarStyle"];
        _bars = new Rectangle[WideBarCount];
        for (var index = 0; index < _bars.Length; index++)
        {
            var bar = new Rectangle { Style = barStyle, CenterPoint = new Vector3(1.5f, (float)(BarFullHeight / 2), 0) };
            _bars[index] = bar;
            BarsPanel.Children.Add(bar);
        }

        Pill.Translation = new Vector3(0, 0, PillElevation);
        ApplyBarLayout(wide: false);
        ResetBars();
    }

    /// <summary>
    /// Re-reads <see cref="Message"/>, <see cref="ConfirmationText"/> and <see cref="ConfirmationSteps"/>
    /// for the current state and resizes the pill to fit.
    /// </summary>
    public void Refresh() => Present(State);

    void IVoiceBubbleView.Present(VoiceBubbleState state)
    {
        if (State == state)
            Refresh();
        else
            State = state;
    }

    /// <summary>
    /// Replaces the question's choices in one batch and selects the first one that can be picked. The list keeps its
    /// size, so the card never resizes while the user filters.
    /// </summary>
    public void SetChoices(List<VoiceBubbleChoice> choices)
    {
        _choices = choices;
        QuestionList.ItemsSource = choices;
        QuestionEmptyTextBlock.Visibility = choices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var first = choices.FindIndex(static choice => choice.IsItem);
        _lastQuestionIndex = first;
        QuestionList.SelectedIndex = first;
        if (first >= 0)
        {
            QuestionList.ScrollIntoView(choices[first]);
        }
    }

    /// <summary>Empties the filter box for a new question without raising <see cref="QuestionFilterChanged"/>.</summary>
    public void ClearQuestionFilter()
    {
        _questionFilter = string.Empty;
        QuestionSearchBox.Text = string.Empty;
    }

    /// <summary>
    /// Moves the voice bars while listening. Bands are loudness values from 0 to 1, lowest frequency first,
    /// in any count; the bars sample them evenly.
    /// </summary>
    public void UpdateBands(ReadOnlySpan<float> bands)
    {
        if (_presentedState != VoiceBubbleState.Listening || bands.Length == 0)
        {
            return;
        }

        var count = _visibleBarCount;
        var wide = count > CompactBarCount;
        for (var index = 0; index < count; index++)
        {
            // The wide waveform mirrors the bands around its centre so the loud low frequencies sit in the middle.
            var position = wide
                ? Math.Abs(index - (count - 1) / 2.0) / ((count - 1) / 2.0)
                : index / (double)count;
            var bandIndex = Math.Min(bands.Length - 1, (int)(position * bands.Length));
            SetBar(_bars[index], Math.Clamp(bands[bandIndex], 0f, 1f));
        }
    }

    partial void OnStateChanged(VoiceBubbleState newValue) => Present(newValue);

    partial void OnTranscriptChanged(string? newValue) => RefreshTranscript();

    partial void OnShowsTranscriptChanged(bool newValue) => RefreshTranscript();

    partial void OnInterimTranscriptChanged(string? newValue) => RefreshTranscript();

    partial void OnListeningTextChanged(string newValue) => RefreshTranscript();

    private void RefreshTranscript()
    {
        if (_isLoaded && _isShown && _presentedState is VoiceBubbleState.Listening or VoiceBubbleState.Analyzing
            && UpdateTranscript(_presentedState))
        {
            Resize(animate: true);
        }
    }

    /// <summary>Fills the transcript row for the state and returns whether the row appeared or went away.</summary>
    private bool UpdateTranscript(VoiceBubbleState state)
    {
        var heard = Transcript?.Trim();
        var pending = InterimTranscript?.Trim();
        var hasWords = !string.IsNullOrEmpty(heard) || !string.IsNullOrEmpty(pending);
        var visible = ShowsTranscript
            && (state == VoiceBubbleState.Listening || (state == VoiceBubbleState.Analyzing && hasWords));

        var changed = (TranscriptViewport.Visibility == Visibility.Visible) != visible;
        TranscriptViewport.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ApplyBarLayout(wide: visible);

        if (!visible)
        {
            StopStoryboard(ref _transcriptScrollStoryboard);
            TranscriptOffset.Y = 0;
            return changed;
        }

        TranscriptText.Inlines.Clear();
        if (!hasWords)
        {
            TranscriptText.Inlines.Add(new Run { Text = ListeningText });
            TranscriptText.Opacity = DimmedTranscriptOpacity;
        }
        else
        {
            if (!string.IsNullOrEmpty(heard))
            {
                TranscriptText.Inlines.Add(new Run { Text = heard });
            }

            if (!string.IsNullOrEmpty(pending))
            {
                TranscriptText.Inlines.Add(new Run
                {
                    Text = string.IsNullOrEmpty(heard) ? pending : " " + pending,
                    Foreground = CreatePendingBrush(),
                });
            }

            TranscriptText.Opacity = state == VoiceBubbleState.Analyzing ? DimmedTranscriptOpacity : 1;
        }

        ScrollTranscript(animate: !changed);
        return changed;
    }

    /// <summary>
    /// Fades the text's own theme colour. Looking a theme brush up from code would follow the app theme,
    /// not a RequestedTheme set on the bubble or its host.
    /// </summary>
    private SolidColorBrush? CreatePendingBrush()
    {
        if (TranscriptText.Foreground is not SolidColorBrush foreground)
        {
            return null;
        }

        var color = foreground.Color;
        color.A = (byte)(color.A / 2);
        return new SolidColorBrush(color);
    }

    /// <summary>Keeps the newest line at the bottom of the two visible lines.</summary>
    private void ScrollTranscript(bool animate)
    {
        TranscriptText.Measure(new Size(TranscriptText.Width, double.PositiveInfinity));
        var target = -Math.Max(0, TranscriptText.DesiredSize.Height - TranscriptViewport.Height);
        var from = TranscriptOffset.Y;
        if (Math.Abs(from - target) < 0.5)
        {
            return;
        }

        StopStoryboard(ref _transcriptScrollStoryboard);
        TranscriptOffset.Y = target;
        if (!animate || !_uiSettings.AnimationsEnabled)
        {
            return;
        }

        var storyboard = new Storyboard();
        storyboard.Children.Add(CreateSplineAnimation(TranscriptOffset, "Y", from, target, TranscriptScrollDuration, DecelerateSpline));
        _transcriptScrollStoryboard = storyboard;
        storyboard.Begin();
    }

    private void ApplyBarLayout(bool wide)
    {
        var count = wide ? WideBarCount : CompactBarCount;
        if (count == _visibleBarCount)
        {
            return;
        }

        _visibleBarCount = count;
        BarsPanel.Spacing = wide ? WideBarSpacing : CompactBarSpacing;
        for (var index = 0; index < _bars.Length; index++)
        {
            _bars[index].Visibility = index < count ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    partial void OnCanRetryChanged(bool newValue)
    {
        if (_presentedState == VoiceBubbleState.Failed)
        {
            Present(State);
        }
    }

    partial void OnIsCountdownVisibleChanged(bool newValue) => UpdateCountdownBar();

    private void ControlLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        if (State != VoiceBubbleState.Hidden)
        {
            Present(State);
        }
    }

    private void ControlUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _version++;
        StopStoryboard(ref _countdownStoryboard);
        StopStoryboard(ref _transitionStoryboard);
        StopStoryboard(ref _morphStoryboard);
        StopStoryboard(ref _transcriptScrollStoryboard);
        StopDots();
        _isShown = false;
        _presentedState = VoiceBubbleState.Hidden;
        BubbleHost.Visibility = Visibility.Collapsed;
        BubbleHost.Opacity = 0;
    }

    private void Present(VoiceBubbleState state)
    {
        if (!_isLoaded)
        {
            return;
        }

        var version = ++_version;
        StopStoryboard(ref _countdownStoryboard);

        if (state == VoiceBubbleState.Hidden)
        {
            if (_isShown)
            {
                PlayExit(version, _presentedState == VoiceBubbleState.Success ? ExitAnimation : VoiceBubbleExitAnimation.Fade);
            }

            _presentedState = VoiceBubbleState.Hidden;
            return;
        }

        var wasShown = _isShown;
        _presentedState = state;
        ApplyContent(state);
        Resize(animate: wasShown);

        if (!wasShown || _transitionStoryboard is not null)
        {
            PlayEnter(fromHidden: !wasShown);
        }

        _isShown = true;
        UpdateCancelBadge();
        StartCountdown(version, state);

        if (state == VoiceBubbleState.Confirmation)
        {
            var confirmButton = HasSteps ? StepsConfirmButton : InlineConfirmButton;
            confirmButton.Focus(FocusState.Programmatic);
        }
        else if (state == VoiceBubbleState.Question)
        {
            if (IsQuestionSearchable)
            {
                QuestionSearchBox.Focus(FocusState.Programmatic);
            }
            else
            {
                QuestionList.Focus(FocusState.Programmatic);
            }
        }
    }

    private bool HasSteps => ConfirmationGroups is { Count: > 0 } || ConfirmationSteps is { Count: > 0 };

    private void ApplyContent(VoiceBubbleState state)
    {
        switch (state)
        {
            case VoiceBubbleState.Listening:
                ResetBars();
                break;
            case VoiceBubbleState.Confirmation when HasSteps:
                BuildSteps();
                break;
            case VoiceBubbleState.Confirmation:
                SetText(InlineConfirmationText, ConfirmationText);
                break;
            case VoiceBubbleState.Success:
            case VoiceBubbleState.Failed:
                SetText(MessageText, Message);
                break;
            case VoiceBubbleState.Question:
                QuestionSearchBox.Visibility = IsQuestionSearchable ? Visibility.Visible : Visibility.Collapsed;
                break;
        }

        RetryButton.Visibility = state == VoiceBubbleState.Failed && CanRetry ? Visibility.Visible : Visibility.Collapsed;
        CompletionBadge.Opacity = 0;

        var stateName = state switch
        {
            VoiceBubbleState.Listening => nameof(ListeningState),
            VoiceBubbleState.Analyzing => nameof(AnalyzingState),
            VoiceBubbleState.Confirmation when HasSteps => nameof(ConfirmationStepsState),
            VoiceBubbleState.Confirmation => nameof(ConfirmationInlineState),
            VoiceBubbleState.Success => nameof(SuccessState),
            VoiceBubbleState.Failed => nameof(FailedState),
            VoiceBubbleState.Question => nameof(QuestionState),
            _ => nameof(HiddenState),
        };
        VisualStateManager.GoToState(this, stateName, false);
        UpdateTranscript(state);

        if (state == VoiceBubbleState.Analyzing)
        {
            StartDots();
        }
        else
        {
            StopDots();
        }
    }

    private void BuildSteps()
    {
        StepsList.Children.Clear();

        StepsConfirmButton.Content = ConfirmText;
        StepsConfirmButton.IsEnabled = true;

        if (ConfirmationGroups is { Count: > 0 } groups)
        {
            // With more than one account to act on, each account is accepted on its own and one button confirms them.
            var isPerAccount = groups.Count(static group => group.CanSelect) > 1;
            if (isPerAccount)
            {
                StepsConfirmButton.Content = ConfirmSelectedText;
                UpdateSelectedConfirm();
            }

            foreach (var group in groups)
            {
                var section = new StackPanel { Spacing = 4 };
                if (CreateGroupHeader(group, isPerAccount && group.CanSelect) is { } header)
                {
                    section.Children.Add(header);
                }

                // Steps count within their account; a lone step needs no number.
                for (var index = 0; index < group.Steps.Count; index++)
                {
                    section.Children.Add(CreateStepRow(group.Steps[index], group.Steps.Count == 1 ? null : index + 1));
                }

                foreach (var notice in group.Notices)
                {
                    section.Children.Add(CreateNotice(notice));
                }

                StepsList.Children.Add(section);
            }

            return;
        }

        var steps = ConfirmationSteps;
        if (steps is null)
        {
            return;
        }

        var rows = new StackPanel { Spacing = 4 };
        for (var index = 0; index < steps.Count; index++)
        {
            rows.Children.Add(CreateStepRow(steps[index], steps[index].Number ?? index + 1));
        }

        StepsList.Children.Add(rows);
    }

    private void UpdateSelectedConfirm()
        => StepsConfirmButton.IsEnabled = ConfirmationGroups?.Any(static group => group.CanSelect && group.IsSelected) == true;

    private FrameworkElement CreateNotice(string message)
    {
        // Drawn from XAML so the warning brushes follow the bubble's RequestedTheme. Its text block is the grid's second child.
        var banner = (Border)((DataTemplate)Resources["WinoVoiceBubbleNoticeTemplate"]).LoadContent();
        var text = (TextBlock)((Grid)banner.Child).Children[1];
        text.Inlines.Add(new Run { Text = NoticeTitle, FontWeight = FontWeights.SemiBold });
        text.Inlines.Add(new Run { Text = " " + message });
        AutomationProperties.SetName(banner, $"{NoticeTitle}. {message}");
        return banner;
    }

    private FrameworkElement? CreateGroupHeader(VoiceBubbleStepGroup group, bool isSelectable)
    {
        if (group.Kind == VoiceBubbleStepGroupKind.None)
        {
            return null;
        }

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (isSelectable)
        {
            var accept = new CheckBox
            {
                IsChecked = group.IsSelected,
                MinWidth = 0,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(accept, group.Title ?? string.Empty);
            accept.Checked += (_, _) => { group.IsSelected = true; UpdateSelectedConfirm(); };
            accept.Unchecked += (_, _) => { group.IsSelected = false; UpdateSelectedConfirm(); };
            Grid.SetColumn(accept, 2);
            header.Children.Add(accept);
        }

        header.Children.Add(CreateGroupIcon(group));

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(texts, 1);
        if (!string.IsNullOrWhiteSpace(group.Title))
        {
            texts.Children.Add(new TextBlock
            {
                Text = group.Title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        if (!string.IsNullOrWhiteSpace(group.Subtitle))
        {
            texts.Children.Add(new TextBlock
            {
                Text = group.Subtitle,
                FontSize = 12,
                Opacity = 0.7,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        header.Children.Add(texts);
        AutomationProperties.SetName(header, string.Join(", ", new[] { group.Title, group.Subtitle }.Where(static text => !string.IsNullOrWhiteSpace(text))));
        return header;
    }

    private FrameworkElement CreateGroupIcon(VoiceBubbleStepGroup group)
    {
        if (group.Kind == VoiceBubbleStepGroupKind.Unresolved)
        {
            return (FrameworkElement)((DataTemplate)Resources["WinoVoiceBubbleWarningGlyphTemplate"]).LoadContent();
        }

        // Stacked pictures overlap by a third, so three accounts fit in the space of two.
        var stack = new Grid { VerticalAlignment = VerticalAlignment.Center };
        var accounts = group.Accounts.Take(group.Kind == VoiceBubbleStepGroupKind.Accounts ? 3 : 1).ToList();
        for (var index = accounts.Count - 1; index >= 0; index--)
        {
            stack.Children.Add(new WinoAccountIcon
            {
                Account = accounts[index],
                IconSize = AccountIconSize,
                Margin = new Thickness(index * AccountIconSize * 0.66, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            });
        }

        stack.Width = accounts.Count == 0 ? AccountIconSize : AccountIconSize * (1 + ((accounts.Count - 1) * 0.66));
        return stack;
    }

    /// <param name="number">The number shown before the step, or null for a step that stands alone.</param>
    private static Grid CreateStepRow(VoiceBubbleStep step, int? number)
    {
        var row = new Grid { ColumnSpacing = number is null ? 0 : 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = number is null ? new GridLength(0) : new GridLength(14) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (number is int shown)
        {
            row.Children.Add(new TextBlock
            {
                Text = shown.ToString(System.Globalization.CultureInfo.CurrentCulture),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Opacity = 0.6,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }

        var body = new StackPanel();
        Grid.SetColumn(body, 1);
        var text = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        SetText(text, step.Text);
        body.Children.Add(text);

        if (!string.IsNullOrWhiteSpace(step.Detail))
        {
            body.Children.Add(new TextBlock
            {
                Text = step.Detail,
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        row.Children.Add(body);
        return row;
    }

    private static void SetText(TextBlock target, VoiceBubbleText? text)
    {
        target.Inlines.Clear();
        if (text is null)
        {
            return;
        }

        foreach (var span in text.Spans)
        {
            var run = new Run { Text = span.Text };
            if (span.IsStrong)
            {
                run.FontWeight = FontWeights.SemiBold;
            }

            target.Inlines.Add(run);
        }
    }

    private void Resize(bool animate)
    {
        ContentRoot.Width = double.NaN;
        ContentRoot.Height = double.NaN;
        ContentRoot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = ContentRoot.DesiredSize;

        // The content keeps its target size while the pill animates, so text never rewraps mid-way.
        ContentRoot.Width = desired.Width;
        ContentRoot.Height = desired.Height;

        var width = desired.Width + PillBorder;
        var height = desired.Height + PillBorder;
        var fromWidth = Pill.Width;
        var fromHeight = Pill.Height;

        StopStoryboard(ref _morphStoryboard);
        Pill.Width = width;
        Pill.Height = height;

        var margin = BubbleHost.Margin;
        TargetSizeChanged?.Invoke(this, new Size(width + margin.Left + margin.Right, height + margin.Top + margin.Bottom));

        if (!animate || !_uiSettings.AnimationsEnabled || double.IsNaN(fromWidth) || double.IsNaN(fromHeight))
        {
            return;
        }

        var storyboard = new Storyboard();
        storyboard.Children.Add(CreateSplineAnimation(Pill, "Width", fromWidth, width, MorphDuration, DecelerateSpline, dependent: true));
        storyboard.Children.Add(CreateSplineAnimation(Pill, "Height", fromHeight, height, MorphDuration, DecelerateSpline, dependent: true));
        _morphStoryboard = storyboard;
        storyboard.Begin();
    }

    private void PlayEnter(bool fromHidden)
    {
        StopStoryboard(ref _transitionStoryboard);
        BubbleHost.Visibility = Visibility.Visible;
        BubbleHost.RenderTransformOrigin = new Point(1, 1);

        if (fromHidden)
        {
            BubbleHost.Opacity = 0;
            BubbleTransform.ScaleX = 0.92;
            BubbleTransform.ScaleY = 0.92;
            BubbleTransform.TranslateY = 12;
        }

        if (!_uiSettings.AnimationsEnabled)
        {
            ResetHostTransform();
            BubbleHost.Opacity = 1;
            return;
        }

        var storyboard = new Storyboard();
        storyboard.Children.Add(CreateSplineAnimation(BubbleHost, "Opacity", null, 1, TimeSpan.FromMilliseconds(160), DecelerateSpline));
        storyboard.Children.Add(CreateSplineAnimation(BubbleTransform, "ScaleX", null, 1, TimeSpan.FromMilliseconds(300), DecelerateSpline));
        storyboard.Children.Add(CreateSplineAnimation(BubbleTransform, "ScaleY", null, 1, TimeSpan.FromMilliseconds(300), DecelerateSpline));
        storyboard.Children.Add(CreateSplineAnimation(BubbleTransform, "TranslateY", null, 0, TimeSpan.FromMilliseconds(300), DecelerateSpline));
        _transitionStoryboard = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (ReferenceEquals(_transitionStoryboard, storyboard))
            {
                _transitionStoryboard = null;
            }
        };
        storyboard.Begin();
    }

    private void PlayExit(int version, VoiceBubbleExitAnimation mode)
    {
        StopStoryboard(ref _transitionStoryboard);
        StopDots();
        UpdateCancelBadge();

        if (!_uiSettings.AnimationsEnabled)
        {
            FinishExit(version);
            return;
        }

        var storyboard = new Storyboard();
        switch (mode)
        {
            case VoiceBubbleExitAnimation.Collapse:
                VisualStateManager.GoToState(this, nameof(CollapsingState), false);
                Resize(animate: true);
                BubbleHost.RenderTransformOrigin = new Point(0.5, 0.5);
                var collapseStart = TimeSpan.FromMilliseconds(520);
                storyboard.Children.Add(CreateSplineAnimation(BubbleTransform, "ScaleX", null, 0.55, TimeSpan.FromMilliseconds(260), AccelerateSpline, begin: collapseStart));
                storyboard.Children.Add(CreateSplineAnimation(BubbleTransform, "ScaleY", null, 0.55, TimeSpan.FromMilliseconds(260), AccelerateSpline, begin: collapseStart));
                storyboard.Children.Add(CreateSplineAnimation(BubbleHost, "Opacity", null, 0, TimeSpan.FromMilliseconds(220), AccelerateSpline, begin: collapseStart + TimeSpan.FromMilliseconds(40)));
                break;
            case VoiceBubbleExitAnimation.Slide:
                storyboard.Children.Add(CreateSplineAnimation(BubbleTransform, "TranslateY", null, 20, TimeSpan.FromMilliseconds(280), AccelerateSpline));
                storyboard.Children.Add(CreateSplineAnimation(BubbleHost, "Opacity", null, 0, TimeSpan.FromMilliseconds(200), AccelerateSpline, begin: TimeSpan.FromMilliseconds(60)));
                break;
            default:
                storyboard.Children.Add(CreateSplineAnimation(BubbleTransform, "ScaleX", null, 0.97, TimeSpan.FromMilliseconds(180), AccelerateSpline));
                storyboard.Children.Add(CreateSplineAnimation(BubbleTransform, "ScaleY", null, 0.97, TimeSpan.FromMilliseconds(180), AccelerateSpline));
                storyboard.Children.Add(CreateSplineAnimation(BubbleHost, "Opacity", null, 0, TimeSpan.FromMilliseconds(180), AccelerateSpline));
                break;
        }

        _transitionStoryboard = storyboard;
        storyboard.Completed += (_, _) => FinishExit(version);
        storyboard.Begin();
    }

    private void FinishExit(int version)
    {
        if (version != _version)
        {
            return;
        }

        StopStoryboard(ref _transitionStoryboard);
        StopStoryboard(ref _morphStoryboard);
        _isShown = false;
        BubbleHost.Visibility = Visibility.Collapsed;
        BubbleHost.Opacity = 0;
        ResetHostTransform();
        VisualStateManager.GoToState(this, nameof(HiddenState), false);
        ExitCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void ResetHostTransform()
    {
        BubbleTransform.ScaleX = 1;
        BubbleTransform.ScaleY = 1;
        BubbleTransform.TranslateY = 0;
        BubbleHost.RenderTransformOrigin = new Point(1, 1);
    }

    private void StartCountdown(int version, VoiceBubbleState state)
    {
        var milliseconds = state switch
        {
            VoiceBubbleState.Success => SuccessDismissMilliseconds,
            VoiceBubbleState.Failed => FailureDismissMilliseconds,
            _ => 0,
        };

        CountdownScale.ScaleX = 1;
        UpdateCountdownBar();
        if (milliseconds <= 0)
        {
            return;
        }

        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
        };
        Storyboard.SetTarget(animation, CountdownScale);
        Storyboard.SetTargetProperty(animation, "ScaleX");
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (version == _version)
            {
                DismissSelf();
            }
        };
        _countdownStoryboard = storyboard;
        storyboard.Begin();
        if (_isPointerOver)
        {
            storyboard.Pause();
        }
    }

    private void UpdateCountdownBar()
    {
        var counts = _presentedState is VoiceBubbleState.Success or VoiceBubbleState.Failed;
        CountdownBar.Opacity = counts && IsCountdownVisible ? 0.75 : 0;
    }

    private void DismissSelf()
    {
        if (State == VoiceBubbleState.Hidden)
        {
            return;
        }

        State = VoiceBubbleState.Hidden;
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void StartDots()
    {
        if (!_isDotsRunning && _uiSettings.AnimationsEnabled)
        {
            DotsStoryboard.Begin();
            _isDotsRunning = true;
        }
    }

    private void StopDots()
    {
        if (_isDotsRunning)
        {
            DotsStoryboard.Stop();
            _isDotsRunning = false;
        }
    }

    private void ResetBars()
    {
        foreach (var bar in _bars)
        {
            SetBar(bar, 0);
        }
    }

    private static void SetBar(Rectangle bar, float band)
    {
        var scale = (BarRestHeight + band * BarRange) / BarFullHeight;
        bar.Scale = new Vector3(1, (float)scale, 1);
        bar.Opacity = 0.45 + 0.55 * band;
    }

    private void UpdateCancelBadge()
    {
        var visible = _isPointerOver && _presentedState is VoiceBubbleState.Listening or VoiceBubbleState.Analyzing;
        CancelButton.Opacity = visible ? 1 : 0;
        CancelButton.IsHitTestVisible = visible;
    }

    private static DoubleAnimationUsingKeyFrames CreateSplineAnimation(
        DependencyObject target,
        string property,
        double? from,
        double to,
        TimeSpan duration,
        KeySpline spline,
        bool dependent = false,
        TimeSpan? begin = null)
    {
        var animation = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = dependent };
        if (begin is { } beginTime)
        {
            animation.BeginTime = beginTime;
        }

        if (from is { } start)
        {
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = start });
        }

        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(duration),
            Value = to,
            KeySpline = new KeySpline { ControlPoint1 = spline.ControlPoint1, ControlPoint2 = spline.ControlPoint2 },
        });
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    private static void StopStoryboard(ref Storyboard? storyboard)
    {
        storyboard?.Stop();
        storyboard = null;
    }

    private void PillContentSizeChanged(object sender, SizeChangedEventArgs e) =>
        PillContent.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };

    private void PillPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        UpdateCancelBadge();
        _countdownStoryboard?.Pause();
    }

    private void PillPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        UpdateCancelBadge();
        _countdownStoryboard?.Resume();
    }

    private void ControlKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        switch (_presentedState)
        {
            case VoiceBubbleState.Listening:
            case VoiceBubbleState.Analyzing:
            case VoiceBubbleState.Confirmation:
            case VoiceBubbleState.Question:
                CancelRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
            case VoiceBubbleState.Success:
            case VoiceBubbleState.Failed:
                DismissSelf();
                e.Handled = true;
                break;
        }
    }

    private void IconButtonClicked(object sender, RoutedEventArgs e)
    {
        if (_presentedState == VoiceBubbleState.Listening)
        {
            SendRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CancelButtonClicked(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);

    private void ConfirmButtonClicked(object sender, RoutedEventArgs e) => ConfirmRequested?.Invoke(this, EventArgs.Empty);

    private void DeclineButtonClicked(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);

    private void RetryButtonClicked(object sender, RoutedEventArgs e) => RetryRequested?.Invoke(this, EventArgs.Empty);

    private void CloseButtonClicked(object sender, RoutedEventArgs e) => DismissSelf();

    private void QuestionSearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        // TextChanged arrives asynchronously, also for text the control set itself; only real edits reach the host.
        var text = QuestionSearchBox.Text ?? string.Empty;
        if (string.Equals(text, _questionFilter, StringComparison.Ordinal))
        {
            return;
        }

        _questionFilter = text;
        QuestionFilterChanged?.Invoke(this, text);
    }

    private void QuestionSearchBoxPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                MoveQuestionSelection(1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                MoveQuestionSelection(-1);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                PickSelectedChoice();
                e.Handled = true;
                break;
        }
    }

    private void MoveQuestionSelection(int step)
    {
        if (_choices is not { Count: > 0 } choices)
        {
            return;
        }

        var index = QuestionList.SelectedIndex;
        do
        {
            index += step;
        }
        while (index >= 0 && index < choices.Count && choices[index].IsHeader);

        if (index < 0 || index >= choices.Count)
        {
            return;
        }

        QuestionList.SelectedIndex = index;
        QuestionList.ScrollIntoView(choices[index]);
    }

    private void PickSelectedChoice()
    {
        if (QuestionList.SelectedItem is VoiceBubbleChoice { IsItem: true } choice)
        {
            ChoiceSelected?.Invoke(this, choice);
        }
    }

    private void QuestionListItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VoiceBubbleChoice { IsItem: true } choice)
        {
            ChoiceSelected?.Invoke(this, choice);
        }
    }

    /// <summary>An account header can't be selected: the selection moves on in the direction it came from.</summary>
    private void QuestionListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = QuestionList.SelectedIndex;
        if (QuestionList.SelectedItem is VoiceBubbleChoice { IsHeader: true } && _choices is { } choices)
        {
            var step = index < _lastQuestionIndex ? -1 : 1;
            var next = index;
            do
            {
                next += step;
            }
            while (next >= 0 && next < choices.Count && choices[next].IsHeader);

            QuestionList.SelectedIndex = next >= 0 && next < choices.Count ? next : _lastQuestionIndex;
            return;
        }

        _lastQuestionIndex = index;
    }

    private void QuestionListContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is VoiceBubbleChoice choice)
        {
            args.ItemContainer.IsHitTestVisible = choice.IsItem;
            args.ItemContainer.IsTabStop = choice.IsItem;
        }
    }
}
