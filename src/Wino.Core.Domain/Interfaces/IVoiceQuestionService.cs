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
    /// Private mode: turns the names this device cannot match (an account, a folder, a person) into the same questions
    /// Personal mode gets from the server. The answers are used for this command only.
    /// </summary>
    Task<VoicePlan> AddLocalQuestionsAsync(VoicePlan plan, CancellationToken cancellationToken = default);

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
