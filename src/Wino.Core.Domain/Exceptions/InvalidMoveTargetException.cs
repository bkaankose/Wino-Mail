using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Exceptions;

public class InvalidMoveTargetException(InvalidMoveTargetReason reason) : Exception
{
    public InvalidMoveTargetReason Reason { get; } = reason;
}
