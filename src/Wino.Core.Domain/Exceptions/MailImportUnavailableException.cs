#nullable enable
using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Exceptions;

/// <summary>The target account cannot receive moved mail at all right now; no further mail is tried.</summary>
public class MailImportUnavailableException(MailImportUnavailableReason reason, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public MailImportUnavailableReason Reason { get; } = reason;
}
