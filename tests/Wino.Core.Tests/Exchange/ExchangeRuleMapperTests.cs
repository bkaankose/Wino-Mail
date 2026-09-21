using FluentAssertions;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Rules;
using Wino.Core.Synchronizers.Exchange;
using Xunit;

namespace Wino.Core.Tests.Exchange;

public class ExchangeRuleMapperTests
{
    // ---- EWS Rule -> DTO -----------------------------------------------------------------------

    [Fact]
    public void ToDto_maps_all_modeled_conditions()
    {
        var rule = new Rule { Id = "r1", DisplayName = "Conditions", Priority = 3, IsEnabled = true };
        rule.Conditions.FromAddresses.Add(new EmailAddress("sender@x.com"));
        rule.Conditions.SentToAddresses.Add(new EmailAddress("me@x.com"));
        rule.Conditions.ContainsSubjectStrings.Add("invoice");
        rule.Conditions.ContainsBodyStrings.Add("payment");
        rule.Conditions.Importance = Importance.High;
        rule.Conditions.HasAttachments = true;

        var dto = ExchangeRuleMapper.ToDto(rule);

        dto.Id.Should().Be("r1");
        dto.Name.Should().Be("Conditions");
        dto.Priority.Should().Be(3);
        dto.IsReadOnly.Should().BeFalse();
        dto.Conditions.Should().SatisfyRespectively(
            c => { c.Field.Should().Be(RuleConditionField.From); c.Value.Should().Be("sender@x.com"); },
            c => { c.Field.Should().Be(RuleConditionField.SubjectContains); c.Value.Should().Be("invoice"); },
            c => { c.Field.Should().Be(RuleConditionField.BodyContains); c.Value.Should().Be("payment"); },
            c => { c.Field.Should().Be(RuleConditionField.SentTo); c.Value.Should().Be("me@x.com"); },
            c => { c.Field.Should().Be(RuleConditionField.Importance); c.Value.Should().Be("High"); },
            c => { c.Field.Should().Be(RuleConditionField.HasAttachment); });
    }

    [Fact]
    public void ToDto_maps_all_modeled_actions()
    {
        var rule = new Rule { DisplayName = "Actions" };
        rule.Actions.MoveToFolder = new FolderId("move-id");
        rule.Actions.CopyToFolder = new FolderId("copy-id");
        rule.Actions.AssignCategories.Add("Work");
        rule.Actions.MarkAsRead = true;
        rule.Actions.ForwardToRecipients.Add(new EmailAddress("boss@x.com"));
        rule.Actions.Delete = true;

        var dto = ExchangeRuleMapper.ToDto(rule);

        dto.IsReadOnly.Should().BeFalse();
        dto.Actions.Should().Contain(a => a.Type == RuleActionType.Move && a.Value == "move-id");
        dto.Actions.Should().Contain(a => a.Type == RuleActionType.Copy && a.Value == "copy-id");
        dto.Actions.Should().Contain(a => a.Type == RuleActionType.Categorize && a.Value == "Work");
        dto.Actions.Should().Contain(a => a.Type == RuleActionType.MarkRead);
        dto.Actions.Should().Contain(a => a.Type == RuleActionType.Forward && a.Value == "boss@x.com");
        dto.Actions.Should().Contain(a => a.Type == RuleActionType.Delete);
    }

    [Fact]
    public void ToDto_maps_stop_processing_flag()
    {
        var rule = new Rule { DisplayName = "Stop" };
        rule.Actions.StopProcessingRules = true;

        ExchangeRuleMapper.ToDto(rule).StopProcessing.Should().BeTrue();
    }

    // ---- DTO -> EWS Rule -----------------------------------------------------------------------

    [Fact]
    public void ToEwsRule_maps_conditions_and_actions()
    {
        var dto = new RemoteInboxRule { Id = "r5", Name = "Round", Priority = 2, IsEnabled = true, StopProcessing = true };
        dto.Conditions.Add(new RuleConditionModel(RuleConditionField.SubjectContains, "report"));
        dto.Conditions.Add(new RuleConditionModel(RuleConditionField.Importance, "Low"));
        dto.Conditions.Add(new RuleConditionModel(RuleConditionField.HasAttachment, null));
        dto.Actions.Add(new RuleActionModel(RuleActionType.Move, "folder-9"));
        dto.Actions.Add(new RuleActionModel(RuleActionType.Categorize, "Travel"));

        var rule = ExchangeRuleMapper.ToEwsRule(dto);

        rule.Id.Should().Be("r5");
        rule.DisplayName.Should().Be("Round");
        rule.Priority.Should().Be(2);
        rule.Conditions.ContainsSubjectStrings.Should().ContainSingle().Which.Should().Be("report");
        rule.Conditions.Importance.Should().Be(Importance.Low);
        rule.Conditions.HasAttachments.Should().BeTrue();
        rule.Actions.MoveToFolder.UniqueId.Should().Be("folder-9");
        rule.Actions.AssignCategories.Should().ContainSingle().Which.Should().Be("Travel");
        rule.Actions.StopProcessingRules.Should().BeTrue();
    }

