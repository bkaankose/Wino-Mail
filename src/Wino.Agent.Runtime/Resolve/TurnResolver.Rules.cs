using Wino.Agent.Runtime.Clarify;
using Wino.Agent.Runtime.Plan;
using Wino.Agent.Runtime.Session;
using Wino.Core.Domain.Enums;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Agent.Runtime.Resolve;

public sealed partial class TurnResolver
{
    /// <summary>
    /// A new mail rule (plan: the rule ref of a CreateMailRule step). The move folder is resolved first, since it fixes
    /// the rule's account; a sender condition then resolves like a sender filter (a relation from the brain, a name
    /// from the contacts, an org to its addresses or domain) and every resolved address becomes its own condition.
    /// The app's rule matches all of its conditions or any of them, so several senders beside other conditions cannot
    /// be one rule: the step becomes one rule per sender (<see cref="_extraArgs"/>). The account is the named one, else
    /// the folder's, else the sender's contact's, else the current one, else the only one, else a question. Nothing is
    /// guessed: an unknown person asks (an org or a brand falls back to its name).
    /// </summary>
    private async Task<StepArgs> ResolveMailRuleArgsAsync(AssistantStep step, AgentAction action, AccountScope scope, CancellationToken ct)
    {
        var ruleRef = Refs(step, "rule").FirstOrDefault(r => r.Kind == AssistantRefKinds.Rule && r.Rule is not null);
        if (ruleRef?.Rule is not { } rule) throw new TurnMessageException(AgentMessageKind.NotUnderstood, step.Gloss);

        Guid? accountId = scope.One;
        var actions = new List<MailRuleAction>();
        foreach (var a in rule.Actions ?? [])
        {
            if (ActionTypeOf(a.Type) is not { } type) continue;
            Guid? folderId = null;
            if (type == MailFilterActionType.Move)
            {
                if (a.Folder is null || Ref(a.Folder) is null) continue;
                var folderStep = step with { Args = [.. step.Args, new AssistantArg("destination", a.Folder)] };
                var folder = (await ResolveContainersAsync(folderStep, action, "destination", accountId is { } acc ? AccountScope.Of(acc) : scope, true, ct).ConfigureAwait(false)).FirstOrDefault();
                if (folder is null) throw new TurnMessageException(AgentMessageKind.NothingMatched, SaidAs(Ref(a.Folder)!));
                folderId = folder.Id;
                accountId ??= folder.AccountId;
            }

            actions.Add(new MailRuleAction(type, folderId));
        }

        if (actions.Count == 0) throw new TurnMessageException(AgentMessageKind.NotUnderstood, step.Gloss);

        var conditions = new List<MailRuleCondition>();
        var senderAccounts = new List<Guid>();
        var i = 0;
        foreach (var c in rule.Conditions ?? [])
        {
            var op = OperatorOf(c.Op);
            switch (c.Field)
            {
                case AssistantRuleFields.From:
                    // The first sender resolves under the usual sender slot (its people get the sender role); later ones under their own.
                    var field = i++ == 0 ? "sender" : $"sender{i}";
                    if (c.Ref is { } id && Ref(id) is { } sender)
                    {
                        var (addresses, domains, words) = await ResolveRuleSenderAsync(step, sender, field, ct).ConfigureAwait(false);
                        foreach (var address in addresses) conditions.Add(new MailRuleCondition(MailFilterConditionField.FromAddress, op, address));
                        foreach (var domain in domains) conditions.Add(new MailRuleCondition(MailFilterConditionField.FromAddress, SuffixOperator(op), "@" + domain));
                        if (addresses.Count == 0 && domains.Count == 0 && words is not null)
                            conditions.Add(new MailRuleCondition(MailFilterConditionField.FromName, ContainsOperator(op), words));
                        foreach (var address in addresses)
                            if (await ContactAccountAsync(address, ct).ConfigureAwait(false) is { } ca) senderAccounts.Add(ca);
                    }
                    else if (c.Value is { } value)
                    {
                        if (TextMatch.LooksLikeAddress(value)) conditions.Add(new MailRuleCondition(MailFilterConditionField.FromAddress, op, value.Trim()));
                        else if (TextMatch.LooksLikeDomain(value)) conditions.Add(new MailRuleCondition(MailFilterConditionField.FromAddress, SuffixOperator(op), "@" + TextMatch.Fold(value)));
                        else conditions.Add(new MailRuleCondition(MailFilterConditionField.FromName, ContainsOperator(op), value.Trim()));
                    }

                    break;
                case AssistantRuleFields.Subject when c.Value is { } subject:
                    conditions.Add(new MailRuleCondition(MailFilterConditionField.Subject, op, subject.Trim()));
                    break;
                case AssistantRuleFields.Body when c.Value is { } body:
                    conditions.Add(new MailRuleCondition(MailFilterConditionField.PreviewText, op, body.Trim()));
                    break;
                case AssistantRuleFields.HasAttachments:
                    var has = !string.Equals(c.Value, "false", StringComparison.OrdinalIgnoreCase);
                    if (op is MailFilterConditionOperator.NotEquals or MailFilterConditionOperator.NotContains) has = !has;
                    conditions.Add(new MailRuleCondition(MailFilterConditionField.HasAttachments, MailFilterConditionOperator.Equals, has ? "true" : "false"));
                    break;
                case AssistantRuleFields.Importance when Enum.TryParse<MailImportance>(c.Value, true, out var importance):
                    conditions.Add(new MailRuleCondition(MailFilterConditionField.Importance, op is MailFilterConditionOperator.NotEquals or MailFilterConditionOperator.NotContains ? MailFilterConditionOperator.NotEquals : MailFilterConditionOperator.Equals, importance.ToString()));
                    break;
            }
        }

        // A rule with nothing to match would act on every mail: never made from words that named a sender nobody resolved.
        if (conditions.Count == 0) throw new TurnMessageException(AgentMessageKind.NotUnderstood, step.Gloss);

        // A mail has one sender, so several sender conditions can only be alternatives: alone they are matched as any; beside
        // other conditions ("from my parents about invoices") the rule splits into one per sender, each with the other conditions.
        var match = rule.Match == AssistantRuleMatch.Any ? MailFilterMatchMode.Any : MailFilterMatchMode.All;
        var senders = conditions.Where(IsSenderCondition).ToList();
        var rest = conditions.Where(c => !IsSenderCondition(c)).ToList();
        var perSender = new List<IReadOnlyList<MailRuleCondition>>();
        if (match == MailFilterMatchMode.All && senders.Count > 1)
        {
            if (rest.Count == 0) match = MailFilterMatchMode.Any;
            else perSender = senders.Select(s => (IReadOnlyList<MailRuleCondition>)[s, .. rest]).ToList();
        }

        accountId ??= senderAccounts.Distinct().ToList() is { Count: 1 } one ? one[0] : null;
        accountId ??= RuleAccount(step, step.Gloss ?? SaidAs(ruleRef));
        var name = ContentText(Refs(step, "name").FirstOrDefault());
        if (perSender.Count == 0) return new StepArgs.MailRuleArgs(accountId.Value, name, match, conditions, actions);
        var rules = perSender.Select(c => new StepArgs.MailRuleArgs(accountId.Value, name is null ? null : $"{name} ({c[0].Value})", match, c, actions)).ToList();
        _extraArgs[step.N] = rules.Skip(1).Cast<StepArgs>().ToList();
        return rules[0];
    }

