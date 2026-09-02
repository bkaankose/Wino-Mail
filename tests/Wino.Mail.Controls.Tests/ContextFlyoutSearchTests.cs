using FluentAssertions;
using Wino.Mail.Controls.Core.ContextFlyout;
using Xunit;

namespace Wino.Mail.Controls.Tests;

public sealed class ContextFlyoutSearchTests
{
    private static readonly ContextFlyoutMenuEntry[] Entries =
    [
        new ContextFlyoutCommandEntry { Text = "Reply" },
        ContextFlyoutSeparatorEntry.Instance,
        new ContextFlyoutSubMenuEntry
        {
            Text = "Move to",
            Items =
            [
                new ContextFlyoutCommandEntry { Text = "Archive" },
                new ContextFlyoutSubMenuEntry
                {
                    Text = "Projects",
                    Items = [new ContextFlyoutToggleEntry { Text = "Wino" }]
                }
            ]
        }
    ];

    [Fact]
    public void Collect_FlattensActionableDescendants()
    {
        var candidates = ContextFlyoutSearch.Collect(Entries);

        candidates.Select(candidate => candidate.DisplayText).Should().Equal(
            "Reply",
            "Move to › Archive",
            "Move to › Projects › Wino");
    }

    [Fact]
    public void Collect_UsesAncestorPathAsBreadcrumb()
    {
        var candidates = ContextFlyoutSearch.Collect(Entries);

        candidates.Select(candidate => candidate.Breadcrumb).Should().Equal(
            string.Empty,
            "Move to",
            "Move to › Projects");
    }

    [Fact]
    public void Collect_SkipsSeparatorsAndSubMenusThemselves()
    {
        var candidates = ContextFlyoutSearch.Collect(Entries);

        candidates.Should().HaveCount(3);
        candidates.Select(candidate => candidate.Source.Text).Should().NotContain("Move to");
    }

    [Fact]
    public void Collect_KeepsTheSourceEntryForInvocation()
    {
        var subMenu = (ContextFlyoutSubMenuEntry)Entries[2];
        var nested = (ContextFlyoutSubMenuEntry)subMenu.Items[1];

        var candidates = ContextFlyoutSearch.Collect(Entries);

        candidates[2].Source.Should().BeSameAs(nested.Items[0]);
    }

    [Fact]
    public void Collect_OnAnEmptyMenu_ReturnsNoCandidates()
        => ContextFlyoutSearch.Collect([]).Should().BeEmpty();
}
