namespace Wino.Mail.Controls.Core.ContextFlyout;

/// <summary>
/// Actionable descendant of a page, flattened for searching. <see cref="DisplayText"/> is the full
/// breadcrumb path, <see cref="Breadcrumb"/> is the path of its ancestors only.
/// </summary>
public sealed record ContextFlyoutSearchCandidate(
    ContextFlyoutCommandEntry Source,
    string DisplayText,
    string Breadcrumb);

public static class ContextFlyoutSearch
{
    public const string BreadcrumbSeparator = " › ";

    /// <summary>
    /// Flattens the actionable descendants of a page once, so filtering a query does not re-walk
    /// the entry tree per keystroke. Submenus contribute their label to the breadcrumb of their
    /// children but never produce a candidate of their own.
    /// </summary>
    public static IReadOnlyList<ContextFlyoutSearchCandidate> Collect(
        IReadOnlyList<ContextFlyoutMenuEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var candidates = new List<ContextFlyoutSearchCandidate>();
        Collect(entries, string.Empty, candidates);

        return candidates;
    }

    public static string AppendBreadcrumb(string breadcrumb, string text)
        => string.IsNullOrWhiteSpace(breadcrumb) ? text : $"{breadcrumb}{BreadcrumbSeparator}{text}";

    private static void Collect(
        IReadOnlyList<ContextFlyoutMenuEntry> entries,
        string breadcrumb,
        ICollection<ContextFlyoutSearchCandidate> destination)
    {
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case ContextFlyoutSubMenuEntry subMenu:
                    Collect(subMenu.Items, AppendBreadcrumb(breadcrumb, subMenu.Text), destination);
                    break;

                case ContextFlyoutCommandEntry command:
                    destination.Add(new ContextFlyoutSearchCandidate(
                        command,
                        AppendBreadcrumb(breadcrumb, command.Text),
                        breadcrumb));
                    break;
            }
        }
    }
}
