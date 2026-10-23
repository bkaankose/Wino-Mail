using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Synchronization;

namespace MailCal.Messaging.Server;

/// <summary>
/// Triggers a new mail synchronization if possible.
/// </summary>
/// <param name="Options">Options for synchronization.</param>
public record NewMailSynchronizationRequested(MailSynchronizationOptions Options) : IClientMessage, IUIMessage;

/// <summary>
/// Triggers a new calendar synchronization if possible.
/// </summary>
/// <param name="Options">Options for synchronization.</param>
public record NewCalendarSynchronizationRequested(CalendarSynchronizationOptions Options) : IClientMessage, IUIMessage;

/// <summary>
/// Triggers a new contacts synchronization if possible.
/// </summary>
/// <param name="Options">Options for synchronization.</param>
public record NewContactSynchronizationRequested(ContactSynchronizationOptions Options) : IClientMessage, IUIMessage;

/// <summary>
/// Triggers a new task synchronization if possible.
/// </summary>
/// <param name="Options">Options for synchronization.</param>
public record NewTaskSynchronizationRequested(TaskSynchronizationOptions Options) : IClientMessage, IUIMessage;
