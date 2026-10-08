#nullable enable
using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Platform;

public sealed record PlatformOperationResult(PlatformOperationStatus Status, string? ErrorMessage = null)
{
    public bool IsSuccess => Status == PlatformOperationStatus.Succeeded;

    public void ThrowIfNotSucceeded()
    {
        switch (Status)
        {
            case PlatformOperationStatus.Succeeded:
                return;
            case PlatformOperationStatus.Cancelled:
                throw new OperationCanceledException(ErrorMessage);
            case PlatformOperationStatus.Unavailable:
                throw new PlatformNotSupportedException(ErrorMessage ?? "The platform operation is unavailable.");
            default:
                throw new InvalidOperationException(ErrorMessage ?? "The platform operation failed.");
        }
    }
}
