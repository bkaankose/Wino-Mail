using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Menus;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

/// <summary>The reader command bar layout the Mac, iPhone, iPad and Android readers share.</summary>
public sealed class MailReaderCommandLayoutTests
{
    private static IMenuOperation[] Menu(params MailOperation[] operations)
        => operations.Select(static operation => (IMenuOperation)MailOperationMenuItem.Create(operation)).ToArray();

    [Fact]
    public void ListedMessage_PutsRepliesFirst_ThenTheMessageActions_AndTheRestInMore()
    {
        var plan = MailReaderCommandLayout.Build(
            Menu(MailOperation.Archive, MailOperation.Reply, MailOperation.SoftDelete, MailOperation.Forward, MailOperation.ReplyAll,
                MailOperation.SetFlag, MailOperation.MarkAsUnread, MailOperation.Seperator, MailOperation.Print),
            hasListItem: true, offerMove: false);

        plan.Kind.Should().Be(MailReaderCommandPlanKind.Message);
        plan.Primary.Select(static slot => slot?.Operation).Should().Equal(
            MailOperation.Reply, MailOperation.ReplyAll, MailOperation.Forward, null,
            MailOperation.Archive, MailOperation.SoftDelete, MailOperation.SetFlag, MailOperation.MarkAsUnread);
        plan.Overflow.Select(static item => item.Operation).Should().Equal(MailOperation.Seperator, MailOperation.Print);
    }

    [Fact]
    public void TouchReaders_OfferMoveNextToDelete()
    {
        var plan = MailReaderCommandLayout.Build(Menu(MailOperation.Reply, MailOperation.SoftDelete), hasListItem: true, offerMove: true);

        plan.Primary.Select(static slot => slot?.Operation).Should().Equal(MailOperation.Reply, null, MailOperation.SoftDelete, MailOperation.Move);
        plan.Primary.Last()!.Item.Should().NotBeNull();
    }

    [Fact]
    public void SavedMessageFile_ShowsOnlyItsOwnOperations()
    {
        var plan = MailReaderCommandLayout.Build(Menu(MailOperation.SaveAs, MailOperation.Print), hasListItem: false, offerMove: true);

        plan.Kind.Should().Be(MailReaderCommandPlanKind.File);
        plan.Primary.Select(static slot => slot?.Operation).Should().Equal(MailOperation.SaveAs, MailOperation.Print);
        plan.Overflow.Should().BeEmpty();
    }

    [Fact]
    public void NothingLoaded_ShowsTheDefaultSetDisabled()
    {
        var plan = MailReaderCommandLayout.Build([], hasListItem: true, offerMove: true);

        plan.Kind.Should().Be(MailReaderCommandPlanKind.Placeholder);
        plan.Primary.OfType<MailReaderCommandSlot>().Should().OnlyContain(static slot => !slot.IsEnabled && slot.Item == null);
        plan.Primary.Should().Contain(static slot => slot != null && slot.Operation == MailOperation.Move);
    }
}
