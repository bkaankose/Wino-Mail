#nullable enable
using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Models.SemanticIndexing;

namespace Wino.Core.Domain.Models.Intelligence;

/// <summary>
/// Everything the coverage editor needs, handed over by the page that opens it.
/// </summary>
/// <remarks>
/// The editor performs no I/O of its own. The management page has already read the folders, the
/// inventory and the local artifact ids by the time the button is reachable, so passing them along
/// makes opening the editor free and — more importantly — keeps every count it shows consistent
/// with the counts on the page behind it.
/// </remarks>
public sealed record IntelligenceCoverageEditorArgs(
    Guid AccountId,
    IReadOnlyCollection<MailItemFolder> Folders,
    IntelligenceCoverageInventory Inventory,
    IReadOnlySet<string> IndexedRemoteMessageIds,
    IReadOnlySet<string> IncludedRemoteFolderIds,
    IReadOnlyList<SemanticIndexFolderCoverageRule> Rules,
    SemanticIndexFolderCoverageRule DefaultRule);

/// <summary>What the coverage editor decided, read by the management page on back navigation.</summary>
public sealed record IntelligenceCoverageResult(
    Guid AccountId,
    IReadOnlyList<string> IncludedRemoteFolderIds,
    IReadOnlyList<SemanticIndexFolderCoverageRule> Rules,
    SemanticIndexFolderCoverageRule DefaultRule);
