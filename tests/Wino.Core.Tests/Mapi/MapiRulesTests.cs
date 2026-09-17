using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Rules;
using Wino.Core.Synchronizers.Mapi;
using Wino.Mapi.Restrictions;
using Wino.Mapi.Rops;
using Wino.Mapi.Rules;
using Wino.Mapi.Wire;
using FluentAssertions;
using Xunit;
using MapiActionType = Wino.Mapi.Rules.RuleActionType;
using RuleActionType = Wino.Core.Domain.Enums.RuleActionType;

namespace Wino.Core.Tests.Mapi;

/// <summary>Rung 8, rules: the rules-table row value types, the codecs, and the DTO mapping.</summary>
public class MapiRulesTests
{
    [Fact]
    public void RuleActions_MoveThenDelete_Decode()
    {
        // RuleActions (16-bit counts): NoOfActions 2; ActionBlock: ActionLength, ActionType, ActionFlavor, ActionFlags, data.
        var move = new RopWriter();
        move.UInt8((byte)MapiActionType.Move); move.UInt32(0); move.UInt32(0);
        move.UInt8(1);                                   // FolderInThisStore
        move.UInt16(0);                                  // StoreEIDSize
        move.UInt16(4); move.Bytes([0xDE, 0xAD, 0xBE, 0xEF]);   // FolderEID
        var moveBytes = move.ToArray();

        var delete = new RopWriter();
        delete.UInt8((byte)MapiActionType.Delete); delete.UInt32(0); delete.UInt32(0);
        var deleteBytes = delete.ToArray();

        var writer = new RopWriter();
        writer.UInt16(2);
        writer.UInt16((ushort)moveBytes.Length); writer.Bytes(moveBytes);
        writer.UInt16((ushort)deleteBytes.Length); writer.Bytes(deleteBytes);

        var actions = RuleActions.Read(new RopReader(writer.ToArray()));

        actions.Should().HaveCount(2);
        actions[0].Type.Should().Be(MapiActionType.Move);
        actions[0].FolderEntryId.Should().Equal(0xDE, 0xAD, 0xBE, 0xEF);
        actions[0].ServerFolderId.Should().BeNull();
        actions[0].StoreEntryId.Should().BeEmpty();
        actions[1].Type.Should().Be(MapiActionType.Delete);
    }

    [Fact]
    public void RuleActions_WriteThenRead_RoundTripsMoveForwardTag()
    {
        var actions = new List<RuleAction>
        {
            RuleAction.MoveTo(0x0001000000000ABC),
            RuleAction.ForwardTo([new RuleRecipient("Ops", "ops@example.com")]),
            RuleAction.TagWith(0x8123101F, new[] { "Red" }),
            RuleAction.MarkAsRead(),
        };

        var writer = new RopWriter();
        RuleActions.Write(writer, actions);
        var read = RuleActions.Read(new RopReader(writer.ToArray()));

        read.Should().HaveCount(4);
        read[0].Type.Should().Be(MapiActionType.Move);
        read[0].ServerFolderId.Should().Be(0x0001000000000ABC);
        read[0].FolderEntryId.Should().HaveCount(21);
        read[1].Recipients.Should().ContainSingle().Which.Address.Should().Be("ops@example.com");
        read[2].TagPropertyTag.Should().Be(0x8123101F);
        read[2].TagValue.Should().BeEquivalentTo(new[] { "Red" });
        read[3].Type.Should().Be(MapiActionType.MarkAsRead);
    }

    [Fact]
    public void RuleActions_UndecodableBlock_IsKeptRawNotFatal()
    {
        // A Forward block whose recipient carries a property type the reader does not know (PtypFloatingTime).
        var block = new RopWriter();
        block.UInt8((byte)MapiActionType.Forward); block.UInt32(0); block.UInt32(0);
        block.UInt16(1); block.UInt8(0); block.UInt32(1); block.UInt32(0x12340007); block.UInt64(0);
        var bytes = block.ToArray();

        var writer = new RopWriter();
        writer.UInt16(1); writer.UInt16((ushort)bytes.Length); writer.Bytes(bytes);

        var read = RuleActions.Read(new RopReader(writer.ToArray()));

        read.Single().Recipients.Should().BeNull();
        read.Single().RawData.Should().NotBeNull();
    }