    [Fact]
    public void ToEwsRule_sets_id_only_for_existing_rule()
    {
        ExchangeRuleMapper.ToEwsRule(new RemoteInboxRule { Name = "new" }).Id.Should().BeNull();
        ExchangeRuleMapper.ToEwsRule(new RemoteInboxRule { Id = "existing", Name = "edit" }).Id.Should().Be("existing");
    }

    [Fact]
    public void ToEwsRule_splits_multiple_addresses()
    {
        var dto = new RemoteInboxRule { Name = "fwd" };
        dto.Conditions.Add(new RuleConditionModel(RuleConditionField.From, "a@x.com, b@y.com; c@z.com"));

        var rule = ExchangeRuleMapper.ToEwsRule(dto);

        rule.Conditions.FromAddresses.Select(e => e.Address)
            .Should().BeEquivalentTo("a@x.com", "b@y.com", "c@z.com");
    }

    [Fact]
    public void ToEwsRule_clamps_unset_priority_to_one()
    {
        ExchangeRuleMapper.ToEwsRule(new RemoteInboxRule { Name = "p" }).Priority.Should().Be(1);
    }

    // ---- Fidelity guard ------------------------------------------------------------------------

    [Fact]
    public void Fully_modeled_rule_is_not_read_only()
    {
        var rule = new Rule { DisplayName = "simple", Priority = 1, IsEnabled = true };
        rule.Conditions.FromAddresses.Add(new EmailAddress("a@b.com"));
        rule.Actions.MoveToFolder = new FolderId("fid");

        ExchangeRuleMapper.ToDto(rule).IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public void Unmodeled_predicate_is_read_only()
    {
        var rule = new Rule { DisplayName = "sensitive" };
        rule.Conditions.Sensitivity = Sensitivity.Personal;

        var dto = ExchangeRuleMapper.ToDto(rule);

        dto.IsReadOnly.Should().BeTrue();
        dto.ReadOnlyReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Unmodeled_sender_strings_predicate_is_read_only()
    {
        var rule = new Rule { DisplayName = "sender-name" };
        rule.Conditions.ContainsSenderStrings.Add("ACME");

        ExchangeRuleMapper.ToDto(rule).IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void Unmodeled_action_is_read_only()
    {
        var rule = new Rule { DisplayName = "redirect" };
        rule.Actions.RedirectToRecipients.Add(new EmailAddress("other@x.com"));

        ExchangeRuleMapper.ToDto(rule).IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void Exception_clause_is_read_only()
    {
        var rule = new Rule { DisplayName = "with-exception" };
        rule.Conditions.ContainsSubjectStrings.Add("invoice");
        rule.Exceptions.FromAddresses.Add(new EmailAddress("vip@x.com"));

        ExchangeRuleMapper.ToDto(rule).IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void Multiple_subject_terms_is_read_only()
    {
        var rule = new Rule { DisplayName = "multi" };
        rule.Conditions.ContainsSubjectStrings.Add("a");
        rule.Conditions.ContainsSubjectStrings.Add("b");

        ExchangeRuleMapper.ToDto(rule).IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void Read_only_rule_keeps_the_conditions_it_could_map()
    {
        // The mapped remainder is kept for display; run-now must exclude such rules (planner tests).
        var rule = new Rule { DisplayName = "partial" };
        rule.Conditions.FromAddresses.Add(new EmailAddress("a@b.com"));
        rule.Conditions.IsMeetingRequest = true;

        var dto = ExchangeRuleMapper.ToDto(rule);

        dto.IsReadOnly.Should().BeTrue();
        dto.Conditions.Should().ContainSingle().Which.Field.Should().Be(RuleConditionField.From);
    }
}
