#nullable enable
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Accounts;

/// <summary>
/// What shared code needs to know about a mail provider, as data. Each provider module states its own values;
/// <see cref="None"/> is what an unregistered provider type answers: every flag is off.
/// </summary>
public sealed record MailProviderTraits
{
    public static MailProviderTraits None { get; } = new();

    /// <summary>Signs in through the provider's interactive OAuth consent, and asks for it again when a mode is turned on.</summary>
    public bool UsesInteractiveOAuth { get; init; }

    /// <summary>The provider's own API serves calendar, contacts and To Do when the account chooses the provider as their source.</summary>
    public bool ServesModesFromProvider { get; init; }

    /// <summary>Source kind of the address books the provider creates; null when the provider serves no contacts.</summary>
    public ContactSourceKind? ProviderContactSource { get; init; }

    /// <summary>Source kind of the task lists the provider creates; null when the provider serves no To Do.</summary>
    public TaskSourceKind? ProviderTaskSource { get; init; }

    /// <summary>Address books can be created, renamed and deleted on the provider.</summary>
    public bool ManagesProviderContactCollections { get; init; }

    /// <summary>Provider tasks have checklist steps.</summary>
    public bool ManagesProviderTaskSteps { get; init; }

    /// <summary>Calendar and To Do can come from a CalDAV server configured on the account.</summary>
    public bool SupportsDavIntegration { get; init; }

    /// <summary>Mail invitations are answered through the provider's calendar API.</summary>
    public bool AnswersInvitationsThroughProvider { get; init; }

    /// <summary>Archiving moves the message into the account's Archive folder, which must exist.</summary>
    public bool ArchiveRequiresArchiveFolder { get; init; }

    /// <summary>Junk and not-junk are plain folder moves instead of a junk-state change.</summary>
    public bool MovesJunkAsFolderMove { get; init; }

    /// <summary>Category definitions live on the server and synchronize with it.</summary>
    public bool SupportsCategorySync { get; init; }

    /// <summary>
    /// The categories assigned to a mail are read from and written to the server. A provider can do this without a
    /// server category list (<see cref="SupportsCategorySync"/> off): it then knows categories by name only.
    /// </summary>
    public bool SynchronizesCategoryAssignments { get; init; }

    /// <summary>The source stamped on categories that come from the server; null when categories stay on the device.</summary>
    public MailCategorySource? CategorySource { get; init; }

    /// <summary>A contact photo can be written to the address books the provider serves (<see cref="ProviderContactSource"/>).</summary>
    public bool SupportsContactPhotoWrite { get; init; }

    /// <summary>The sender addresses (aliases) of the mailbox are read from the server.</summary>
    public bool SupportsAliasSync { get; init; }

    /// <summary>The display name and picture of the mailbox are read from the server.</summary>
    public bool SupportsProfileInfoSync { get; init; }

    public bool SupportsFocusedInbox { get; init; }

    public bool SupportsProviderMailFilters { get; init; }

    /// <summary>Receiving mail moved from another account needs the opt-in <see cref="ProviderFeature.CrossAccountMoveImport"/>.</summary>
    public bool MailImportRequiresAuthorization { get; init; }

    /// <summary>The server removes an event from the calendar once the user declines it.</summary>
    public bool RemovesDeclinedEvents { get; init; }

    /// <summary>A message can sit in several folders at once (labels), so one remote message maps to several local copies.</summary>
    public bool StoresMessageInSeveralFolders { get; init; }

    /// <summary>Automatic To Do and contact synchronization from the provider runs inside the quota window.</summary>
    public bool UsesQuotaWindowForTasksAndContacts { get; init; }

    /// <summary>The account's mail synchronization range limits what its first synchronization downloads.</summary>
    public bool AppliesMailSynchronizationRange { get; init; }

    /// <summary>The mail synchronization range can be widened after the first synchronization.</summary>
    public bool SupportsMailSynchronizationRangeChange { get; init; }

    /// <summary>Mail changes are tracked for the whole mailbox (the account's delta identifier), not per folder.</summary>
    public bool TracksMailChangesPerAccount { get; init; }

    /// <summary>What delivers live mail updates on desktop.</summary>
    public LiveCoverageKind LiveCoverage { get; init; }
}
