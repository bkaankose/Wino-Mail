using System;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Exceptions;

public class InvalidMoveTargetException(InvalidMoveTargetReason reason) : Exception
{
    public InvalidMoveTargetReason Reason { get; } = reason;
}
