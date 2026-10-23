using System.Collections.Generic;
using System.Text.Json.Serialization;
using MailCal.Core.Domain.Models.WhatsNew;

namespace MailCal.Core.Domain;

[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(WhatsNewRelease))]
public partial class BasicTypesJsonContext : JsonSerializerContext;
