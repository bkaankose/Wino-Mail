using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.AutoDiscovery;

namespace Wino.Core.Services
{
    /// <summary>
    /// We have 2 methods to do auto discovery.
    /// 1. Use https://emailsettings.firetrust.com/settings?q={address} API
    /// 2. TODO: Thunderbird auto discovery file.
    /// </summary>
    public class AutoDiscoveryService : IAutoDiscoveryService
    {
        private const string FiretrustURL = " https://emailsettings.firetrust.com/settings?q=";

        // TODO: Try Thunderbird Auto Discovery as second approach.

        public Task<AutoDiscoverySettings> GetAutoDiscoverySettings(AutoDiscoveryMinimalSettings autoDiscoveryMinimalSettings)
            => GetSettingsFromFiretrustAsync(autoDiscoveryMinimalSettings.Email);

        private async Task<AutoDiscoverySettings> GetSettingsFromFiretrustAsync(string mailAddress)
        {
            using var client = new HttpClient();
            var response = await client.GetAsync($"{FiretrustURL}{mailAddress}");

            if (response.IsSuccessStatusCode)
                return await DeserializeFiretrustResponse(response);
            else
            {
                Log.Warning($"Firetrust AutoDiscovery failed. ({response.StatusCode})");

                return null;
            }
        }

        private async Task<AutoDiscoverySettings> DeserializeFiretrustResponse(HttpResponseMessage response)
        {
            try
            {
                var content = await response.Content.ReadAsStringAsync();

                return JsonConvert.DeserializeObject<AutoDiscoverySettings>(content);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to deserialize Firetrust response.");
            }

            return null;
        }
    }
}
