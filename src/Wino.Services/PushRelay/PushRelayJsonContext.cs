#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Services.PushRelay;

/// <summary>Source-generated JSON for every relay contract and the persisted relay state (Native AOT).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PushRelayRegisterDeviceRequest))]
[JsonSerializable(typeof(PushRelayRegisterDeviceResponse))]
[JsonSerializable(typeof(PushRelayUpdateDeviceRequest))]
[JsonSerializable(typeof(PushRelayDevice))]
[JsonSerializable(typeof(PushRelayPushTestResponse))]
[JsonSerializable(typeof(PushRelaySyncAccountsRequest))]
[JsonSerializable(typeof(PushRelaySyncAccountsResponse))]
[JsonSerializable(typeof(PushRelayCreateMailboxRequest))]
[JsonSerializable(typeof(PushRelayUpdateCredentialsRequest))]
[JsonSerializable(typeof(PushRelayMailbox))]
[JsonSerializable(typeof(PushRelayMailboxList))]
[JsonSerializable(typeof(PushRelayResyncResponse))]
[JsonSerializable(typeof(PushRelayWellKnown))]
[JsonSerializable(typeof(PushRelayState))]
[JsonSerializable(typeof(PushRelayDeviceIdentity))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
public sealed partial class PushRelayJsonContext : JsonSerializerContext;
