#nullable enable

namespace Wino.Calendar.ViewModels;

/// <summary>
/// Decides which HTML an event composer saves as the event notes. Notes the user did not change
/// go back exactly as they arrived (an imported .ics or a To-Do description), byte for byte, rather
/// than the editor's normalized copy. Platform-neutral; currently used by the Mac composer only.
/// </summary>
public static class CalendarNotesHtmlResolver
{
    /// <param name="originalHtml">The HTML the composer was opened with (empty for none).</param>
    /// <param name="baselineHtml">The editor's own HTML right after it rendered <paramref name="originalHtml"/>, or null when it could not be read.</param>
    /// <param name="currentHtml">The editor's HTML now, or null when it could not be read.</param>
    /// <param name="isEditorReady">False when the editor never finished loading.</param>
    /// <param name="wasEdited">Whether the editor reported a content change after it loaded.</param>
    public static string Resolve(string? originalHtml, string? baselineHtml, string? currentHtml, bool isEditorReady, bool wasEdited)
    {
        var original = originalHtml ?? string.Empty;

        if (!isEditorReady || currentHtml is null)
            return original;

        // Edit-then-undo also lands back on the baseline, which keeps the original.
        if (baselineHtml is not null)
            return string.Equals(currentHtml, baselineHtml, System.StringComparison.Ordinal) ? original : currentHtml;

        return wasEdited ? currentHtml : original;
    }
}
