using FluentAssertions;
using Wino.Mail.Controls.Core.HoverActions;
using Xunit;

namespace Wino.Mail.Controls.Tests;

public sealed class HoverActionConfigurationTests
{
    [Fact]
    public void GetVisibleActions_PreservesSlotOrderAndDuplicates()
    {
        var actions = HoverActionConfiguration.GetVisibleActions(
            HoverActionKind.Archive,
            HoverActionKind.Archive,
            HoverActionKind.ToggleRead);

        actions.Should().Equal(
            HoverActionKind.Archive,
            HoverActionKind.Archive,
            HoverActionKind.ToggleRead);
    }

    [Fact]
    public void GetVisibleActions_RemovesNoneSlots()
    {
        var actions = HoverActionConfiguration.GetVisibleActions(
            HoverActionKind.None,
            HoverActionKind.Delete,
            HoverActionKind.None);

        actions.Should().Equal(HoverActionKind.Delete);
    }

    [Fact]
    public void GetVisibleActions_AllNone_ReturnsEmpty()
    {
        var actions = HoverActionConfiguration.GetVisibleActions(
            HoverActionKind.None,
            HoverActionKind.None,
            HoverActionKind.None);

        actions.Should().BeEmpty();
    }
}
