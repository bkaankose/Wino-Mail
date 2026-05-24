using System.Text.Json;
using System.Text.Json.Serialization;

#nullable enable

namespace Wino.Messaging.SyncHost;

public static class SyncHostJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        ReferenceHandler = ReferenceHandler.IgnoreCycles
    };

    public static JsonElement ToElement<T>(T value)
        => JsonSerializer.SerializeToElement(value, Options);

    public static T? FromElement<T>(JsonElement element)
        => element.Deserialize<T>(Options);
}
