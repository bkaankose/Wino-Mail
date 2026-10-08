using System.Text.Json.Serialization;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Services;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(KnownImapProviderCatalogDocument))]
internal sealed partial class KnownImapProviderCatalogJsonContext : JsonSerializerContext;
