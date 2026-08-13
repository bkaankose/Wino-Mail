using System.Text.Json.Serialization;
using Wino.Mail.AI.Abstractions;

namespace Wino.Services;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(MailTranslationResult))]
internal sealed partial class WinoIntelligenceJsonContext : JsonSerializerContext;
