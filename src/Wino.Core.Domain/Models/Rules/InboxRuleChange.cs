using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Rules;

/// <summary>A pending create/update/delete to apply against the server's inbox-rule collection.</summary>
public sealed class InboxRuleChange
{
    public InboxRuleChangeKind Kind { get; set; }

    /// <summary>The rule for <see cref="InboxRuleChangeKind.Create"/> / <see cref="InboxRuleChangeKind.Update"/>.</summary>
    public RemoteInboxRule Rule { get; set; }

    /// <summary>The rule id for <see cref="InboxRuleChangeKind.Delete"/>.</summary>
    public string RuleId { get; set; }

    public static InboxRuleChange Create(RemoteInboxRule rule) => new() { Kind = InboxRuleChangeKind.Create, Rule = rule };

    public static InboxRuleChange Update(RemoteInboxRule rule) => new() { Kind = InboxRuleChangeKind.Update, Rule = rule };

    public static InboxRuleChange Delete(string ruleId) => new() { Kind = InboxRuleChangeKind.Delete, RuleId = ruleId };
}
