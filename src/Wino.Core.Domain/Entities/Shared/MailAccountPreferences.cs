using System;
using System.Collections.Generic;
using System.Linq;
using SQLite;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;

namespace Wino.Core.Domain.Entities.Shared;

public partial class MailAccountPreferences
{
    /// <summary>How many newest messages a folder indexes when nothing else has been chosen.</summary>
    public const int DefaultLatestMessageCount = 100;

    private HashSet<string>? _excludedIntelligenceIndicatorIds;
    private HashSet<string>? _selectedIntelligenceFolderIds;
    private List<SemanticIndexFolderCoverageRule>? _intelligenceFolderCoverageRules;
    private SemanticIndexFolderCoverageRule? _intelligenceDefaultCoverageRule;
    [PrimaryKey]
    public Guid Id { get; set; }

    /// <summary>
    /// Id of the account in MailAccount table.
    /// </summary>
    public Guid AccountId { get; set; }

    /// <summary>
    /// Gets or sets whether sent draft messages should be appended to the sent folder.
    /// Some IMAP servers do this automatically, some don't.
    /// It's disabled by default.
    /// </summary>
    public bool ShouldAppendMessagesToSentFolder { get; set; }

    /// <summary>
    /// Gets or sets whether the notifications are enabled for the account.
    /// </summary>
    public bool IsNotificationsEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether the account has Focused inbox support.
    /// Null if the account provider type doesn't support Focused inbox.
    /// </summary>
    public bool? IsFocusedInboxEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether signature should be appended automatically.
    /// </summary>
    public bool IsSignatureEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether this account's unread items should be included in taskbar badge.
    /// </summary>
    public bool IsTaskbarBadgeEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether this account's enabled folders should be included in the taskbar jump list.
    /// </summary>
    public bool IsJumpListEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether semantic indexing is explicitly enabled for this mail account.
    /// Disabled by default so existing synchronization behavior is unchanged.
    /// </summary>
    public bool IsSemanticIndexingEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether this mailbox contributes facts to Daily Briefing.
    /// This is local mailbox state and is enabled for existing and new accounts.
    /// </summary>
    public bool IsDailyBriefingEnabled { get; set; } = true;

    /// <summary>
    /// Newline-separated stable intelligence indicator ids excluded from local
    /// presentation. The normalized set is exposed through
    /// <see cref="ExcludedIntelligenceIndicatorIds"/> because SQLite cannot map a
    /// mutable set directly.
    /// </summary>
    public string ExcludedIntelligenceIndicatorIdsStorage { get; set; } = string.Empty;

    /// <summary>Whether the user has explicitly configured folders for historical intelligence indexing.</summary>
    public bool IsIntelligenceFolderSelectionInitialized { get; set; }

    /// <summary>Newline-separated provider folder ids included in historical intelligence indexing.</summary>
    public string SelectedIntelligenceFolderIdsStorage { get; set; } = string.Empty;

    public bool IsIntelligenceCoverageInitialized { get; set; }

    public string IntelligenceFolderCoverageStorage { get; set; } = string.Empty;

    /// <summary>
    /// The rule every included folder follows unless it carries one of its own. Most mailboxes
    /// never need per-folder rules, so this is what the coverage editor changes by default.
    /// </summary>
    public string IntelligenceDefaultCoverageStorage { get; set; } = string.Empty;

    public bool AutomaticallyIndexNewMessages { get; set; } = true;

    /// <summary>
    /// Gets the local set of excluded intelligence indicator ids. Unknown and
    /// obsolete values are ignored when the set is materialized from storage.
    /// </summary>
    [Ignore]
    public HashSet<string> ExcludedIntelligenceIndicatorIds
    {
        get => _excludedIntelligenceIndicatorIds ??= IntelligenceIndicatorId.ParsePersisted(ExcludedIntelligenceIndicatorIdsStorage);
        set => _excludedIntelligenceIndicatorIds = IntelligenceIndicatorId.NormalizeExcluded(value);
    }

