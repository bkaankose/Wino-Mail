using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Rules;
using Wino.Core.Rules;
using Xunit;

namespace Wino.Core.Tests.Exchange;

public class RuleRunPlannerTests
{
    private static RemoteInboxRule Rule(string name, int priority, bool stop,
        IEnumerable<RuleConditionModel> conditions, IEnumerable<RuleActionModel> actions, bool enabled = true)
        => new()
        {
            Id = name,
            Name = name,
            Priority = priority,
            IsEnabled = enabled,
            StopProcessing = stop,
            Conditions = conditions.ToList(),
            Actions = actions.ToList()
        };

    private static RuleMessageFacts Msg(string from = null, string subject = null, string importance = null,
        bool attachments = false, IReadOnlyList<string> recipients = null, string body = null, string fromName = null)
        => new()
        {
            MessageId = Guid.NewGuid(),
            FromAddress = from,
            FromName = fromName,
            Subject = subject,
            Importance = importance,
            HasAttachments = attachments,
            Recipients = recipients ?? Array.Empty<string>(),
            Body = body
        };

    [Fact]
    public void Read_only_rule_with_stripped_conditions_never_runs()
    {
        // The fidelity guard marks partially-mapped rules read-only; a rule whose only condition was
        // unmappable arrives with an EMPTY condition list. Running it would match every message and
        // mass-move the folder, so read-only rules must be excluded from run-now entirely.
        var readOnlyRule = Rule("outlook-managed", 1, false,
            Array.Empty<RuleConditionModel>(),
            new[] { new RuleActionModel(RuleActionType.Move, "sender-folder") });
        readOnlyRule.IsReadOnly = true;

        var plan = RuleRunPlanner.Plan(new[] { readOnlyRule }, new[] { Msg(from: "anyone@example.com"), Msg(subject: "hi") });

        plan.Batches.Should().BeEmpty();
        plan.MatchedMessageCount.Should().Be(0);
    }

    [Fact]
    public void Editable_rule_with_no_conditions_matches_everything()
    {
        // An editable condition-less rule is Outlook's legitimate "apply to every message" shape.
        var rule = Rule("catch-all", 1, false,
            Array.Empty<RuleConditionModel>(),
            new[] { new RuleActionModel(RuleActionType.MarkRead, null) });

        var plan = RuleRunPlanner.Plan(new[] { rule }, new[] { Msg(from: "a@b.c"), Msg(subject: "x") });

        plan.MatchedMessageCount.Should().Be(2);
    }

    [Fact]
    public void Matching_rule_produces_move_batch()
    {
        var rule = Rule("r", 1, false,
            new[] { new RuleConditionModel(RuleConditionField.From, "github.com") },
            new[] { new RuleActionModel(RuleActionType.Move, "folder-1") });
        var msg = Msg(from: "notifications@github.com");

        var plan = RuleRunPlanner.Plan(new[] { rule }, new[] { msg });

        plan.Batches.Should().ContainSingle();
        plan.Batches[0].Action.Should().Be(RuleActionType.Move);
        plan.Batches[0].Value.Should().Be("folder-1");
        plan.Batches[0].MessageIds.Should().ContainSingle().Which.Should().Be(msg.MessageId);
        plan.MatchedMessageCount.Should().Be(1);
    }

    [Fact]
    public void Stop_processing_short_circuits_later_rules()
    {
        var stopRule = Rule("stop", 1, stop: true,
            new[] { new RuleConditionModel(RuleConditionField.SubjectContains, "invoice") },
            new[] { new RuleActionModel(RuleActionType.MarkRead, null) });
        var laterRule = Rule("later", 2, false,
            new[] { new RuleConditionModel(RuleConditionField.SubjectContains, "invoice") },
            new[] { new RuleActionModel(RuleActionType.Delete, null) });
        var msg = Msg(subject: "Your invoice is ready");

        var plan = RuleRunPlanner.Plan(new[] { stopRule, laterRule }, new[] { msg });

        plan.Batches.Select(b => b.Action).Should().BeEquivalentTo(new[] { RuleActionType.MarkRead });
    }

    [Fact]
    public void Same_action_and_target_coalesces_across_messages()
    {
        var rule = Rule("r", 1, false,
            new[] { new RuleConditionModel(RuleConditionField.SubjectContains, "receipt") },
            new[] { new RuleActionModel(RuleActionType.Move, "receipts") });
        var a = Msg(subject: "receipt #1");
        var b = Msg(subject: "your receipt");

        var plan = RuleRunPlanner.Plan(new[] { rule }, new[] { a, b });

        plan.Batches.Should().ContainSingle();
        plan.Batches[0].MessageIds.Should().BeEquivalentTo(new[] { a.MessageId, b.MessageId });
    }

