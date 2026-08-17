#nullable enable
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Intelligence;

/// <summary>
/// The single definition of which folders intelligence may read. The SQL predicate and the
/// folder-picker predicate used to be independent copies of the same list, so they could drift.
/// </summary>
public static class IntelligenceFolderFilter
{
    /// <summary>
    /// Folders whose contents are never indexed: drafts and deleted mail are not correspondence,
    /// junk is noise, and the remaining types are provider-side views over mail that already
    /// lives in a real folder.
    /// </summary>
    public static readonly IReadOnlyList<SpecialFolderType> ExcludedSpecialFolderTypes =
    [
        SpecialFolderType.Draft,
        SpecialFolderType.Deleted,
        SpecialFolderType.Junk,
        SpecialFolderType.Chat,
        SpecialFolderType.Category,
        SpecialFolderType.Unread,
        SpecialFolderType.Forums,
        SpecialFolderType.Updates,
        SpecialFolderType.Personal,
        SpecialFolderType.Promotions,
        SpecialFolderType.Social,
        SpecialFolderType.More,
    ];

    /// <summary>
    /// Renders the exclusion as a parameterised SQL clause. The caller must append
    /// <see cref="ExcludedSpecialFolderTypeArguments"/> to its argument list in the same position.
    /// </summary>
    public static string SqlNotInClause(string folderAlias)
        => $"{folderAlias}.SpecialFolderType NOT IN ({string.Join(", ", ExcludedSpecialFolderTypes.Select(static _ => "?"))})";

    public static object[] ExcludedSpecialFolderTypeArguments()
        => [.. ExcludedSpecialFolderTypes.Select(static type => (object)(int)type)];

    public static bool IsSelectable(MailItemFolder folder)
        => folder.IsSynchronizationEnabled &&
           !string.IsNullOrWhiteSpace(folder.RemoteFolderId) &&
           !ExcludedSpecialFolderTypes.Contains(folder.SpecialFolderType);
}
