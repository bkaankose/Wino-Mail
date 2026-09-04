#nullable enable
using System.Text.Json.Serialization;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Services;

[JsonSerializable(typeof(WinoAccountIntelligenceSnapshot))]
[JsonSerializable(typeof(SemanticIndexFolderCoverageRule[]))]
[JsonSerializable(typeof(MessageAttachmentMetadataV1[]))]
internal sealed partial class LocalIntelligenceJsonContext : JsonSerializerContext;
