#if WINRT_EXPOSED
using WinRT;
#endif

namespace Wino.Mail.Controls.Core.IntelligenceHeader;

/// <summary>
/// Generated features that the header requests from its host. Passive signals such as the
/// deadline or the needs-reply state are not listed here because they are never requested.
/// </summary>
public enum WinoIntelligenceFeature
{
    Summary,
    SuggestedReplies,
    FindSimilarMail,
}

public enum WinoIntelligenceFeatureState
{
    Idle,
    Busy,
    Done,
}

/// <summary>
/// Whether the host has produced intelligence for the current content yet. Mirrors the shape of
/// the app's per-message indexing state so a host can map it one-to-one, without the control
/// taking a dependency on the domain enum.
/// </summary>
/// <remarks>
/// This is host-driven throughout. The control never advances it on its own: clicking the process
/// button only raises <see cref="WinoIntelligenceHeader.ProcessRequested"/>, and the host moves the
/// state as its own job progresses.
/// </remarks>
public enum WinoIntelligenceProcessingState
{
    /// <summary>Not processed yet, and processing can be requested.</summary>
    NotProcessed,

    /// <summary>Queued for processing; waiting for a worker.</summary>
    Queued,

    /// <summary>Inference is running for this content.</summary>
    Processing,

    /// <summary>Processed. Signals and generated features are available.</summary>
    Processed,

    /// <summary>The last processing attempt failed; the user may retry.</summary>
    Failed,

    /// <summary>This content can never be processed, so no action is offered.</summary>
    Unavailable,
}

public enum WinoIntelligenceAction
{
    AddDeadlineToCalendar,
    Translate,
    CancelTranslation,
    FindSimilarMail,
}

public sealed record WinoIntelligenceLanguageOption(string Code, string Label);

// Bound by property path from the reply DataTemplate in Generic.xaml, which a resource
// dictionary cannot express with x:Bind. The attribute keeps those paths trimming and AOT safe.
#if WINRT_EXPOSED
[GeneratedBindableCustomProperty]
#endif
public sealed partial class WinoIntelligenceReply
{
    public WinoIntelligenceReply()
    {
    }

    public WinoIntelligenceReply(string tone, string text)
    {
        Tone = tone;
        Text = text;
    }

    public string Tone { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public override string ToString() => Text;
}

// Bound by property path from Generic.xaml. Keep this type bindable under trimming/AOT.
#if WINRT_EXPOSED
[GeneratedBindableCustomProperty]
#endif
public sealed partial class WinoIntelligenceSimilarMailItem
{
    public string DisplayName { get; init; } = string.Empty;

    public string Initials { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    public string Meta { get; init; } = string.Empty;

    public string ScoreText { get; init; } = string.Empty;

    public object? Tag { get; init; }
}

/// <summary>
/// Raised when the user asks for a generated feature. The host must terminate the request by
/// calling one of the completion methods on the control with this <see cref="RequestId"/>.
/// </summary>
public sealed class WinoIntelligenceRequestEventArgs(Guid requestId, WinoIntelligenceFeature feature) : EventArgs
{
    public Guid RequestId { get; } = requestId;

    public WinoIntelligenceFeature Feature { get; } = feature;
}

/// <summary>
/// Raised when the user cancels a pending request. The control already returned the feature to
/// its idle state, so the host only needs to cancel its own work.
/// </summary>
public sealed class WinoIntelligenceCancelRequestedEventArgs(Guid requestId, WinoIntelligenceFeature feature) : EventArgs
{
    public Guid RequestId { get; } = requestId;

    public WinoIntelligenceFeature Feature { get; } = feature;
}

public sealed class WinoIntelligenceActionEventArgs(WinoIntelligenceAction action) : EventArgs
{
    public WinoIntelligenceAction Action { get; } = action;
}

public sealed class WinoIntelligenceReplyChosenEventArgs(WinoIntelligenceReply reply, int index) : EventArgs
{
    public WinoIntelligenceReply Reply { get; } = reply;

    public int Index { get; } = index;
}

public sealed class WinoIntelligenceSimilarMailChosenEventArgs(WinoIntelligenceSimilarMailItem item, int index) : EventArgs
{
    public WinoIntelligenceSimilarMailItem Item { get; } = item;

    public int Index { get; } = index;
}
