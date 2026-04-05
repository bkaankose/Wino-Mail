using System.Collections.Generic;
using System.Text.Json.Serialization;
using Wino.Core.Domain.Models.Updates;

namespace Wino.Core.Domain;

[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(UpdateNotes))]
[JsonSerializable(typeof(List<UpdateNoteSection>))]
public partial class BasicTypesJsonContext : JsonSerializerContext;
