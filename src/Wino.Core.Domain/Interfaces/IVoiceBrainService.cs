#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Voice;
using Wino.Mail.Api.Contracts.Voice;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Personal voice mode on this device: the Wino Account's mode (read from the server and cached), the brain sections
/// this device derives from its local mailboxes and pushes, and the learned facts. Everything runs off the UI thread
/// and never throws to the caller; failures are logged and retried on the next trigger.
/// <para>
/// No change, no sync: synchronization only marks an account's section dirty. Dirty sections are rebuilt at most every
/// 30 minutes, or when a voice command starts and the last rebuild is older than five minutes, and a rebuilt section
/// is sent only when what it says changed (<c>VoiceBrainSectionBuilder.ValueHash</c>). Facts the brain already has are
/// not sent again.
/// </para>
/// </summary>
public interface IVoiceBrainService
{
    /// <summary>The last known mode of the signed-in Wino Account; Private until the server says otherwise.</summary>
    VoiceMode CurrentMode { get; }

    /// <summary>Loads the cached mode and subscribes to account and sync changes. Nothing is sent at startup.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the mode from the server. Returns the cached mode when the server can't be reached.</summary>
    Task<VoiceMode> RefreshModeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches the mode. Personal starts pushing this device's sections; Private deletes the brain on the server and
    /// clears the local caches. Returns false when the server refused or could not be reached.
    /// </summary>
    Task<bool> SetModeAsync(VoiceMode mode, CancellationToken cancellationToken = default);

    /// <summary>The mode a voice request was planned in, as the API reported it.</summary>
    void ApplyReportedMode(VoiceMode mode);

    /// <summary>Asks for a debounced push of every mailbox section; unchanged sections are skipped.</summary>
    void RequestPush();

    /// <summary>A voice command is starting: dirty sections are pushed when the last rebuild is older than five minutes.</summary>
    void NotifyVoiceStarting();

    /// <summary>Downloads the brain, falling back to the cached copy when the server can't be reached.</summary>
    Task<VoiceBrainSnapshot?> GetBrainAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves an answer as a learned fact. Only in Personal mode; failures are logged and dropped.</summary>
    Task AddFactAsync(AddBrainFactRequest request, CancellationToken cancellationToken = default);

    Task<bool> DeleteFactAsync(string factId, CancellationToken cancellationToken = default);

    /// <summary>Deletes everything the server remembers; the mode stays Personal and the sections are pushed again later.</summary>
    Task<bool> DeleteBrainAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Key-value storage for voice preferences and caches in the device-local intelligence database
/// (WinoMailIntelligence.db), next to the other Wino Intelligence data.
/// </summary>
public interface IVoiceBrainStore
{
    Task<string?> GetVoiceValueAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>A null <paramref name="value"/> removes the key.</summary>
    Task SetVoiceValueAsync(string key, string? value, CancellationToken cancellationToken = default);

    Task ClearVoiceValuesAsync(CancellationToken cancellationToken = default);
}
