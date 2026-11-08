using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Reader;

/// <summary>
/// Which remote content mode the reader applies to a message, and why.
/// </summary>
/// <param name="EffectiveMode">The mode the renderer applies. An Images only rule maps to Block trackers, a Block rule to Block all.</param>
public sealed record RemoteContentDecision(
    RemoteContentMode EffectiveMode,
    RemoteContentDecisionReason Reason,
    RemoteContentSenderRule? Rule,
    string SenderAddress,
    string SenderDomain);
