using System.Collections.Generic;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels.Messages;

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
