#nullable enable
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Core.Domain.Interfaces;

/// <summary>Per-device relay state in non-roaming configuration.</summary>
public interface IPushRelayStateStore
{
    PushRelayState Load();
    void Save(PushRelayState state);

    /// <summary>Forgets everything except the user's opt-in.</summary>
    void Clear();
}
