#nullable enable
using System;
using System.Collections.Generic;

namespace Wino.Core.Domain.Models.PushRelay;

// Client copies of the WinoRelay /v1 contracts (WinoRelay.Api.Contracts). JSON is camelCase with nulls omitted.

/// <summary><c>POST /v1/devices</c>. <c>Name</c> and <c>OsVersion</c> are at most 64 characters.</summary>
public sealed record PushRelayRegisterDeviceRequest(
    string Platform,
    string? PushToken,
    string? AppVersion,
    string? AppBuild,
    string? Locale,
    string? OsVersion,
    string? Name);

public sealed record PushRelayRegisterDeviceResponse(Guid DeviceId, DateTimeOffset ServerTime);

/// <summary><c>PATCH /v1/devices/me</c>. A null field keeps the server value.</summary>
public sealed record PushRelayUpdateDeviceRequest(
    string? PushToken,
    string? AppVersion,
    string? AppBuild,
    string? Locale,
    string? OsVersion,
    string? Name);

public sealed record PushRelayDevice(
    Guid DeviceId,
    string Platform,
    bool PushTokenRegistered,
    DateTimeOffset? PushTokenInvalidatedAt,
    string? PushTokenInvalidReason,
    string? AppVersion,
    string? AppBuild,
    DateTimeOffset CreatedAt,
    List<PushRelayMailbox>? Mailboxes,
    string? Name,
    string? OsVersion);

public sealed record PushRelayPushTestResponse(string Status);

/// <summary><c>PUT /v1/devices/me/accounts</c>: the complete list of local account ids.</summary>
public sealed record PushRelaySyncAccountsRequest(List<string> ClientAccountIds);

public sealed record PushRelaySyncAccountsResponse(int Removed, List<PushRelayMailbox>? Mailboxes);

/// <summary>IMAP credentials uploaded to the relay. <see cref="ToString"/> never prints the password.</summary>
public sealed record PushRelayImapCredentials(
    string Host,
    int Port,
    string Security,
    string Username,
    string? Password,
    string? AuthMethod)
{
    public override string ToString() => $"PushRelayImapCredentials {{ Host = {Host}, Port = {Port}, Security = {Security} }}";
}

/// <summary><c>POST /v1/mailboxes</c>. Gmail and Graph registrations are added with their provider builders.</summary>
public sealed record PushRelayCreateMailboxRequest(string Provider, string ClientAccountId, PushRelayImapCredentials? Imap);

/// <summary><c>PUT /v1/mailboxes/{id}/credentials</c>.</summary>
public sealed record PushRelayUpdateCredentialsRequest(PushRelayImapCredentials? Imap);

public sealed record PushRelayMonitoring(
    string? Kind,
    string? Status,
    string? Reason,
    string? Message,
    bool Retryable,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? ExpiresAt,
    int? PollIntervalSeconds,
    string? ProviderError,
    DateTimeOffset? UpdatedAt);

public sealed record PushRelayHints(string? Folder, long? UidValidity, long? UidNext, long? HighestModSeq, int? Exists, string? HistoryId);

/// <summary>One subscription of this device. <see cref="Id"/> is the subscription id used in mailbox routes.</summary>
public sealed record PushRelayMailbox(
    Guid Id,
    Guid MailboxId,
    string? Provider,
    string? Identity,
    string? ClientAccountId,
    string? Status,
    string? StatusDetail,
    PushRelayMonitoring? Monitoring,
    PushRelayHints? Hints,
    DateTimeOffset? LastChangeAt,
    DateTimeOffset? LastPushAt,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RegistrationExpiresAt,
    bool Muted);

public sealed record PushRelayMailboxList(List<PushRelayMailbox>? Items);

public sealed record PushRelayResyncResponse(string Status, PushRelayMonitoring? Monitoring);

public sealed record PushRelayMinAppVersion(string? Ios, string? Android, string? Macos);

/// <summary><c>GET /.well-known/winorelay</c>.</summary>
public sealed record PushRelayWellKnown(
    string? Version,
    int ApiVersion,
    List<string>? EgressIps,
    PushRelayMinAppVersion? MinAppVersion,
    List<string>? Providers,
    string? GmailTopic,
    string? GraphNotificationUrl,
    string? GraphLifecycleUrl);

/// <summary>
/// What a provider builder produces for one account: the create request, the credential update request and a
/// fingerprint of the uploaded credentials. <see cref="ToString"/> never prints credentials.
/// </summary>
public sealed record PushRelayMailboxRegistrationRequest(
    PushRelayCreateMailboxRequest Create,
    PushRelayUpdateCredentialsRequest Update,
    string CredentialFingerprint)
{
    public override string ToString() => $"PushRelayMailboxRegistrationRequest {{ Provider = {Create.Provider}, ClientAccountId = {Create.ClientAccountId} }}";
}