    /// <summary>Compatibility alias for callers that prefer the plural noun form.</summary>
    [Ignore]
    public IReadOnlySet<string> ExcludedIntelligenceIndicators => ExcludedIntelligenceIndicatorIds;

    [Ignore]
    public HashSet<string> SelectedIntelligenceFolderIds
    {
        get => _selectedIntelligenceFolderIds ??= ParseFolderIds(SelectedIntelligenceFolderIdsStorage);
        set => _selectedIntelligenceFolderIds = NormalizeFolderIds(value);
    }

    /// <summary>
    /// The rule each included folder follows. Folders absent from this list fall back to
    /// <see cref="IntelligenceDefaultCoverageRule"/>.
    /// </summary>
    [Ignore]
    public List<SemanticIndexFolderCoverageRule> IntelligenceFolderCoverageRules
    {
        get => _intelligenceFolderCoverageRules ??= [.. SemanticIndexCoverageRuleStorage.Deserialize(IntelligenceFolderCoverageStorage)];
        set => _intelligenceFolderCoverageRules = value ?? [];
    }

    /// <summary>
    /// The rule a newly included folder starts on. Its <see cref="SemanticIndexFolderCoverageRule.RemoteFolderId"/>
    /// is always empty — it is a template, not a folder's own rule.
    /// </summary>
    [Ignore]
    public SemanticIndexFolderCoverageRule IntelligenceDefaultCoverageRule
    {
        get => _intelligenceDefaultCoverageRule ??= SemanticIndexCoverageRuleStorage.DeserializeDefault(
            IntelligenceDefaultCoverageStorage,
            SemanticIndexFolderCoverageRule.Latest(string.Empty, DefaultLatestMessageCount));
        set => _intelligenceDefaultCoverageRule = value with { RemoteFolderId = string.Empty };
    }

    /// <summary>
    /// Normalizes local intelligence preferences before a SQLite insert or update.
    /// </summary>
    public void PrepareForStorage()
    {
        ExcludedIntelligenceIndicatorIdsStorage = IntelligenceIndicatorId.SerializePersisted(ExcludedIntelligenceIndicatorIds);
        SelectedIntelligenceFolderIdsStorage = string.Join('\n', SelectedIntelligenceFolderIds.OrderBy(static id => id, StringComparer.Ordinal));
        IntelligenceFolderCoverageStorage = SemanticIndexCoverageRuleStorage.Serialize(IntelligenceFolderCoverageRules);
        IntelligenceDefaultCoverageStorage = SemanticIndexCoverageRuleStorage.SerializeDefault(IntelligenceDefaultCoverageRule);
    }

    private static HashSet<string> ParseFolderIds(string? value)
        => NormalizeFolderIds((value ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static HashSet<string> NormalizeFolderIds(IEnumerable<string>? values)
        => values?.Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .ToHashSet(StringComparer.Ordinal) ?? [];

    /// <summary>
    /// Stable id of the range preset the user last chose for semantic indexing.
    /// Null until the user picks one, which is when the one month default applies.
    /// </summary>
    public string SemanticIndexRangePresetId { get; set; }

    /// <summary>
    /// First and last selected message dates, stored only for a custom range.
    /// Dates are kept instead of day offsets because offsets shift as mail arrives.
    /// </summary>
    public DateTime? SemanticIndexRangeCutoffUtc { get; set; }

    public DateTime? SemanticIndexRangeThroughUtc { get; set; }

    /// <summary>
    /// Gets or sets signature for new messages. Null if signature is not needed.
    /// </summary>
    public Guid? SignatureIdForNewMessages { get; set; }

    /// <summary>
    /// Gets or sets signature for following messages. Null if signature is not needed.
    /// </summary>
    public Guid? SignatureIdForFollowingMessages { get; set; }
}
