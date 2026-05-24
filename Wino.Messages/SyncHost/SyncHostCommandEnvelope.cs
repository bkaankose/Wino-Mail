using System;
using System.Text.Json;

#nullable enable

namespace Wino.Messaging.SyncHost;

public sealed record SyncHostCommandEnvelope(
    int ProtocolVersion,
    Guid CorrelationId,
    string CommandType,
    JsonElement Payload);

public sealed record SyncHostResponseEnvelope(
    int ProtocolVersion,
    Guid CorrelationId,
    bool Success,
    JsonElement Payload,
    string? ErrorType = null,
    string? ErrorMessage = null);

public sealed record SyncHostEventEnvelope(
    int ProtocolVersion,
    Guid EventId,
    string EventType,
    JsonElement Payload);
