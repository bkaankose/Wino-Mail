using System;
using System.IO;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

internal sealed class EasCommandException(string command, string status)
    : IOException(string.Format(Wino.Core.Domain.Translator.ExchangeSetup_ActiveSyncCommandFailed, command, status))
{
    internal string Status { get; } = status;
}

/// <summary>The server refuses this device or mailbox until an administrator changes something; retrying does not help.</summary>
internal sealed class EasAccessDeniedException(string message, string status) : InvalidOperationException(message)
{
    internal string Status { get; } = status;
}

/// <summary>The device policy cannot be satisfied (MS-ASCMD status 139, 141, 145, or a provisioning loop).</summary>
internal sealed class EasPolicyException(string message, string status) : InvalidOperationException(message)
{
    internal string Status { get; } = status;
}

/// <summary>The administrator wiped the account from this device; its data and credentials are erased and the partnership is not used again.</summary>
internal sealed class EasRemoteWipeException(string message) : InvalidOperationException(message);

/// <summary>HTTP 503, 429 or MS-ASCMD status 111: the server asks the client to back off.</summary>
internal sealed class EasServerBusyException(string message, TimeSpan? retryAfter) : IOException(message)
{
    internal TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>The account's synchronized data was discarded at the server's request; the operation starts over.</summary>
internal sealed class EasResyncRequiredException(string message) : IOException(message);

/// <summary>The response is larger than the client accepts.</summary>
internal sealed class EasResponseTooLargeException() : IOException(Wino.Core.Domain.Translator.ExchangeSetup_ActiveSyncResponseTooLarge);
