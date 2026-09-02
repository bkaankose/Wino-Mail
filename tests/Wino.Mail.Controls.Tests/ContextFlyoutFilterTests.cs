using FluentAssertions;
using Wino.Mail.Controls.Core.ContextFlyout;
using Xunit;

namespace Wino.Mail.Controls.Tests;

public sealed class ContextFlyoutFilterTests
{
    private static readonly ContextFlyoutFilterEntry[] Entries =
    [
        new(false, "Reply"),
        new(false, "Move to › Projects › Wino", "Move to › Projects", "folder destination"),
        new(true),
        new(false, "Category › Work", "Category", "label tag"),
        new(true)
    ];

    [Fact]
    public void EmptyQuery_PreservesItemsAndRemovesTrailingSeparator()
    {
        ContextFlyoutFilter.GetVisibleIndexes(Entries, string.Empty)
            .Should().Equal(0, 1, 2, 3);
    }

    [Fact]
    public void MultipleTerms_MatchAcrossTextAndBreadcrumb()
    {
        ContextFlyoutFilter.GetVisibleIndexes(Entries, "projects wino")
            .Should().Equal(1);
    }

    [Fact]
    public void Keywords_AreCaseInsensitive()
    {
        ContextFlyoutFilter.GetVisibleIndexes(Entries, "DESTINATION")
            .Should().Equal(1);
    }

    [Fact]
    public void Filtering_RemovesOrphanedSeparators()
    {
        ContextFlyoutFilter.GetVisibleIndexes(Entries, "work")
            .Should().Equal(3);
    }

    [Fact]
    public void NoMatch_ReturnsEmptyCollection()
    {
        ContextFlyoutFilter.GetVisibleIndexes(Entries, "missing")
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("Delete", false, false, false, false)]
    [InlineData("C", true, false, false, false)]
    [InlineData("Insert", false, false, true, false)]
    public void TextEditingShortcut_IsNotExecuted(
        string key,
        bool control,
        bool alt,
        bool shift,
        bool windows)
    {
        ContextFlyoutShortcutPolicy.CanExecuteWhileFiltering(key, control, alt, shift, windows)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("R", true, true, false, false)]
    [InlineData("F1", false, false, true, false)]
    [InlineData("M", false, false, false, true)]
    public void ModifiedNonEditingShortcut_CanExecute(
        string key,
        bool control,
        bool alt,
        bool shift,
        bool windows)
    {
        ContextFlyoutShortcutPolicy.CanExecuteWhileFiltering(key, control, alt, shift, windows)
            .Should().BeTrue();
    }
}