    [Fact]
    public void Restriction_EncodeThenDecode_RoundTripsEveryKindTheMapperWrites()
    {
        var node = RestrictionNode.And(
            RestrictionNode.Comment(
                [TaggedPropertyValue.Unicode(PropertyTags.SmtpAddress, "a@b.c")],
                RestrictionNode.Or(
                    RestrictionNode.Property(RestrictionOps.RelOpEq, PropertyTags.SenderSearchKey, RuleActions.SmtpSearchKey("a@b.c")),
                    RestrictionNode.Content(PropertyTags.SenderSmtpAddress, "a@b.c", RestrictionOps.FuzzyFullString, RestrictionOps.FuzzyIgnoreCase))),
            RestrictionNode.SubObject(PropertyTags.MessageRecipients, RestrictionNode.Property(RestrictionOps.RelOpEq, PropertyTags.SearchKey, RuleActions.SmtpSearchKey("x@y.z"))),
            RestrictionNode.Content(PropertyTags.Subject, "Alert"),
            RestrictionNode.Property(RestrictionOps.RelOpEq, PropertyTags.Importance, 2u),
            RestrictionNode.BitMask(RestrictionOps.BitMaskNeZ, PropertyTags.MessageFlags, PropertyTags.MessageFlagHasAttach),
            RestrictionNode.Not(RestrictionNode.Exist(PropertyTags.MessageClass)));

        var writer = new RopWriter();
        Restriction.Encode(writer, node);
        var decoded = Restriction.Decode(new RopReader(writer.ToArray()));

        decoded.Kind.Should().Be(RestrictionKind.And);
        decoded.Children.Should().HaveCount(6);
        var comment = decoded.Children[0];
        comment.Kind.Should().Be(RestrictionKind.Comment);
        comment.CommentValues.Single().Value.Should().Be("a@b.c");
        comment.Children.Single().Children[0].Value.Should().BeEquivalentTo(RuleActions.SmtpSearchKey("a@b.c"));
        decoded.Children[1].Kind.Should().Be(RestrictionKind.SubObject);
        decoded.Children[2].FuzzyLevelLow.Should().Be(RestrictionOps.FuzzySubString);
        decoded.Children[3].Value.Should().Be(2u);
        decoded.Children[4].Mask.Should().Be(PropertyTags.MessageFlagHasAttach);
        decoded.Children[5].Children.Single().Kind.Should().Be(RestrictionKind.Exist);

        // Idempotent: re-encoding the decoded tree gives the same bytes.
        var again = new RopWriter();
        Restriction.Encode(again, decoded);
        again.ToArray().Should().Equal(writer.ToArray());
    }

    [Fact]
    public void ModifyRules_AddRow_EncodesFlagsCountAndInlineRestriction()
    {
        var definition = new MapiRuleDefinition("Test", 1, true, false, RestrictionNode.Exist(PropertyTags.MessageClass), [RuleAction.Delete()]);
        var data = new RopFolder.RuleData(RopFolder.RuleDataFlags.Add,
        [
            TaggedPropertyValue.Long(PropertyTags.RuleSequence, definition.Sequence),
            new TaggedPropertyValue(PropertyTags.RuleCondition, definition.Condition),
            new TaggedPropertyValue(PropertyTags.RuleActions, definition.Actions),
        ]);

        var rop = RopFolder.BuildModifyRules(1, [data]);

        var reader = new RopReader(rop);
        reader.UInt8().Should().Be(RopFolder.RopModifyRules);
        reader.UInt8(); reader.UInt8().Should().Be(1);
        reader.UInt8().Should().Be(0);                      // ModifyRulesFlags
        reader.UInt16().Should().Be(1);                     // RulesCount
        reader.UInt8().Should().Be((byte)RopFolder.RuleDataFlags.Add);
        reader.UInt16().Should().Be(3);                     // PropertyValueCount
        reader.UInt32().Should().Be(PropertyTags.RuleSequence); reader.UInt32().Should().Be(1);
        reader.UInt32().Should().Be(PropertyTags.RuleCondition);
        reader.UInt8().Should().Be(Restriction.TypeExist); reader.UInt32().Should().Be(PropertyTags.MessageClass);
        reader.UInt32().Should().Be(PropertyTags.RuleActions);
        reader.UInt16().Should().Be(1);                     // NoOfActions
        reader.UInt16().Should().Be(9);                     // ActionLength: type + flavor + flags
        reader.UInt8().Should().Be((byte)MapiActionType.Delete);
    }

