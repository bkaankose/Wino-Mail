using System.Text.Json.Serialization;

namespace Wino.Core.MacOS.Bindings.StoreKit2;

/// <summary>The reply every WinoStoreKit2 export sends: a value, or an error message.</summary>
internal sealed record StoreKit2Envelope<T>(bool Ok, T? Value, string? Error);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(StoreKit2Envelope<StoreKit2Storefront>))]
[JsonSerializable(typeof(StoreKit2Envelope<StoreKit2Product[]>))]
[JsonSerializable(typeof(StoreKit2Envelope<StoreKit2PurchaseResult>))]
[JsonSerializable(typeof(StoreKit2Envelope<StoreKit2Transaction>))]
[JsonSerializable(typeof(StoreKit2Envelope<StoreKit2Transaction[]>))]
[JsonSerializable(typeof(StoreKit2Envelope<bool>))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class StoreKit2JsonContext : JsonSerializerContext;
