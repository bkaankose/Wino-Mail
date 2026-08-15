#nullable enable
using System.Text.Json.Serialization;
using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Services;

[JsonSerializable(typeof(WinoAccountIntelligenceSnapshot))]
internal sealed partial class LocalIntelligenceJsonContext : JsonSerializerContext;
