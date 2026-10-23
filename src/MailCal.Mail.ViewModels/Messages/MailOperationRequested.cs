using System.Collections.Generic;
using MailCal.Core.Domain.Enums;
using MailCal.Mail.ViewModels.Data;

namespace MailCal.Mail.ViewModels.Messages;

/// <summary>
/// Identifies the UI surface that requested a mail operation.
/// </summary>
public enum MailOperationTriggerSource
{
    Swipe,
    Idle,
    Hover,
    Other
}

/// <summary>
/// Requests a mail operation from a UI surface.
/// </summary>
public record MailOperationRequested(
    MailOperation Operation,
    MailOperationTriggerSource TriggerSource,
    IReadOnlyList<MailItemViewModel>? MailItems = null);
