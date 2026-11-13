#nullable enable
using System;

namespace Wino.Core.Domain.Exceptions;

/// <summary>An invitation answer or removal failed; the message is localized for the user.</summary>
public sealed class MailInvitationException : Exception
{
    public MailInvitationException(string message, Exception? innerException = null) : base(message, innerException) { }
}
