using Newtonsoft.Json;

namespace Wino.Core.Domain.Models.AutoDiscovery
{
    public class AutoDiscoveryProviderSetting
    {
        [JsonProperty("protocol")]
        public string Protocol { get; set; }

        [JsonProperty("address")]
        public string Address { get; set; }

        [JsonProperty("port")]
        public int Port { get; set; }

        [JsonProperty("secure")]
        public string Secure { get; set; }

        [JsonProperty("username")]
        public string Username { get; set; }
    }
}
