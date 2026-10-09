using FluentAssertions;
using Wino.Calendar.ViewModels;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class CalendarNotesHtmlResolverTests
{
    private const string Original = "<div dir=\"ltr\"><b>Agenda</b>&nbsp;<a href=\"https://example.com\">link</a></div>\r\n";
    private const string Baseline = "<div dir=\"ltr\"><b>Agenda</b>&nbsp;<a href=\"https://example.com\">link</a></div>";

    [Fact]
    public void Untouched_ReturnsOriginalByteForByte()
        => CalendarNotesHtmlResolver.Resolve(Original, Baseline, Baseline, isEditorReady: true, wasEdited: false).Should().Be(Original);

    [Fact]
    public void Edited_ReturnsEditorHtml()
        => CalendarNotesHtmlResolver.Resolve(Original, Baseline, "<p>New</p>", isEditorReady: true, wasEdited: true).Should().Be("<p>New</p>");

    [Fact]
    public void EditedThenReverted_ReturnsOriginal()
        => CalendarNotesHtmlResolver.Resolve(Original, Baseline, Baseline, isEditorReady: true, wasEdited: true).Should().Be(Original);

    [Fact]
    public void EditorNeverReady_ReturnsOriginal()
        => CalendarNotesHtmlResolver.Resolve(Original, null, null, isEditorReady: false, wasEdited: false).Should().Be(Original);

    [Fact]
    public void EmptyOriginalWithEditorPlaceholder_ReturnsEmpty()
        => CalendarNotesHtmlResolver.Resolve(string.Empty, "<p><br></p>", "<p><br></p>", isEditorReady: true, wasEdited: false).Should().BeEmpty();

    [Fact]
    public void NoBaseline_UsesTheEditFlag()
    {
        CalendarNotesHtmlResolver.Resolve(Original, null, "<p>x</p>", isEditorReady: true, wasEdited: false).Should().Be(Original);
        CalendarNotesHtmlResolver.Resolve(Original, null, "<p>x</p>", isEditorReady: true, wasEdited: true).Should().Be("<p>x</p>");
    }

    [Fact]
    public void UnreadableCurrentHtml_ReturnsOriginal()
        => CalendarNotesHtmlResolver.Resolve(Original, Baseline, null, isEditorReady: true, wasEdited: true).Should().Be(Original);
}
