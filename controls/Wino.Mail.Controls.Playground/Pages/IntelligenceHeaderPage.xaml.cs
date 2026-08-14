using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.Core.IntelligenceHeader;
using Wino.Mail.Controls.Core.IntelligenceTileBar;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class IntelligenceHeaderPage : Page
{
    private const string SampleSummary =
        "Nordic Supply sent the renewed 12-month framework agreement. Terms are unchanged except a 4% "
        + "adjustment on the logistics line, and they need the countersigned copy before the current "
        + "contract lapses.";

    private static readonly WinoIntelligenceReply[] SampleReplies =
    [
        new("Polite", "Thanks Claudia — I'll review the agreement and return the signed copy before Friday."),
        new("Formal", "Received with thanks. The countersigned copy will follow on Thursday, ahead of the deadline."),
        new("Shorter", "Signed copy comes back Thursday. Let's discuss the 4% on a call first."),
    ];

    private static readonly WinoIntelligenceSimilarMailItem[] SampleSimilarMail =
    [
        new() { DisplayName = "Claudia Denis", Initials = "CD", Subject = "Re: Vendor agreement — revised draft v4", Meta = "Claudia Denis · 2 Aug", ScoreText = "94%", Tag = Guid.NewGuid() },
        new() { DisplayName = "Legal Group", Initials = "LG", Subject = "Countersignature process for suppliers", Meta = "Legal Group · 18 Jul", ScoreText = "81%", Tag = Guid.NewGuid() },
        new() { DisplayName = "Marta Rossi", Initials = "MR", Subject = "Payment terms alignment", Meta = "Marta Rossi · 9 Jul", ScoreText = "76%", Tag = Guid.NewGuid() },
    ];

    private readonly DispatcherTimer _hostTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private readonly DispatcherTimer _processingTimer = new() { Interval = TimeSpan.FromMilliseconds(1400) };

    private Guid? _pendingSummaryId;
    private Guid? _pendingRepliesId;
    private Guid? _pendingSimilarId;
    private WinoIntelligenceFeature _pendingFeature;
    private bool _isSynchronizing;
    private bool _isInitialized;
    private int _messageIndex;

    public ObservableCollection<IntelligenceHeaderVariantOption> VariantOptions { get; } =
    [
        new("Processed · both signals", WinoIntelligenceProcessingState.Processed, hasDeadline: true, needsReply: true),
        new("Processed · deadline only", WinoIntelligenceProcessingState.Processed, hasDeadline: true, needsReply: false),
        new("Processed · needs reply only", WinoIntelligenceProcessingState.Processed, hasDeadline: false, needsReply: true),
        new("Processed · no signals", WinoIntelligenceProcessingState.Processed, hasDeadline: false, needsReply: false),
        new("Not processed", WinoIntelligenceProcessingState.NotProcessed, hasDeadline: false, needsReply: false),
        new("Queued", WinoIntelligenceProcessingState.Queued, hasDeadline: false, needsReply: false),
        new("Processing", WinoIntelligenceProcessingState.Processing, hasDeadline: false, needsReply: false),
        new("Processing failed", WinoIntelligenceProcessingState.Failed, hasDeadline: false, needsReply: false),
        new("Unavailable", WinoIntelligenceProcessingState.Unavailable, hasDeadline: false, needsReply: false),
    ];

    public ObservableCollection<string> EventTrace { get; } = [];

    public IntelligenceHeaderPage()
    {
        InitializeComponent();

        // A ToggleSwitch with IsOn set in markup raises Toggled while the page is still being
        // parsed, when the switches further down the file do not exist yet.
        _isInitialized = true;

        _hostTimer.Tick += OnHostTimerTick;
        _processingTimer.Tick += OnProcessingTimerTick;

        HeaderVariantComboBox.SelectedIndex = 0;

        // The control never invents a language list; the real host supplies one from the user's
        // preferences. Without this the translate panel renders two empty combo boxes.
        IntelligenceHeader.TranslationLanguages = new WinoIntelligenceLanguageOption[]
        {
            new WinoIntelligenceLanguageOption(string.Empty, "Detect language"),
            new WinoIntelligenceLanguageOption("en-US", "English"),
            new WinoIntelligenceLanguageOption("tr-TR", "Turkish"),
            new WinoIntelligenceLanguageOption("de-DE", "German"),
            new WinoIntelligenceLanguageOption("fr-FR", "French"),
        };
        IntelligenceHeader.SelectedSourceLanguage = string.Empty;
        IntelligenceHeader.SelectedTargetLanguage = "en-US";

        IntelligenceHeader.ContentKey = Guid.NewGuid().ToString();
        IntelligenceHeader.IntelligenceTiles = new WinoIntelligenceTile[]
        {
            new(WinoIntelligenceTileKind.Deadline, "\uE787", "Review: Friday, 5:00 PM", "Review deadline: Friday, 5:00 PM"),
            new(WinoIntelligenceTileKind.NeedsReply, "\uE97A", "Needs reply", "This message needs a reply"),
            new(WinoIntelligenceTileKind.Priority, "\uE7BA", "Urgent", "Urgent priority", isWarning: true),
            new(WinoIntelligenceTileKind.SmartLabel, "\uE8EC", "Finance", "Finance"),
            new(WinoIntelligenceTileKind.BriefingFact, "\uE946", string.Empty, "Action required, urgent: countersign the agreement", true),
        };
        ApplyVariant(VariantOptions[0]);
    }

    private void ApplyVariant(IntelligenceHeaderVariantOption option)
    {
        _isSynchronizing = true;
        try
        {
            IntelligenceHeader.ProcessingState = option.ProcessingState;
            IntelligenceHeader.DeadlineText = option.HasDeadline ? "Fri 17:00" : string.Empty;
            IntelligenceHeader.DeadlineDetailText = option.HasDeadline
                ? "Countersigned copy due Friday, 14 Aug at 17:00"
                : string.Empty;
            IntelligenceHeader.NeedsReply = option.NeedsReply;
            IntelligenceHeader.NeedsReplyDetailText = option.NeedsReply
                ? "Claudia is waiting on your confirmation"
                : string.Empty;

            ExpandedToggle.IsOn = IntelligenceHeader.IsExpanded;
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    private void HeaderVariantChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizing) return;
        if (HeaderVariantComboBox.SelectedItem is not IntelligenceHeaderVariantOption option) return;

        _processingTimer.Stop();
        ApplyVariant(option);
        AddTrace($"Variant applied: {option.Title}");
    }

    private void ExpandedToggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || _isSynchronizing) return;
        IntelligenceHeader.IsExpanded = ExpandedToggle.IsOn;
    }

    private void AvailabilityToggled(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;

        IntelligenceHeader.IsAddToCalendarAvailable = AddToCalendarToggle.IsOn;
        IntelligenceHeader.IsTranslateAvailable = TranslateToggle.IsOn;
        IntelligenceHeader.IsFindSimilarMailAvailable = FindSimilarToggle.IsOn;
    }

    /// <summary>
    /// Stands in for the app's on-demand indexing job: accept immediately as Queued, move to
    /// Processing, then land on Processed. The control never advances this itself.
    /// </summary>
    private void HeaderProcessRequested(object? sender, EventArgs e)
    {
        AddTrace("Process requested");
        ResultSummary.Text = "Host queued the message for processing. The control is only reflecting state.";

        IntelligenceHeader.ProcessingState = WinoIntelligenceProcessingState.Queued;
        _processingTimer.Start();
    }

    private void OnProcessingTimerTick(object? sender, object e)
    {
        switch (IntelligenceHeader.ProcessingState)
        {
            case WinoIntelligenceProcessingState.Queued:
                IntelligenceHeader.ProcessingState = WinoIntelligenceProcessingState.Processing;
                AddTrace("Processing started");
                break;

            case WinoIntelligenceProcessingState.Processing:
                _processingTimer.Stop();
                IntelligenceHeader.DeadlineText = "Fri 17:00";
                IntelligenceHeader.DeadlineDetailText = "Countersigned copy due Friday, 14 Aug at 17:00";
                IntelligenceHeader.NeedsReply = true;
                IntelligenceHeader.NeedsReplyDetailText = "Claudia is waiting on your confirmation";
                IntelligenceHeader.ProcessingState = WinoIntelligenceProcessingState.Processed;
                AddTrace("Processing finished · insights available");
                ResultSummary.Text = "Insights unlocked. Suggested replies and find-similar are now offered.";
                SyncVariantComboToState();
                break;

            default:
                _processingTimer.Stop();
                break;
        }
    }

    private void SyncVariantComboToState()
    {
        var match = VariantOptions.FirstOrDefault(x =>
            x.ProcessingState == IntelligenceHeader.ProcessingState &&
            x.HasDeadline == !string.IsNullOrEmpty(IntelligenceHeader.DeadlineText) &&
            x.NeedsReply == IntelligenceHeader.NeedsReply);
        if (match is null) return;

        _isSynchronizing = true;
        try
        {
            HeaderVariantComboBox.SelectedItem = match;
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    private void HeaderFeatureRequested(object? sender, WinoIntelligenceRequestEventArgs e)
    {
        _pendingFeature = e.Feature;
        if (e.Feature == WinoIntelligenceFeature.Summary) _pendingSummaryId = e.RequestId;
        else if (e.Feature == WinoIntelligenceFeature.SuggestedReplies) _pendingRepliesId = e.RequestId;
        else _pendingSimilarId = e.RequestId;

        AddTrace($"Requested · {e.Feature} · {e.RequestId:N}");
        ResultSummary.Text = $"Host received a {e.Feature} request.";

        if (AutoCompleteToggle.IsOn) _hostTimer.Start();
    }

    private void HeaderFeatureCancelRequested(object? sender, WinoIntelligenceCancelRequestedEventArgs e)
    {
        ClearPending(e.Feature);
        _hostTimer.Stop();
        AddTrace($"Cancelled · {e.Feature} · {e.RequestId:N}");
        ResultSummary.Text = "The user cancelled the request. The host should cancel its own work.";
    }

    private void OnHostTimerTick(object? sender, object e)
    {
        _hostTimer.Stop();
        CompletePending();
    }

    private void CompletePendingClicked(object sender, RoutedEventArgs e) => CompletePending();

    private void CompletePending()
    {
        if (_pendingFeature == WinoIntelligenceFeature.Summary)
        {
            if (_pendingSummaryId is not { } id)
            {
                AddTrace("No pending summary request.");
                return;
            }

            var applied = IntelligenceHeader.CompleteSummary(id, SampleSummary);
            _pendingSummaryId = null;
            AddTrace($"CompleteSummary returned {applied}");
            ResultSummary.Text = applied
                ? "Summary applied. The trigger button is replaced by the result."
                : "Summary rejected as stale — the host must not report this to the user.";
            return;
        }

        if (_pendingFeature == WinoIntelligenceFeature.FindSimilarMail)
        {
            if (_pendingSimilarId is not { } similarId)
            {
                AddTrace("No pending similar-mail request.");
                return;
            }

            var similarApplied = IntelligenceHeader.CompleteSimilarMail(similarId, SampleSimilarMail);
            _pendingSimilarId = null;
            AddTrace($"CompleteSimilarMail returned {similarApplied}");
            ResultSummary.Text = similarApplied ? "Similar mail applied. Pick a row to navigate." : "Similar mail rejected as stale.";
            return;
        }

        if (_pendingRepliesId is not { } repliesId)
        {
            AddTrace("No pending replies request.");
            return;
        }

        var repliesApplied = IntelligenceHeader.CompleteSuggestedReplies(repliesId, SampleReplies);
        _pendingRepliesId = null;
        AddTrace($"CompleteSuggestedReplies returned {repliesApplied}");
        ResultSummary.Text = repliesApplied
            ? "Replies applied. Click one to raise SuggestedReplyChosen."
            : "Replies rejected as stale — the host must not report this to the user.";
    }

    private void FailPendingClicked(object sender, RoutedEventArgs e)
    {
        _hostTimer.Stop();

        var id = _pendingFeature switch
        {
            WinoIntelligenceFeature.Summary => _pendingSummaryId,
            WinoIntelligenceFeature.SuggestedReplies => _pendingRepliesId,
            _ => _pendingSimilarId,
        };
        if (id is not { } requestId)
        {
            AddTrace("No pending request to fail.");
            return;
        }

        var handled = IntelligenceHeader.FailRequest(requestId);
        ClearPending(_pendingFeature);

        AddTrace($"FailRequest returned {handled}");
        ResultSummary.Text = handled
            ? "Request failed. The control returned to idle and rendered no error — the shell owns that message."
            : "Failure rejected as stale — the shell must stay silent.";
    }

    private void CompleteStaleClicked(object sender, RoutedEventArgs e)
    {
        var applied = IntelligenceHeader.CompleteSummary(Guid.NewGuid(), "This result belongs to another message.");
        AddTrace($"Stale CompleteSummary returned {applied}");
        ResultSummary.Text = "A stale id changes nothing and returns false, so the host can stay silent.";
    }

    private void SwapMessageClicked(object sender, RoutedEventArgs e)
    {
        _hostTimer.Stop();
        _processingTimer.Stop();
        _pendingSummaryId = null;
        _pendingRepliesId = null;
        _pendingSimilarId = null;
        _messageIndex++;

        // Assigning a new content key resets the control, which is what invalidates in-flight ids.
        IntelligenceHeader.ContentKey = Guid.NewGuid().ToString();

        var variant = VariantOptions[_messageIndex % VariantOptions.Count];
        _isSynchronizing = true;
        try
        {
            HeaderVariantComboBox.SelectedItem = variant;
        }
        finally
        {
            _isSynchronizing = false;
        }
        ApplyVariant(variant);

        AddTrace($"Content swapped · variant {variant.Title}");
        ResultSummary.Text = "New content key. Any in-flight request is now stale and will be rejected.";
    }

    private void ResetClicked(object sender, RoutedEventArgs e)
    {
        _hostTimer.Stop();
        _processingTimer.Stop();
        _pendingSummaryId = null;
        _pendingRepliesId = null;
        _pendingSimilarId = null;
        IntelligenceHeader.Reset();

        _isSynchronizing = true;
        try
        {
            ExpandedToggle.IsOn = false;
        }
        finally
        {
            _isSynchronizing = false;
        }

        AddTrace("Reset");
        ResultSummary.Text = "Reset cleared every generated result and in-flight request.";
    }

    private void HeaderActionInvoked(object? sender, WinoIntelligenceActionEventArgs e)
    {
        AddTrace($"Action · {e.Action}");
        ResultSummary.Text = $"The host would now handle {e.Action}.";
    }

    private void HeaderSuggestedReplyChosen(object? sender, WinoIntelligenceReplyChosenEventArgs e)
    {
        AddTrace($"Reply picked [{e.Index}] {e.Reply.Tone}");
        ResultSummary.Text = $"Reply chosen: {e.Reply.Text}";
    }

    private void HeaderSimilarMailChosen(object? sender, WinoIntelligenceSimilarMailChosenEventArgs e)
    {
        AddTrace($"Similar mail picked [{e.Index}] {e.Item.Subject}");
        ResultSummary.Text = $"The host would navigate to: {e.Item.Subject}";
    }

    private void HeaderExpansionChanged(object? sender, bool isExpanded)
    {
        _isSynchronizing = true;
        try
        {
            ExpandedToggle.IsOn = isExpanded;
        }
        finally
        {
            _isSynchronizing = false;
        }

        AddTrace($"Expansion changed: {isExpanded}");
    }

    private void ClearPending(WinoIntelligenceFeature feature)
    {
        if (feature == WinoIntelligenceFeature.Summary) _pendingSummaryId = null;
        else if (feature == WinoIntelligenceFeature.SuggestedReplies) _pendingRepliesId = null;
        else _pendingSimilarId = null;
    }

    private void AddTrace(string message)
    {
        EventTrace.Insert(0, $"{DateTime.Now:T}  {message}");
        while (EventTrace.Count > 12) EventTrace.RemoveAt(EventTrace.Count - 1);
    }
}

public sealed class IntelligenceHeaderVariantOption(
    string title,
    WinoIntelligenceProcessingState processingState,
    bool hasDeadline,
    bool needsReply)
{
    public IntelligenceHeaderVariantOption()
        : this(string.Empty, WinoIntelligenceProcessingState.NotProcessed, false, false)
    {
    }

    public string Title { get; set; } = title;

    public WinoIntelligenceProcessingState ProcessingState { get; set; } = processingState;

    public bool HasDeadline { get; set; } = hasDeadline;

    public bool NeedsReply { get; set; } = needsReply;
}
