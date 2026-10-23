using System.Text.Json.Serialization;
using MailCal.Core.Domain.Models.Connectivity;

namespace MailCal.Services;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(KnownImapProviderCatalogDocument))]
internal sealed partial class KnownImapProviderCatalogJsonContext : JsonSerializerContext;