    [Fact]
    public void GetPropertyIdsFromNames_Keywords_EncodesStringName()
    {
        var rop = RopProperties.BuildGetPropertyIdsFromNames(0, [(PropertyTags.PublicStringsPropertySet, PropertyTags.KeywordsPropertyName)]);

        var reader = new RopReader(rop);
        reader.UInt8().Should().Be(RopProperties.RopGetPropertyIdsFromNames);
        reader.UInt8(); reader.UInt8();
        reader.UInt8().Should().Be(0x02);                   // Create
        reader.UInt16().Should().Be(1);
        reader.UInt8().Should().Be(0x01);                   // Kind: string
        new Guid(reader.Bytes(16).Span).Should().Be(PropertyTags.PublicStringsPropertySet);
        reader.UInt8().Should().Be(18);                     // NameSize in bytes, terminator included
        System.Text.Encoding.Unicode.GetString(reader.Bytes(16).Span).Should().Be("Keywords");
        reader.UInt16().Should().Be(0);
        reader.Remaining.Should().Be(0);

        var response = new RopWriter();
        response.UInt8(RopProperties.RopGetPropertyIdsFromNames); response.UInt8(0); response.UInt32(0);
        response.UInt16(1); response.UInt16(0x8123);
        RopProperties.ParseGetPropertyIdsFromNames(new RopReader(response.ToArray())).Should().Equal((ushort)0x8123);
    }

    [Fact]
    public void RulesTableRow_DecodesConditionAndActionsInline()
    {
        // A standard row over RulesTableColumns: id, sequence, state, name, provider, level, user flags,
        // provider data, condition (narrow restriction), actions.
        var writer = new RopWriter();
        writer.UInt8(RopFolder.RopQueryRows); writer.UInt8(2); writer.UInt32(0);
        writer.UInt8(0); writer.UInt16(1);
        writer.UInt8(0x00);
        writer.UInt64(0x0001000000000007);               // RuleId
        writer.UInt32(3);                                // Sequence
        writer.UInt32(MapiRuleInfo.StateEnabled | MapiRuleInfo.StateExitLevel);
        writer.UnicodeZ("Alerts to folder");
        writer.UnicodeZ("RuleOrganizer");
        writer.UInt32(0);                                // Level
        writer.UInt32(0);                                // UserFlags
        writer.UInt16(2); writer.Bytes([1, 2]);          // ProviderData
        // Condition: CONTENT (narrow: no wide counts involved), FL_SUBSTRING|IGNORECASE on PidTagSubject = "Alert"
        writer.UInt8(0x03); writer.UInt16(0x0001); writer.UInt16(0x0001); writer.UInt32(PropertyTags.Subject); writer.UInt32(PropertyTags.Subject); writer.UnicodeZ("Alert");
        // Actions: one MarkAsRead
        var action = new RopWriter(); action.UInt8((byte)MapiActionType.MarkAsRead); action.UInt32(0); action.UInt32(0);
        writer.UInt16(1); writer.UInt16((ushort)action.Length); writer.Bytes(action.ToArray());

        var rows = RopFolder.ParseQueryRows(new RopReader(writer.ToArray()), PropertyTags.RulesTableColumns);
        var row = rows.Single();

        PropertyTags.RuleId.Should().Be(0x66740014u, "PidTagRuleId is 0x6674; a neighbouring id reads as NOT_FOUND on every row");
        row[0].AsUInt64.Should().Be(0x0001000000000007);
        row[3].AsString.Should().Be("Alerts to folder");
        var condition = row[8].Value.Should().BeOfType<RestrictionNode>().Subject;
        condition.Kind.Should().Be(RestrictionKind.Content);
        condition.Value.Should().Be("Alert");
        var actions = row[9].Value.Should().BeOfType<List<RuleAction>>().Subject;
        actions.Single().Type.Should().Be(MapiActionType.MarkAsRead);

        var info = new MapiRuleInfo(row[0].AsUInt64!.Value, row[1].AsUInt32!.Value, row[2].AsUInt32!.Value, row[3].AsString!, row[4].AsString!, 0, 0, row[7].AsBinary, condition, actions);
        info.IsEnabled.Should().BeTrue();
        info.StopsProcessing.Should().BeTrue();
    }

    // ---- Mapper ------------------------------------------------------------------------------------

    private static readonly Dictionary<ulong, string> FolderKeys = new() { [0x0001000000000ABC] = "ews-folder-id" };

    private static string FolderKey(ulong id) => FolderKeys.TryGetValue(id, out var key) ? key : null;
    private static ulong? FolderId(string key) => FolderKeys.FirstOrDefault(p => p.Value == key).Key is var id && id != 0 ? id : null;

