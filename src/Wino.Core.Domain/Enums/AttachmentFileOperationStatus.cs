#nullable enable
namespace Wino.Core.Domain.Enums;

public enum AttachmentFileOperationStatus
{
    Succeeded,
    Cancelled,
    ConfirmationRequired,
    PolicyBlocked,
    Failed
}
