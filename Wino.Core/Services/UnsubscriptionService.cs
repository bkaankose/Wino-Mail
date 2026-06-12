using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Core.Services;

public class UnsubscriptionService : IUnsubscriptionService
{
    public async Task<bool> OneClickUnsubscribeAsync(UnsubscribeInfo info)
    {
        try
        {
            using var httpClient = new HttpClient();

            var unsubscribeRequest = new HttpRequestMessage(HttpMethod.Post, info.HttpLink)
            {
                Content = new StringContent("List-Unsubscribe=One-Click", Encoding.UTF8, "application/x-www-form-urlencoded")
            };

            var result = await httpClient.SendAsync(unsubscribeRequest).ConfigureAwait(false);

            return result.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to unsubscribe from {HttpLink} - {Message}", info.HttpLink, ex.Message);
        }

        return false;
    }
}