    [Fact]
    public void Mapper_DtoToDefinitionToDto_RoundTripsTheModeledSubset()
    {
        var dto = new RemoteInboxRule
        {
            Id = "",
            Name = "Alerts",
            Priority = 2,
            IsEnabled = true,
            StopProcessing = true,
            Conditions =
            {
                new RuleConditionModel(RuleConditionField.From, "boss@corp.example, hr@corp.example"),
                new RuleConditionModel(RuleConditionField.SentTo, "me@corp.example"),
                new RuleConditionModel(RuleConditionField.SubjectContains, "urgent"),
                new RuleConditionModel(RuleConditionField.BodyContains, "invoice"),
                new RuleConditionModel(RuleConditionField.Importance, "High"),
                new RuleConditionModel(RuleConditionField.HasAttachment, null),
            },
            Actions =
            {
                new RuleActionModel(RuleActionType.Move, "ews-folder-id"),
                new RuleActionModel(RuleActionType.Categorize, "Red"),
                new RuleActionModel(RuleActionType.Forward, "ops@corp.example"),
                new RuleActionModel(RuleActionType.MarkRead, null),
            }
        };

        var definition = MapiRuleMapper.ToDefinition(dto, FolderId, 0x8123101F);
        definition.State.Should().Be(MapiRuleInfo.StateEnabled | MapiRuleInfo.StateExitLevel);

        // Through the wire and back, as the rules table would return it.
        var writer = new RopWriter();
        Restriction.Encode(writer, definition.Condition);
        var condition = Restriction.Decode(new RopReader(writer.ToArray()));
        var actionWriter = new RopWriter();
        RuleActions.Write(actionWriter, definition.Actions);
        var actions = RuleActions.Read(new RopReader(actionWriter.ToArray()));

        var info = new MapiRuleInfo(0x0002000000000001, definition.Sequence, definition.State, definition.Name, "RuleOrganizer", 0, 0, null, condition, actions);
        var back = MapiRuleMapper.ToDto(info, FolderKey, 0x8123101F);

        back.IsReadOnly.Should().BeFalse(back.ReadOnlyReason);
        back.Id.Should().Be("0002000000000001");
        back.Priority.Should().Be(2);
        back.StopProcessing.Should().BeTrue();
        back.Conditions.Should().BeEquivalentTo(dto.Conditions);
        back.Actions.Should().BeEquivalentTo(dto.Actions);
    }

    [Fact]
    public void Mapper_OutlookShapes_ReadAsFromAndSentTo()
    {
        // Outlook's own encoding: comment-wrapped search-key equality with an EX DN, no SMTP beside it.
        var dn = "/O=CORP/OU=EXCHANGE ADMINISTRATIVE GROUP/CN=RECIPIENTS/CN=BOSS";
        var condition = RestrictionNode.And(
            RestrictionNode.Comment(
                [TaggedPropertyValue.Unicode(PropertyTags.DisplayName, "Boss")],
                RestrictionNode.Property(RestrictionOps.RelOpEq, PropertyTags.SenderSearchKey, System.Text.Encoding.ASCII.GetBytes("EX:" + dn + "\0"))),
            RestrictionNode.SubObject(PropertyTags.MessageRecipients,
                RestrictionNode.Comment(
                    [TaggedPropertyValue.Unicode(PropertyTags.SmtpAddress, "team@corp.example")],
                    RestrictionNode.Property(RestrictionOps.RelOpEq, PropertyTags.SearchKey, RuleActions.SmtpSearchKey("team@corp.example")))));

        var info = new MapiRuleInfo(1, 10, MapiRuleInfo.StateEnabled, "From boss", "RuleOrganizer", 0, 0, null, condition, [RuleAction.Delete()]);
        var dto = MapiRuleMapper.ToDto(info, FolderKey, null);

        dto.IsReadOnly.Should().BeFalse(dto.ReadOnlyReason);
        dto.Conditions.Should().Contain(c => c.Field == RuleConditionField.From && c.Value == dn);
        dto.Conditions.Should().Contain(c => c.Field == RuleConditionField.SentTo && c.Value == "team@corp.example");
    }

