#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Voice;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Runs voice plans on the local accounts. Every change goes through <see cref="IWinoRequestDelegator"/>, the same
/// path as the app's own commands; this only finds which mails, folders and accounts each step means.
/// </summary>
public interface IVoicePlanExecutor
{
    /// <summary>
    /// Finds the mails each step acts on without changing anything: counts them into the step text, and turns steps
    /// with no matching mails, or with a folder the account doesn't have, into banners.
    /// </summary>
    Task<VoicePlanResolution> ResolveAsync(VoicePlanPreview preview, CancellationToken cancellationToken = default);

    /// <summary>
    /// The steps of the accepted groups that need a second confirmation: deleting 20 or more mails, emptying a folder
    /// of 50 or more, archiving 100 or more, deleting across all folders or accounts, and deleting mails that are
    /// already in Deleted items, which removes them for good.
    /// </summary>
    IReadOnlyList<VoicePlanRisk> GetRisks(VoicePlanResolution resolution, IReadOnlyCollection<VoicePlanPreviewGroup> acceptedGroups);

    /// <summary>
    /// Runs the steps of <paramref name="acceptedGroups"/> in spoken order. Mail actions, tasks and confident events go
    /// through the request delegator; searches, drafts and events to review open through <paramref name="host"/>.
    /// </summary>
    Task<VoicePlanExecutionResult> ExecuteAsync(
        VoicePlanResolution resolution,
        IReadOnlyCollection<VoicePlanPreviewGroup> acceptedGroups,
        double planConfidence,
        IVoicePlanHost host,
        CancellationToken cancellationToken = default);
}