    /// <summary>A condition that names who the mail is from (an address, a domain or name words), not one that excludes a sender.</summary>
    private static bool IsSenderCondition(MailRuleCondition c)
        => c.Field is MailFilterConditionField.FromAddress or MailFilterConditionField.FromName && c.Operator is not (MailFilterConditionOperator.NotEquals or MailFilterConditionOperator.NotContains);

    /// <summary>
    /// The sender of a rule condition: people and groups through the people resolver (a relation needs a fact or a pick; a
    /// name no contact carries asks, since a rule on name words would catch every similar name for good), an org through
    /// the sender filter (addresses, a domain, or words).
    /// </summary>
    private async Task<(List<string> Addresses, List<string> Domains, string? Words)> ResolveRuleSenderAsync(AssistantStep step, AssistantRef sender, string field, CancellationToken ct)
    {
        if (sender.Kind == AssistantRefKinds.Org)
        {
            var org = await ResolveOrgFilterAsync(step, sender, field, ct).ConfigureAwait(false);
            return (org.Addresses, org.Domains, org.Words);
        }

        if (TextMatch.LooksLikeAddress(sender.Norm)) return ([sender.Norm!.Trim()], [], null);
        if (TextMatch.LooksLikeDomain(sender.Norm)) return ([], [TextMatch.Fold(sender.Norm)], null);
        var people = await ResolvePeopleAsync(step, field, PersonUse.RuleSender, ct, [sender with { Role = AssistantRoles.Sender }]).ConfigureAwait(false);
        return (people.People.Select(p => p.Address).ToList(), [], people.Words);
    }

