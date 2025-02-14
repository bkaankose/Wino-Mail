using System.Text.Json.Serialization;
using Wino.Core.Domain.Models.AutoDiscovery;
using Wino.Core.Domain.Models.Personalization;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Core.Domain.Models;

[JsonSerializable(typeof(AutoDiscoverySettings))]
[JsonSerializable(typeof(CustomThemeMetadata))]
[JsonSerializable(typeof(WebViewMessage))]
public partial class DomainModelsJsonContext: JsonSerializerContext;
