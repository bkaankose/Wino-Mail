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
    // RFC 8058 section 3.2: the key/value pair of List-Unsubscribe-Post is the request body.
    private const string OneClickBody = "List-Unsubscribe=One-Click";

    public async Task<bool> OneClickUnsubscribeAsync(UnsubscribeInfo info)
    {
        var target = info?.IsOneClick == true ? info.OneClickUrl ?? info.HttpLink : null;

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            Log.Warning("A one-click unsubscribe was asked for without an https address.");
            return false;
        }

        try
        {
            // RFC 8058 section 3.1: no cookies or other context, and the sender must not redirect.
            using var handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false, UseDefaultCredentials = false };
            using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

            using var unsubscribeRequest = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(OneClickBody, Encoding.ASCII, "application/x-www-form-urlencoded")
            };

            using var result = await httpClient.SendAsync(unsubscribeRequest).ConfigureAwait(false);

            return result.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to unsubscribe from {Host} - {Message}", uri.Host, ex.Message);
        }

        return false;
    }
}