    /// <summary>The account a contact belongs to when the device lists it under exactly one.</summary>
    private async Task<Guid?> ContactAccountAsync(string address, CancellationToken ct)
    {
        var hits = (await _q.SearchContactsAsync(TextMatch.LocalPart(address), 10, ct).ConfigureAwait(false))
            .Where(c => string.Equals(c.Address, address, StringComparison.OrdinalIgnoreCase) && c.AccountId is not null)
            .Select(c => c.AccountId!.Value).Distinct().ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>The rule's account when nothing named it: the current one, else the only one, else a question.</summary>
    private Guid RuleAccount(AssistantStep step, string saidAs)
    {
        if (_t.Request.CurrentAccountId is { } current && _index.Account(current) is not null) return current;
        if (_index.Accounts.Count == 1) return _index.Accounts[0].Id;
        if (_index.Accounts.Count == 0) throw new TurnMessageException(AgentMessageKind.Unavailable, saidAs);
        var options = _index.Accounts.Select(a => new Candidate(ItemRef.Account(a.Id), a.Name, a.Address, null, 0, false)).ToList();
        var answer = Consume(step.N, "account", ClarificationKind.PickAccount, saidAs, options, searchable: false);
        return answer.Refs.Select(r => r.AccountId).FirstOrDefault(id => id is not null && _index.Account(id) is not null)
            ?? throw new TurnMessageException(AgentMessageKind.Unavailable, saidAs);
    }

    private static MailFilterActionType? ActionTypeOf(string type) => type switch
    {
        AssistantRuleActions.MarkRead => MailFilterActionType.MarkRead,
        AssistantRuleActions.MarkUnread => MailFilterActionType.MarkUnread,
        AssistantRuleActions.Flag => MailFilterActionType.SetFlag,
        AssistantRuleActions.Unflag => MailFilterActionType.ClearFlag,
        AssistantRuleActions.Move => MailFilterActionType.Move,
        AssistantRuleActions.Archive => MailFilterActionType.Archive,
        AssistantRuleActions.MarkJunk => MailFilterActionType.MoveToJunk,
        AssistantRuleActions.MarkNotJunk => MailFilterActionType.MarkAsNotJunk,
        AssistantRuleActions.Delete => MailFilterActionType.SoftDelete,
        AssistantRuleActions.DeletePermanently => MailFilterActionType.HardDelete,
        _ => null,
    };

    private static MailFilterConditionOperator OperatorOf(string op) => op switch
    {
        AssistantRuleOperators.NotEqual => MailFilterConditionOperator.NotEquals,
        AssistantRuleOperators.Contains => MailFilterConditionOperator.Contains,
        AssistantRuleOperators.NotContains => MailFilterConditionOperator.NotContains,
        AssistantRuleOperators.StartsWith => MailFilterConditionOperator.StartsWith,
        AssistantRuleOperators.EndsWith => MailFilterConditionOperator.EndsWith,
        _ => MailFilterConditionOperator.Equals,
    };

    /// <summary>A domain matches the end of the address; a negated condition keeps its negation.</summary>
    private static MailFilterConditionOperator SuffixOperator(MailFilterConditionOperator op)
        => op is MailFilterConditionOperator.NotEquals or MailFilterConditionOperator.NotContains ? MailFilterConditionOperator.NotContains : MailFilterConditionOperator.EndsWith;

    /// <summary>Words match a sender name by containment; a negated condition keeps its negation.</summary>
    private static MailFilterConditionOperator ContainsOperator(MailFilterConditionOperator op)
        => op is MailFilterConditionOperator.NotEquals or MailFilterConditionOperator.NotContains ? MailFilterConditionOperator.NotContains : MailFilterConditionOperator.Contains;
}
