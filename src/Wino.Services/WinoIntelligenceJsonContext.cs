using System.Text.Json.Serialization;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.AI.Abstractions;

namespace Wino.Services;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(MailTranslationResult))]
[JsonSerializable(typeof(ClassificationSignals))]
internal sealed partial class WinoIntelligenceJsonContext : JsonSerializerContext;
