#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// The questions a Personal plan leaves open (<see cref="VoiceCommand.Unresolved"/>): what to ask, the candidates to
/// pick from, and how an answer goes into the plan and, in Personal mode, into a learned fact.
/// </summary>
public interface IVoiceQuestionService
{
    /// <summary>Every open question of the plan, once per kind and phrase, in spoken order.</summary>
    IReadOnlyList<VoiceQuestion> GetQuestions(VoicePlan plan);

    /// <summary>
    /// Turns the names this device cannot match (an account, a folder, a person) into the same questions
    /// Personal mode gets from the server, in both modes: Personal mode's brain doesn't know every name. Private answers
    /// are used for this command only.
    /// </summary>
    Task<VoicePlan> AddLocalQuestionsAsync(VoicePlan plan, CancellationToken cancellationToken = default);

    /// <summary>
    /// Personal mode: answers the questions this device can answer itself (a phrase that names exactly one local
    /// account, folder or person, as the account named "Work" does for "my work account") so the user isn't asked what
    /// is obvious on the device. Relationships are always asked. The facts teach the brain the answers.
    /// </summary>
    Task<VoiceKnownAnswers> AnswerKnownQuestionsAsync(VoicePlan plan, CancellationToken cancellationToken = default);

    /// <summary>
    /// The candidates for a question, filtered by <paramref name="filter"/>. People are searched in all contacts with
    /// the most frequent correspondents first; folders and calendars come grouped by account.
    /// </summary>
    Task<IReadOnlyList<VoiceAnswerCandidate>> GetCandidatesAsync(VoiceQuestion question, string? filter, CancellationToken cancellationToken = default);

    /// <summary>Replaces the question's phrase everywhere in the plan with the answer.</summary>
    VoicePlan ApplyAnswer(VoicePlan plan, VoiceQuestion question, VoiceAnswerCandidate answer);

    /// <summary>The learned fact an answer becomes in Personal mode, or null when it can't be expressed.</summary>
    AddBrainFactRequest? CreateFact(VoiceQuestion question, VoiceAnswerCandidate answer);
}
