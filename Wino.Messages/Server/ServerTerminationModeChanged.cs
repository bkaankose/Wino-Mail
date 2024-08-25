using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Messaging.Server
{
    /// <summary>
    /// App close behavior for server is changed.
    /// </summary>
    /// <param name="ServerBackgroundMode">New server background mode.</param>
    public record ServerTerminationModeChanged(ServerBackgroundMode ServerBackgroundMode) : IClientMessage;
}
