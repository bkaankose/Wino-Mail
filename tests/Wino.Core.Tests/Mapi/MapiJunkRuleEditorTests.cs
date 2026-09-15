using Wino.Mapi;
using Wino.Mapi.Restrictions;
using Wino.Mapi.Rops;
using Wino.Mapi.Rules;
using FluentAssertions;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>
/// The junk rule write-back against a live condition: the 276-byte PidTagExtendedRuleMessageCondition
/// Exchange wrote after OWA's "Block or allow" page added one blocked and one safe sender (2026-09-10).
/// </summary>
public class MapiJunkRuleEditorTests
{
    private static readonly byte[] LiveCondition = Convert.FromHexString("000000020000000102000000010100000003000001001f001f0c1f001f0c6d00610074007400300032003000310039003100400067006d00610069006c002e0063006f006d000000000200000001020000000002000000080300764004020300764003007640ffffffff01000000000201020000000100000000090d00120e0100000000020103000000010100000003000001001f001f0c1f001f0c6d00610074007400300032003000310039003100400068006f0074006d00610069006c002e0063006f006d000000090d00120e010100000003000001001f0003301f0003306d00610074007400300032003000310039003100400068006f0074006d00610069006c002e0063006f006d0000000100000000");

    [Fact]
    public void LiveCondition_RoundTripsByteForByte()
    {
        var tree = Restriction.DecodeExtendedRuleCondition(LiveCondition);
        var lists = MapiRulesOperations.ClassifyJunkCondition(tree);
        lists.BlockedSenders.Should().Equal("matt020191@gmail.com");
        lists.SafeSenders.Should().Equal("matt020191@hotmail.com");
        lists.SafeRecipients.Should().Equal("matt020191@hotmail.com");

        Restriction.EncodeExtendedRuleCondition(tree).Should().Equal(LiveCondition, "what is written back must be what Exchange wrote");
    }

    [Fact]
    public void Locate_FindsTheThreeLists()
    {
        var lists = JunkRuleEditor.Locate(Restriction.DecodeExtendedRuleCondition(LiveCondition));
        lists.BlockedSenders.Children.Should().ContainSingle(c => (string)c.Value! == "matt020191@gmail.com");
        lists.SafeSenders.Children.Should().ContainSingle(c => (string)c.Value! == "matt020191@hotmail.com");
        lists.SafeRecipients.Children.Should().ContainSingle(c => c.PropertyTag == PropertyTags.EmailAddress);

        var wrong = RestrictionNode.Or(RestrictionNode.Exist(PropertyTags.MessageClass));
        var act = () => JunkRuleEditor.Locate(wrong);
        act.Should().Throw<MapiFormatException>("an unfamiliar shape is refused, not guessed at");
    }

    [Fact]
    public void Block_AddsTermInExchangesShape_Once_AndDropsItFromSafeLists()
    {
        var tree = Restriction.DecodeExtendedRuleCondition(LiveCondition);
        JunkRuleEditor.Block(tree, " Spammer@Example.com ");
        JunkRuleEditor.Block(tree, "spammer@example.com");

        var lists = MapiRulesOperations.ClassifyJunkCondition(Restriction.DecodeExtendedRuleCondition(Restriction.EncodeExtendedRuleCondition(tree)));
        lists.BlockedSenders.Should().Equal("matt020191@gmail.com", "spammer@example.com");

        var term = JunkRuleEditor.Locate(tree).BlockedSenders.Children[1];
        term.Kind.Should().Be(RestrictionKind.Content);
        term.PropertyTag.Should().Be(PropertyTags.SenderEmailAddress);
        term.FuzzyLevelLow.Should().Be(RestrictionOps.FuzzyFullString);
        term.FuzzyLevelHigh.Should().Be(RestrictionOps.FuzzyIgnoreCase);

        // Blocking a safe sender moves it: it leaves both safe lists.
        JunkRuleEditor.Block(tree, "matt020191@hotmail.com");
        var moved = MapiRulesOperations.ClassifyJunkCondition(tree);
        moved.SafeSenders.Should().BeEmpty();
        moved.SafeRecipients.Should().BeEmpty();
        moved.BlockedSenders.Should().Contain("matt020191@hotmail.com");
    }

    [Fact]
    public void Unblock_RemovesTheTerm_AndLeavesTheRest()
    {
        var tree = Restriction.DecodeExtendedRuleCondition(LiveCondition);
        JunkRuleEditor.Unblock(tree, "MATT020191@gmail.com");
        JunkRuleEditor.Unblock(tree, "nobody@example.com");

        var lists = MapiRulesOperations.ClassifyJunkCondition(tree);
        lists.BlockedSenders.Should().BeEmpty();
        lists.SafeSenders.Should().Equal("matt020191@hotmail.com");
        Restriction.EncodeExtendedRuleCondition(tree).Length.Should().BeLessThan(LiveCondition.Length);
    }

    [Fact]
    public void Trust_AddsToBothSafeLists_AndDropsFromBlocked()
    {
        var tree = Restriction.DecodeExtendedRuleCondition(LiveCondition);
        JunkRuleEditor.Trust(tree, "matt020191@gmail.com");
        JunkRuleEditor.Trust(tree, "matt020191@gmail.com");

        var lists = MapiRulesOperations.ClassifyJunkCondition(Restriction.DecodeExtendedRuleCondition(Restriction.EncodeExtendedRuleCondition(tree)));
        lists.BlockedSenders.Should().BeEmpty();
        lists.SafeSenders.Should().Equal("matt020191@hotmail.com", "matt020191@gmail.com");
        lists.SafeRecipients.Should().Equal("matt020191@hotmail.com", "matt020191@gmail.com");
        JunkRuleEditor.Locate(tree).SafeRecipients.Children[1].PropertyTag.Should().Be(PropertyTags.EmailAddress);

        JunkRuleEditor.Untrust(tree, "MATT020191@hotmail.com");
        var after = MapiRulesOperations.ClassifyJunkCondition(tree);
        after.SafeSenders.Should().Equal("matt020191@gmail.com");
        after.SafeRecipients.Should().Equal("matt020191@gmail.com");
    }

    [Fact]
    public void ConditionWithNamedProperties_IsNotRewritten()
    {
        var tree = Restriction.DecodeExtendedRuleCondition(LiveCondition);
        tree.NamedPropertyCount = 1;
        var act = () => Restriction.EncodeExtendedRuleCondition(tree);
        act.Should().Throw<MapiFormatException>();
    }
}
