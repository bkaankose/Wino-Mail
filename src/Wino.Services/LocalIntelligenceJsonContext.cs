#nullable enable
using System.Text.Json.Serialization;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;

namespace Wino.Services;

[JsonSerializable(typeof(WinoAccountIntelligenceSnapshot))]
[JsonSerializable(typeof(SemanticIndexFolderCoverageRule[]))]
internal sealed partial class LocalIntelligenceJsonContext : JsonSerializerContext;