    [Fact]
    public void Mapper_UnsupportedShapes_AreReadOnlyWithReason()
    {
        var withException = new MapiRuleInfo(1, 10, MapiRuleInfo.StateEnabled, "r", "RuleOrganizer", 0, 0, null,
            RestrictionNode.And(RestrictionNode.Content(PropertyTags.Subject, "x"), RestrictionNode.Not(RestrictionNode.Content(PropertyTags.Subject, "y"))),
            [RuleAction.Delete()]);
        MapiRuleMapper.ToDto(withException, FolderKey, null).Should().Match<RemoteInboxRule>(r => r.IsReadOnly && r.ReadOnlyReason.Contains("exception"));

        var foreignFolder = new MapiRuleInfo(2, 10, MapiRuleInfo.StateEnabled, "r", "RuleOrganizer", 0, 0, null,
            RestrictionNode.Exist(PropertyTags.MessageClass), [RuleAction.MoveTo(0x0001000000000FFF)]);
        MapiRuleMapper.ToDto(foreignFolder, FolderKey, null).Should().Match<RemoteInboxRule>(r => r.IsReadOnly && r.ReadOnlyReason.Contains("not synced"));

        var redirect = new MapiRuleInfo(3, 10, MapiRuleInfo.StateEnabled, "r", "RuleOrganizer", 0, 0, null,
            RestrictionNode.Exist(PropertyTags.MessageClass),
            [new RuleAction { Type = MapiActionType.Forward, Flavor = 0x01, Recipients = [new RuleRecipient("a", "a@b.c")] }]);
        MapiRuleMapper.ToDto(redirect, FolderKey, null).Should().Match<RemoteInboxRule>(r => r.IsReadOnly && r.ReadOnlyReason.Contains("redirects"));

        var allMessages = new MapiRuleInfo(4, 10, MapiRuleInfo.StateEnabled, "r", "RuleOrganizer", 0, 0, null,
            RestrictionNode.Exist(PropertyTags.MessageClass), [RuleAction.MarkAsRead()]);
        var dto = MapiRuleMapper.ToDto(allMessages, FolderKey, null);
        dto.IsReadOnly.Should().BeFalse();
        dto.Conditions.Should().BeEmpty();
    }
}

/// <summary>Rung 8, junk: the junk rule's condition splits into blocked and safe lists.</summary>
public class MapiJunkListTests
{
    [Fact]
    public void JunkCondition_SafeUnderNot_BlockedOtherwise()
    {
        var condition = RestrictionNode.And(
            RestrictionNode.Not(RestrictionNode.Or(
                RestrictionNode.Content(PropertyTags.SenderEmailAddress, "Friend@Example.com", RestrictionOps.FuzzyFullString, RestrictionOps.FuzzyIgnoreCase),
                RestrictionNode.SubObject(PropertyTags.MessageRecipients,
                    RestrictionNode.Content(PropertyTags.EmailAddress, "list@example.org", RestrictionOps.FuzzyFullString, RestrictionOps.FuzzyIgnoreCase)))),
            RestrictionNode.Or(
                RestrictionNode.Property(RestrictionOps.RelOpGe, 0x40760003, 5u),
                RestrictionNode.Content(PropertyTags.SenderEmailAddress, "spam@bad.example", RestrictionOps.FuzzyFullString, RestrictionOps.FuzzyIgnoreCase),
                RestrictionNode.Content(PropertyTags.SenderEmailAddress, "@worse.example", RestrictionOps.FuzzySubString, RestrictionOps.FuzzyIgnoreCase)));

        // Through the extended (32-bit count) encoding, as PidTagExtendedRuleMessageCondition stores it.
        var writer = new RopWriter();
        writer.UInt16(0);                                   // NoOfNamedProps
        Restriction.Encode(writer, condition, extendedFormat: true);
        var decoded = Restriction.DecodeExtendedRuleCondition(writer.ToArray());

        var lists = MapiRulesOperations.ClassifyJunkCondition(decoded);

        lists.BlockedSenders.Should().Equal("spam@bad.example", "@worse.example");
        lists.SafeSenders.Should().Equal("friend@example.com");
        lists.SafeRecipients.Should().Equal("list@example.org");
    }
}

public class MapiNamedPropertyTests
{
    [Fact]
    public void GetPropertyIdsFromNames_ByLid_EncodesKindZeroAndLid()
    {
        var rop = RopProperties.BuildGetPropertyIdsFromNames(0, [RopProperties.PropertyName.ById(PropertyTags.AddressPropertySet, PropertyTags.LidEmail1EmailAddress)]);

        var reader = new RopReader(rop);
        reader.UInt8(); reader.UInt8(); reader.UInt8(); reader.UInt8();
        reader.UInt16().Should().Be(1);
        reader.UInt8().Should().Be(0x00);
        new Guid(reader.Bytes(16).Span).Should().Be(PropertyTags.AddressPropertySet);
        reader.UInt32().Should().Be(0x8083);
        reader.Remaining.Should().Be(0);
    }
}
