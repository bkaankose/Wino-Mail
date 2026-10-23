#nullable enable
using System;
using System.Collections.Generic;

namespace MailCal.Core.Domain.Models.LocalBackup;

/// <summary>
/// One mailbox in a backup file. Property names are the JSON names in existing backups.
/// </summary>
public sealed class UserMailboxSyncItemDto
{
    public Guid? MailboxId { get; init; }

    public string Address { get; init; } = string.Empty;

    public int ProviderType { get; init; }

    public int SpecialImapProvider { get; init; }

    public string? AccountName { get; init; }

    public string? SenderName { get; init; }

    public string? AccountColorHex { get; init; }

    public int SortOrder { get; init; }

    public bool IsCalendarAccessGranted { get; init; }

    public int CalendarSupportMode { get; init; }

    public string? IncomingServer { get; init; }

    public string? IncomingServerPort { get; init; }

    public string? IncomingServerUsername { get; init; }

    public int? IncomingServerSocketOption { get; init; }

    public int? IncomingAuthenticationMethod { get; init; }

    public string? OutgoingServer { get; init; }

    public string? OutgoingServerPort { get; init; }

    public string? OutgoingServerUsername { get; init; }

    public int? OutgoingServerSocketOption { get; init; }

    public int? OutgoingAuthenticationMethod { get; init; }

    public string? CalDavServiceUrl { get; init; }

    public string? CalDavUsername { get; init; }

    public string? ProxyServer { get; init; }

    public string? ProxyServerPort { get; init; }

    public int? MaxConcurrentClients { get; init; }

    public bool? IsMailAccessGranted { get; init; }

    public bool? ShouldAppendMessagesToSentFolder { get; init; }

    public bool? IsNotificationsEnabled { get; init; }

    public bool? IsFocusedInboxEnabled { get; init; }

    public bool? IsSignatureEnabled { get; init; }

    public bool? IsTaskbarBadgeEnabled { get; init; }

    public bool? IsJumpListEnabled { get; init; }

    public Guid? SignatureIdForNewMessages { get; init; }

    public Guid? SignatureIdForFollowingMessages { get; init; }

    public List<UserMailboxSignatureSyncItemDto>? Signatures { get; init; }

    public List<UserMailboxFolderSyncItemDto>? Folders { get; init; }
}

public sealed class UserMailboxFolderSyncItemDto
{
    public string RemoteFolderId { get; init; } = string.Empty;

    public bool IsSticky { get; init; }

    public bool IsHidden { get; init; }

    public int Order { get; init; }

    public bool ShowUnreadCount { get; init; }

    public bool IsJumpListEnabled { get; init; }
}

public sealed class UserMailboxSignatureSyncItemDto
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string HtmlBody { get; init; } = string.Empty;
}
