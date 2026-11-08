namespace Wino.Mail.Controls.Core.VoiceBubble;

/// <summary>
/// The voice bubble surface each platform draws. It only presents: the shared voice bubble controller sets the text
/// for a state, calls <see cref="Present"/>, feeds <see cref="UpdateBands"/> while listening, and reacts to the events.
/// Success and failure dismiss themselves after their delay and raise <see cref="Dismissed"/>; the exit animation
/// raises <see cref="ExitCompleted"/>.
/// </summary>
public interface IVoiceBubbleView
{
    VoiceBubbleState State { get; }

    /// <summary>Moves to <paramref name="state"/>, or re-reads the text of the current state when it is the same.</summary>
    void Present(VoiceBubbleState state);

    int SuccessDismissMilliseconds { get; set; }

    bool CanRetry { get; set; }

    bool ShowsTranscript { get; set; }

    string ListeningText { get; set; }

    string ConfirmText { get; set; }

    string DeclineText { get; set; }

    string RetryText { get; set; }

    string ConfirmSelectedText { get; set; }

    string NoticeTitle { get; set; }

    string QuestionSearchPlaceholder { get; set; }

    string QuestionEmptyText { get; set; }

    string QuestionCancelText { get; set; }

    string? Transcript { get; set; }

    string? InterimTranscript { get; set; }

    string? QuestionText { get; set; }

    string? QuestionCaption { get; set; }

    bool IsQuestionSearchable { get; set; }

    VoiceBubbleText? Message { get; set; }

    VoiceBubbleText? ConfirmationText { get; set; }

    IReadOnlyList<VoiceBubbleStep>? ConfirmationSteps { get; set; }

    IReadOnlyList<VoiceBubbleStepGroup>? ConfirmationGroups { get; set; }

    /// <summary>Replaces the question's choices in one batch.</summary>
    void SetChoices(List<VoiceBubbleChoice> choices);

    /// <summary>Empties the filter box for a new question without raising <see cref="QuestionFilterChanged"/>.</summary>
    void ClearQuestionFilter();

    /// <summary>Loudness bands from 0 to 1, lowest frequency first, while listening.</summary>
    void UpdateBands(ReadOnlySpan<float> bands);

    event EventHandler? SendRequested;

    event EventHandler? CancelRequested;

    event EventHandler? ConfirmRequested;

    event EventHandler? RetryRequested;

    event EventHandler? Dismissed;

    event EventHandler? ExitCompleted;

    event EventHandler<string>? QuestionFilterChanged;

    event EventHandler<VoiceBubbleChoice>? ChoiceSelected;

    /// <summary>How the current request is entered; set before <see cref="Present"/>.</summary>
    VoiceBubbleInputMode InputMode { get; set; }

    /// <summary>
    /// The text of a typed request, shown in <see cref="VoiceBubbleState.Composing"/> as an editable text with a caret in
    /// the transcript's place and style. The controller sets it (for example when spoken words are appended); user edits
    /// raise <see cref="ComposedTextChanged"/>.
    /// </summary>
    string ComposedText { get; set; }

    string ComposePlaceholder { get; set; }

    /// <summary>Shows the microphone button in <see cref="VoiceBubbleState.Composing"/>.</summary>
    bool CanSwitchToVoice { get; set; }

    /// <summary>One short line under the request, for example that the spoken requests of this month are used up.</summary>
    string? InputNotice { get; set; }

    event EventHandler<string>? ComposedTextChanged;

    /// <summary>The microphone button in <see cref="VoiceBubbleState.Composing"/>.</summary>
    event EventHandler? SwitchToVoiceRequested;
}
