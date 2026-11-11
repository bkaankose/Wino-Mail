using Wino.Agent.Runtime.Brain;
using Wino.Agent.Runtime.Plan;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Mail.Api.Contracts.Assistant;

namespace Wino.Agent.Runtime.Ports;

/// <summary>The classifier transport: the app's account API client over the interpret socket and the compose endpoint.</summary>
public interface IReadingClient
{
    Task<AssistantReading> ReadAsync(AssistantReadRequest request, CancellationToken ct);

    Task<AssistantComposeResult> ComposeAsync(AssistantComposeRequest request, CancellationToken ct);
}

/// <summary>
/// Read-only questions the runtime asks the device. Everything returned stays on the device; the runtime puts ids,
/// addresses and display names into plans and the brain, never content. Lists are the account's current state;
/// the runtime snapshots them once per turn.
/// </summary>
public interface IAgentDomainQueries
{
    /// <summary>Every account in the user's order.</summary>
    Task<IReadOnlyList<MailAccount>> GetAccountsAsync(CancellationToken ct);

    Task<IReadOnlyList<MailAccountAlias>> GetAliasesAsync(Guid accountId, CancellationToken ct);

    Task<IReadOnlyList<MailItemFolder>> GetFoldersAsync(Guid accountId, CancellationToken ct);

    Task<IReadOnlyList<AccountCalendar>> GetCalendarsAsync(Guid accountId, CancellationToken ct);

    Task<IReadOnlyList<AccountTaskList>> GetTaskListsAsync(Guid accountId, CancellationToken ct);

    Task<IReadOnlyList<MailCategory>> GetCategoriesAsync(Guid accountId, CancellationToken ct);

    Task<IReadOnlyList<ContactList>> GetContactListsAsync(CancellationToken ct);

    /// <summary>
    /// Contacts whose display name words or address local part start with any word of <paramref name="query"/>
    /// (case and accents ignored), top correspondents first, at most <paramref name="limit"/>. An empty query
    /// returns the top correspondents. The runtime ranks them itself.
    /// </summary>
    Task<IReadOnlyList<AgentContact>> SearchContactsAsync(string query, int limit, CancellationToken ct);

    /// <summary>Senders (from received mail) whose name, address or domain contains <paramref name="query"/>, most frequent first.</summary>
    Task<IReadOnlyList<AgentSender>> SearchSendersAsync(string query, int limit, CancellationToken ct);

    Task<int> CountMailsAsync(Guid? accountId, MailQuery query, CancellationToken ct);

    Task<IReadOnlyList<AgentEventSummary>> FindEventsAsync(Guid? accountId, EventQuery query, int limit, CancellationToken ct);

    Task<IReadOnlyList<AgentTaskSummary>> FindTasksAsync(Guid? accountId, TaskQuery query, int limit, CancellationToken ct);

    /// <summary>Busy intervals of the account's calendars (all accounts when null) for free-slot search.</summary>
    Task<IReadOnlyList<AgentBusyInterval>> GetBusyIntervalsAsync(Guid? accountId, DateTime from, DateTime to, CancellationToken ct);
}

/// <summary><see cref="IsTopCorrespondent"/>: the user mails this address often (the device's own measure).</summary>
public sealed record AgentContact(Guid? ContactId, string Address, string? Name, bool IsTopCorrespondent, Guid? AccountId = null);

public sealed record AgentSender(string Address, string? Name, string Domain, int Count);

public sealed record AgentEventSummary(Guid Id, Guid AccountId, Guid CalendarId, string Title, DateTime Start, DateTime End, bool AllDay, bool IsOrganizer, int AttendeeCount, bool Recurring);

public sealed record AgentTaskSummary(Guid Id, Guid AccountId, Guid ListId, string Title, DateTime? Due, bool Completed, bool Important);

public sealed record AgentBusyInterval(DateTime Start, DateTime End);

/// <summary>"Remember this?" after a plan ran. True saves the proposal, false declines it for 30 days.</summary>
public interface IAgentPrompter
{
    Task<bool> ProposeAsync(BrainProposal proposal, CancellationToken ct);
}

/// <summary>
/// Storage for the brain document (an opaque byte array the app keeps encrypted at rest with a device key) and for
/// the small secrets of brain sync (the derived key). The runtime owns the format; the app owns the bytes.
/// </summary>
public interface IAgentBrainStorage
{
    Task<byte[]?> ReadAsync(CancellationToken ct);

    Task WriteAsync(byte[] document, CancellationToken ct);

    Task DeleteAsync(CancellationToken ct);

    byte[]? ReadSecret(string name);

    void WriteSecret(string name, byte[] value);

    void DeleteSecret(string name);
}

/// <summary>The device's clock and the user's time preferences, read at each request.</summary>
public interface IAgentClock
{
    DateTime LocalNow { get; }

    string TimeZoneId { get; }

    DayOfWeek WeekStart { get; }

    TimeOnly? WorkDayStart { get; }

    TimeOnly? WorkDayEnd { get; }

    string? UiCulture { get; }

    IReadOnlyList<string> ExpectedLanguages { get; }
}

/// <summary>Runs a resolved plan in step order, chaining <see cref="StepTarget.FromStep"/> targets from earlier outcomes; reports light outcomes only.</summary>
public interface IAgentPlanExecutor
{
    Task<PlanOutcome> ExecuteAsync(ResolvedPlan plan, IProgress<StepOutcome> progress, CancellationToken ct);
}

/// <summary>
/// Brain sync transport (phase 5): the encrypted blob on the server, one row per user, with its revision. The runtime
/// never sends a plain document through it. Not used by the runtime yet; <see cref="IBrainBlobCodec"/> and
/// <see cref="BrainMerge"/> are the other halves of the seam.
/// </summary>
public interface IBrainSyncTransport
{
    Task<string?> GetRevisionAsync(CancellationToken ct);

    Task<BrainBlob?> DownloadAsync(CancellationToken ct);

    /// <summary>Uploads with <c>If-Match</c>; returns the new revision or throws <see cref="BrainSyncConflictException"/>.</summary>
    Task<string> UploadAsync(byte[] blob, string? ifMatchRevision, CancellationToken ct);

    Task DeleteAsync(CancellationToken ct);
}

public sealed record BrainBlob(byte[] Bytes, string Revision);

public sealed class BrainSyncConflictException(string currentRevision) : Exception("The server holds a newer brain")
{
    public string CurrentRevision { get; } = currentRevision;
}

/// <summary>Turns a brain document into the encrypted sync blob and back (phase 5: Argon2id + AES-GCM, <c>WBRN</c> header).</summary>
public interface IBrainBlobCodec
{
    byte[] Encrypt(BrainDocument document, byte[] key);

    BrainDocument Decrypt(byte[] blob, byte[] key);
}
