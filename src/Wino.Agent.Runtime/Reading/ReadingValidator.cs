using Wino.Agent.Runtime.Plan;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Agent.Runtime.Reading;

/// <summary>
/// Structural checks of a reading before resolution. The server already guarantees known kinds, catalog actions and
/// argument names and existing ref ids; this pass is the device's own defence: unknown actions, unknown ref ids,
/// forward links and more than five steps are dropped rather than failing the turn, and every problem is listed.
/// </summary>
public static class ReadingValidator
{
    public sealed record Result(AssistantReading Reading, IReadOnlyList<string> Problems);

    public static Result Validate(AssistantReading reading)
    {
        var problems = new List<string>();
        var refs = (reading.Refs ?? []).Where(r => !string.IsNullOrEmpty(r.Id) && AssistantRefKinds.All.Contains(r.Kind)).ToList();
        var ids = refs.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        refs = refs.Select(r => r.Kind == AssistantRefKinds.Rule ? r with { Rule = CleanRule(r.Rule, ids) } : r).ToList();
        foreach (var r in refs.Where(r => r.Kind == AssistantRefKinds.Rule && r.Rule is null)) problems.Add($"ref {r.Id}: a rule without a known action");
        var ruleIds = refs.Where(r => r.Kind == AssistantRefKinds.Rule && r.Rule is not null).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var steps = new List<AssistantStep>();
        foreach (var step in (reading.Steps ?? []).Take(AssistantReading.MaxSteps))
        {
            var info = AssistantActionCatalog.Find(step.Action);
            if (info is null)
            {
                problems.Add($"step {step.N}: unknown action {step.Action}");
                continue;
            }

            var args = new List<AssistantArg>();
            foreach (var arg in step.Args ?? [])
            {
                if (!info.Args.Contains(arg.Name, StringComparer.Ordinal))
                {
                    problems.Add($"step {step.N}: {step.Action} has no argument {arg.Name}");
                    continue;
                }

                var one = arg.Ref is not null && ids.Contains(arg.Ref) ? arg.Ref : null;
                var many = arg.Refs?.Where(ids.Contains).ToList();
                if (arg.Ref is not null && one is null) problems.Add($"step {step.N}: {arg.Name} points at unknown ref {arg.Ref}");
                if (one is null && (many is null || many.Count == 0) && arg.Value is null) continue;
                args.Add(new AssistantArg(arg.Name, one, many is { Count: > 0 } ? many : null, one is null && many is not { Count: > 0 } ? arg.Value : null));
            }

            // Closed vocabularies: a setting, a view or a rule the catalog does not know never reaches a plan.
            var missing = step.Action switch
            {
                AssistantActions.CreateMailRule when !args.Any(a => a.Name == "rule" && a.Ref is { } id && ruleIds.Contains(id)) => "rule",
                AssistantActions.ChangeSetting when !Enum.TryParse<AgentSetting>(Literal(args, "setting"), true, out _) => "setting",
                AssistantActions.ChangeSetting when Literal(args, "enabled") is not ("true" or "false") => "enabled",
                AssistantActions.OpenView when !Enum.TryParse<AgentView>(Literal(args, "view"), true, out _) => "view",
                _ => null,
            };
            if (missing is not null)
            {
                problems.Add($"step {step.N}: {step.Action} without a known {missing}; dropped");
                continue;
            }

            var on = step.On;
            if (on is not null && on != AssistantStep.OnSelection && !ids.Contains(on))
            {
                problems.Add($"step {step.N}: on points at unknown ref {on}");
                on = null;
            }

            var from = step.From;
            if (from is not null && (from.Step < 1 || from.Step >= step.N || !AssistantStepLinkParts.All.Contains(from.Part)))
            {
                problems.Add($"step {step.N}: from must point at an earlier step with a known part");
                from = null;
            }

            if (from is not null) on = null;
            var scope = AssistantScopes.IsKnown(step.Scope) ? step.Scope : null;
            if (step.Scope is not null && scope is null) problems.Add($"step {step.N}: unknown scope {step.Scope}");
            // A mail rule is the standing wish itself; a condition on it would only refuse what the user asked for.
            var cond = step.Action == AssistantActions.CreateMailRule ? null : step.Cond;
            steps.Add(step with { N = steps.Count + 1, On = on, From = from, Scope = scope, Cond = cond, Args = args, Alt = step.Alt?.Where(a => AssistantActionCatalog.IsKnown(a.Action)).ToList() });
        }

        if ((reading.Steps?.Count ?? 0) > AssistantReading.MaxSteps) problems.Add("more than five steps; the rest were dropped");
        var memory = (reading.Memory ?? []).Where(m => ids.Contains(m.Subject) && AssistantMemoryPredicates.All.Contains(m.Predicate)).ToList();
        if (memory.Count != (reading.Memory?.Count ?? 0)) problems.Add("memory statements with unknown subjects or predicates were dropped");
        // Step numbers were renumbered: fix links.
        var renumbered = steps.Select(s => s.From is { } f && f.Step > steps.Count ? s with { From = null } : s).ToList();
        return new Result(reading with { Steps = renumbered, Refs = refs, Memory = memory }, problems);
    }

    public static AgentAction ToAction(string name) => Enum.TryParse<AgentAction>(name, out var a) ? a : throw new ArgumentException("unknown action " + name);

    private static string? Literal(List<AssistantArg> args, string name) => args.Find(a => a.Name == name)?.Value;

    /// <summary>Known fields, operators and action types only, with refs that exist; null when no action is left.</summary>
    private static AssistantRule? CleanRule(AssistantRule? rule, HashSet<string> ids)
    {
        if (rule is null) return null;
        var conditions = (rule.Conditions ?? [])
            .Where(c => AssistantRuleFields.All.Contains(c.Field) && AssistantRuleOperators.All.Contains(c.Op))
            .Select(c => c with { Ref = c.Field == AssistantRuleFields.From && c.Ref is { } r && ids.Contains(r) ? r : null })
            .Where(c => c.Ref is not null || !string.IsNullOrWhiteSpace(c.Value))
            .Take(AssistantRule.MaxConditions).ToList();
        var actions = (rule.Actions ?? [])
            .Where(a => AssistantRuleActions.All.Contains(a.Type))
            .Select(a => a with { Folder = a.Type == AssistantRuleActions.Move && a.Folder is { } f && ids.Contains(f) ? f : null })
            .Where(a => a.Type != AssistantRuleActions.Move || a.Folder is not null)
            .DistinctBy(a => a.Type).Take(AssistantRule.MaxActions).ToList();
        return actions.Count == 0 ? null : new AssistantRule(rule.Match == AssistantRuleMatch.Any ? AssistantRuleMatch.Any : AssistantRuleMatch.All, conditions, actions);
    }
}
