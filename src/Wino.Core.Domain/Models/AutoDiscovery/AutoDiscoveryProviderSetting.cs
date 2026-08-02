using System.Text.Json.Serialization;

using System.Collections.Generic;

namespace Wino.Core.Domain.Models.AutoDiscovery;

public class AutoDiscoveryProviderSetting
{
    [JsonPropertyName("protocol")]
    public string Protocol { get; set; }

    [JsonPropertyName("address")]
    public string Address { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("secure")]
    public string Secure { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; }

    [JsonPropertyName("authentication")]
    public List<string> AuthenticationMethods { get; set; } = [];
}
