using Wino.Agent.Runtime.Brain;
using Wino.Agent.Runtime.Plan;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Agent.Runtime.Session;

/// <summary>
/// The compose hint (plan 5.3): built only from <see cref="BrainEntryKind.Preference"/> entries the user stated,
/// whose subject is a recipient of the step or its account (or the user, for the sign-off); closed-vocabulary values
/// only; attached only to a compose call. The request's own content fields win. Recipients that disagree -> no hint.
/// </summary>
public static class HintBuilder
{
    public static AssistantHints? Build(AgentBrain brain, IReadOnlyList<Recipient> recipients, Guid? accountId, string? contentLanguage, string? contentTone, string? contentLength)
    {
        // Plan 5.3's table: a recipient's stated language and tone come first, then the request's own content fields.
        var language = Agree(recipients.Select(r => Stated(brain, BrainSubject.Contact(r.Address, null).Key, "language"))) ?? contentLanguage;
        var tone = Agree(recipients.Select(r => Stated(brain, BrainSubject.Contact(r.Address, null).Key, "tone"))) ?? contentTone;
        string? signoff = null;
        if (accountId is { } a)
        {
            var accountKey = BrainSubject.Account(a, null).Key;
            language ??= Stated(brain, accountKey, "language");
            tone ??= Stated(brain, accountKey, "tone");
            signoff = Stated(brain, accountKey, "signoff");
        }

        signoff ??= Stated(brain, BrainSubject.Self().Key, "signoff");
        if (!AssistantHints.Tones.Contains(tone ?? "", StringComparer.OrdinalIgnoreCase)) tone = null;
        if (!AssistantHints.Signoffs.Contains(signoff ?? "", StringComparer.Ordinal)) signoff = null;
        var length = AssistantHints.Lengths.Contains(contentLength ?? "", StringComparer.OrdinalIgnoreCase) ? contentLength!.ToLowerInvariant() : null;
        if (language is null && tone is null && length is null && signoff is null) return null;
        return new AssistantHints(language, tone?.ToLowerInvariant(), length, signoff);
    }

    private static string? Stated(AgentBrain brain, string subjectKey, string key)
        => brain.PreferencesFor(subjectKey).Where(e => e.Source == BrainSource.Stated && e.Value.StartsWith(key + "=", StringComparison.Ordinal))
            .Select(e => e.Value[(key.Length + 1)..]).FirstOrDefault();

    /// <summary>One value every recipient with a preference shares; null when they disagree or none has one.</summary>
    private static string? Agree(IEnumerable<string?> values)
    {
        var set = values.Where(v => v is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return set.Count == 1 ? set[0] : null;
    }
}
