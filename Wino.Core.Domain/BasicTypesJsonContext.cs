using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wino.Core.Domain;

[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(bool))]
public partial class BasicTypesJsonContext : JsonSerializerContext;
