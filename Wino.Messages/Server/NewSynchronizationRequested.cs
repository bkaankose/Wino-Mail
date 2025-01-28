using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Messaging.Server
{
    /// <summary>
    /// Triggers a new mail synchronization if possible.
    /// </summary>
    /// <param name="Options">Options for synchronization.</param>
    public record NewMailSynchronizationRequested(MailSynchronizationOptions Options, SynchronizationSource Source) : IClientMessage, IUIMessage;

    /// <summary>
    /// Triggers a new calendar synchronization if possible.
    /// </summary>
    /// <param name="Options">Options for synchronization.</param>
    public record NewCalendarSynchronizationRequested(CalendarSynchronizationOptions Options, SynchronizationSource Source) : IClientMessage;
}
