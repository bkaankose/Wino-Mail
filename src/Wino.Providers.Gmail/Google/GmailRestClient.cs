using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;

namespace Wino.Providers.Gmail.Google;

public sealed class GmailService : IDisposable, IGoogleBatchService
{
    public GmailService(HttpClient httpClient)
    {
        HttpClient = httpClient;
        Users = new global::Wino.Providers.Gmail.Google.Apis.Gmail.v1.UsersResource(httpClient, this);
    }

    public global::Wino.Providers.Gmail.Google.Apis.Gmail.v1.UsersResource Users { get; }

    public HttpClient HttpClient { get; }

    public string BatchEndpoint => "https://gmail.googleapis.com/batch/gmail/v1";

    public void Dispose()
    {
    }
}
