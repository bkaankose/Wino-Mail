using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Rules;

/// <summary>
/// Provider-neutral representation of a server-side inbox rule (currently Exchange, over EWS or
/// MAPI/HTTP). The server is the source of truth: these are fetched on demand and written back on
/// save; there is no local persistence. Conditions are AND-ed; actions run in order.
/// </summary>
public sealed class RemoteInboxRule
{
    /// <summary>Server rule id. Null/empty for a rule that has not been created yet.</summary>
    public string Id { get; set; }

    public string Name { get; set; }

    /// <summary>Lower priority runs first. Mirrors EWS <c>Rule.Priority</c>.</summary>
    public int Priority { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Maps to the EWS <c>StopProcessingRules</c> action; surfaced as a rule-level flag.</summary>
    public bool StopProcessing { get; set; }

    /// <summary>
    /// True when the rule contains conditions/actions/exceptions Wino does not model (or the server
    /// flagged it unsupported/in-error). Read-only rules are shown but never written back, so we never
    /// silently drop the parts we cannot represent. See the fidelity guard in the Exchange mappers.
    /// </summary>
    public bool IsReadOnly { get; set; }

    public string ReadOnlyReason { get; set; }

    public List<RuleConditionModel> Conditions { get; set; } = new();

    public List<RuleActionModel> Actions { get; set; } = new();
}

/// <summary>A single rule condition: a field plus its (optionally empty) value.</summary>
public sealed class RuleConditionModel
{
    public RuleConditionField Field { get; set; }

    /// <summary>Interpreted per <see cref="RuleFieldCatalog.ValueKind(RuleConditionField)"/>.</summary>
    public string Value { get; set; }

    public RuleConditionModel() { }

    public RuleConditionModel(RuleConditionField field, string value)
    {
        Field = field;
        Value = value;
    }
}

/// <summary>A single rule action: a type plus its (optionally empty) value.</summary>
public sealed class RuleActionModel
{
    public RuleActionType Type { get; set; }

    /// <summary>Interpreted per <see cref="RuleFieldCatalog.ValueKind(RuleActionType)"/>.</summary>
    public string Value { get; set; }

    public RuleActionModel() { }

    public RuleActionModel(RuleActionType type, string value)
    {
        Type = type;
        Value = value;
    }
}
