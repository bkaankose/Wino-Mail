#nullable enable

namespace Wino.Core.Domain.Models.Intelligence;

/// <summary>
/// Locates a message body in the local store or at the provider.
/// This is a client concern - it describes how this app fetches a body - so it lives in
/// the app domain rather than in the shared AI package.
/// </summary>
public sealed record MailBodyLocator(
    string RemoteMessageId,
    string RemoteFolderId,
    uint? ImapUid = null,
    uint? ImapUidValidity = null,
    string? ProviderMessageId = null);
