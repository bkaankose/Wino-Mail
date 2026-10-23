#nullable enable
namespace MailCal.Core.Domain.Enums;

public enum AttachmentFileOperationStatus
{
    Succeeded,
    Cancelled,
    ConfirmationRequired,
    PolicyBlocked,
    Failed,
    Unavailable
}
