#nullable enable
namespace Wino.Core.Domain.Models.Reader;

/// <summary>The answer to an action card's question: open the address, and stop asking from now on.</summary>
public sealed record ActionCardOpenConfirmation(bool IsConfirmed, bool DontAskAgain);

/// <summary>What the user can do with one problem the checks before sending found.</summary>
public enum DraftCheckIssueAction
{
    Show,
    Fix,
    Describe,
    NotNeeded
}

public enum DraftCheckDialogChoice
{
    GoBack,
    SendAnyway,
    IssueAction
}

/// <summary>One row of the checks dialog. The flags say which buttons the row offers.</summary>
public sealed record DraftCheckIssueItem(string Title, string Detail, bool CanShow, bool CanFix, bool CanDescribe, bool CanMarkNotNeeded);

/// <summary>
/// The user's choice in the checks dialog. With <see cref="DraftCheckDialogChoice.IssueAction"/>, <see cref="IssueIndex"/>
/// is the row and <see cref="Action"/> the button.
/// </summary>
public sealed record DraftCheckDialogResult(DraftCheckDialogChoice Choice, int IssueIndex = -1, DraftCheckIssueAction Action = DraftCheckIssueAction.Show);

/// <summary>A picture's size in CSS pixels.</summary>
public readonly record struct PictureSize(int Width, int Height);
