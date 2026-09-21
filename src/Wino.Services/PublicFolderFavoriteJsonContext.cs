using System.Collections.Generic;
using System.Text.Json.Serialization;
using Wino.Core.Domain.Models.PublicFolders;

namespace Wino.Services;

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<PublicFolderFavorite>))]
internal sealed partial class PublicFolderFavoriteJsonContext : JsonSerializerContext;
