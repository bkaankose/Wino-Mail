using System.Text.Json.Serialization;

namespace Wino.Core.Domain.Models.AutoDiscovery
{
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
    }
}
