using System.Collections.Generic;
using System.Text.Json.Serialization;
using MailCal.Core.Domain.Models.AutoDiscovery;
using MailCal.Core.Domain.Models.Personalization;
using MailCal.Core.Domain.Models.Reader;

namespace MailCal.Core.Domain.Models;

[JsonSerializable(typeof(AutoDiscoverySettings))]
[JsonSerializable(typeof(CustomThemeMetadata))]
[JsonSerializable(typeof(WebViewMessage))]
[JsonSerializable(typeof(List<ImageInfo>))]
public partial class DomainModelsJsonContext : JsonSerializerContext;
