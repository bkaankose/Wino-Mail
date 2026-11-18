using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wino.Providers.Gmail.Google;

public sealed class GoogleApiException : Exception
{
    public GoogleApiException(System.Net.HttpStatusCode httpStatusCode, GoogleApiError error)
        : base(error?.Message ?? $"Google API request failed with HTTP {(int)httpStatusCode}.")
    {
        HttpStatusCode = httpStatusCode;
        Error = error;
    }

    public System.Net.HttpStatusCode HttpStatusCode { get; }

    public GoogleApiError Error { get; }
}

public sealed class GoogleApiErrorEnvelope
{
    public GoogleApiError Error { get; set; }
}

public sealed class GoogleApiError
{
    public int Code { get; set; }

    public string Message { get; set; }

    public string Status { get; set; }

    public List<GoogleApiErrorDetail> Errors { get; set; }
}

public sealed class GoogleApiErrorDetail
{
    public string Domain { get; set; }

    public string Message { get; set; }

    public string Reason { get; set; }

    public override string ToString() => Message ?? Reason ?? string.Empty;
}