    [Fact]
    public void Copy_and_forward_are_skipped_not_batched()
    {
        var rule = Rule("r", 1, false,
            new[] { new RuleConditionModel(RuleConditionField.SubjectContains, "report") },
            new[]
            {
                new RuleActionModel(RuleActionType.Copy, "archive"),
                new RuleActionModel(RuleActionType.Forward, "boss@x.com"),
                new RuleActionModel(RuleActionType.MarkRead, null)
            });
        var msg = Msg(subject: "weekly report");

        var plan = RuleRunPlanner.Plan(new[] { rule }, new[] { msg });

        plan.SkippedCopyForwardCount.Should().Be(2);
        plan.Batches.Should().ContainSingle().Which.Action.Should().Be(RuleActionType.MarkRead);
    }

    [Fact]
    public void All_conditions_must_match()
    {
        var rule = Rule("r", 1, false,
            new[]
            {
                new RuleConditionModel(RuleConditionField.SubjectContains, "invoice"),
                new RuleConditionModel(RuleConditionField.HasAttachment, null)
            },
            new[] { new RuleActionModel(RuleActionType.Delete, null) });

        var noAttachment = Msg(subject: "invoice", attachments: false);
        var withAttachment = Msg(subject: "invoice", attachments: true);

        RuleRunPlanner.Plan(new[] { rule }, new[] { noAttachment }).IsEmpty.Should().BeTrue();
        RuleRunPlanner.Plan(new[] { rule }, new[] { withAttachment }).Batches.Should().ContainSingle();
    }

    [Fact]
    public void Importance_and_categorize_round_trip()
    {
        var rule = Rule("r", 1, false,
            new[] { new RuleConditionModel(RuleConditionField.Importance, "High") },
            new[] { new RuleActionModel(RuleActionType.Categorize, "Work") });

        var plan = RuleRunPlanner.Plan(new[] { rule }, new[] { Msg(importance: "High") });

        var batch = plan.Batches.Should().ContainSingle().Subject;
        batch.Action.Should().Be(RuleActionType.Categorize);
        batch.Value.Should().Be("Work");
    }

    [Fact]
    public void Disabled_rules_are_ignored()
    {
        var rule = Rule("r", 1, false,
            new[] { new RuleConditionModel(RuleConditionField.SubjectContains, "x") },
            new[] { new RuleActionModel(RuleActionType.Delete, null) },
            enabled: false);

        RuleRunPlanner.Plan(new[] { rule }, new[] { Msg(subject: "xyz") }).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void From_matches_display_name_in_legacy_exchange_dn()
    {
        // Exchange stores the sender as a legacy X500/EX DN that embeds the display name; the message
        // carries an SMTP address + display name, so the rule should still match on the name.
        var rule = Rule("r", 1, false,
            new[] { new RuleConditionModel(RuleConditionField.From,
                "/o=CORP/ou=Exchange Administrative Group (FYDIBOHF23SPDLT)/cn=Recipients/cn=4dd262d9-Jane Doe") },
            new[] { new RuleActionModel(RuleActionType.Move, "f") });

        var msg = Msg(from: "jane@corp.example", fromName: "Jane Doe");

        RuleRunPlanner.Plan(new[] { rule }, new[] { msg }).Batches.Should().ContainSingle();
    }

    [Fact]
    public void Sent_to_and_body_use_loaded_facts()
    {
        var rule = Rule("r", 1, false,
            new[]
            {
                new RuleConditionModel(RuleConditionField.SentTo, "team@corp.example"),
                new RuleConditionModel(RuleConditionField.BodyContains, "quarterly")
            },
            new[] { new RuleActionModel(RuleActionType.MarkRead, null) });

        var match = Msg(recipients: new[] { "me@corp.example", "team@corp.example" }, body: "The quarterly numbers");
        var noBody = Msg(recipients: new[] { "team@corp.example" });

        RuleRunPlanner.Plan(new[] { rule }, new[] { match }).Batches.Should().ContainSingle();
        RuleRunPlanner.Plan(new[] { rule }, new[] { noBody }).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void No_match_yields_empty_plan()
    {
        var rule = Rule("r", 1, false,
            new[] { new RuleConditionModel(RuleConditionField.From, "nobody@x.com") },
            new[] { new RuleActionModel(RuleActionType.Move, "f") });

        RuleRunPlanner.Plan(new[] { rule }, new[] { Msg(from: "someone@y.com") }).IsEmpty.Should().BeTrue();
    }
}
