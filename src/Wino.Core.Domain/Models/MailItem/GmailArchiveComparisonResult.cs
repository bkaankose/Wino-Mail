namespace Wino.Core.Domain.Models.MailItem;

/// <summary>
/// Comparison result of the Gmail archive.
/// </summary>
/// <param name="Added">Mail copy ids to be added to Archive.</param>
/// <param name="Removed">Mail copy ids to be removed from Archive.</param>
public record GmailArchiveComparisonResult(string[] Added, string[] Removed);
