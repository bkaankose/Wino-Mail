using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Wino.Core.Outlook;

public sealed class OutlookContactsClient
{
    private static readonly Dictionary<string, ParsableFactory<IParsable>> ErrorMapping = new()
    {
        ["4XX"] = ODataError.CreateFromDiscriminatorValue,
        ["5XX"] = ODataError.CreateFromDiscriminatorValue
    };

    private readonly IRequestAdapter _adapter;
    public OutlookContactsClient(IRequestAdapter adapter) => _adapter = adapter;

    public Task<OutlookContactFolderCollectionResponse> GetContactFoldersAsync(string url = null, CancellationToken cancellationToken = default)
        => SendCollectionAsync(url ?? "https://graph.microsoft.com/v1.0/me/contactFolders?$top=100", OutlookContactFolderCollectionResponse.CreateFromDiscriminatorValue, cancellationToken);

    public Task<OutlookContactFolderCollectionResponse> GetChildFoldersAsync(string folderId, string url = null, CancellationToken cancellationToken = default)
        => SendCollectionAsync(url ?? $"https://graph.microsoft.com/v1.0/me/contactFolders/{Uri.EscapeDataString(folderId)}/childFolders?$top=100", OutlookContactFolderCollectionResponse.CreateFromDiscriminatorValue, cancellationToken);

    public Task<OutlookContactCollectionResponse> GetDeltaAsync(string folderId, string deltaLink = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);

        var url = deltaLink ?? $"https://graph.microsoft.com/v1.0/me/contactFolders/{Uri.EscapeDataString(folderId)}/contacts/delta";
        return SendCollectionAsync(url, OutlookContactCollectionResponse.CreateFromDiscriminatorValue, cancellationToken, preferPageSize: true);
    }

    public Task<OutlookContactCollectionResponse> GetDefaultContactsAsync(string url = null, CancellationToken cancellationToken = default)
        => SendCollectionAsync(url ?? "https://graph.microsoft.com/v1.0/me/contacts?$top=100", OutlookContactCollectionResponse.CreateFromDiscriminatorValue, cancellationToken);

    public Task<Contact> GetContactAsync(string remoteId, CancellationToken cancellationToken = default)
        => SendAsync<Contact>($"https://graph.microsoft.com/v1.0/me/contacts/{Uri.EscapeDataString(remoteId)}", Method.GET, Contact.CreateFromDiscriminatorValue, cancellationToken);

    public Task<Contact> CreateContactAsync(string folderId, Contact contact, CancellationToken cancellationToken = default)
        => SendParsableAsync(folderId == "default"
            ? "https://graph.microsoft.com/v1.0/me/contacts"
            : $"https://graph.microsoft.com/v1.0/me/contactFolders/{Uri.EscapeDataString(folderId)}/contacts", Method.POST, contact, cancellationToken);

    public Task<Contact> UpdateContactAsync(string remoteId, Contact contact, CancellationToken cancellationToken = default)
        => SendParsableAsync($"https://graph.microsoft.com/v1.0/me/contacts/{Uri.EscapeDataString(remoteId)}", Method.PATCH, contact, cancellationToken);

    public Task DeleteContactAsync(string remoteId, CancellationToken cancellationToken = default)
        => SendNoContentAsync($"https://graph.microsoft.com/v1.0/me/contacts/{Uri.EscapeDataString(remoteId)}", Method.DELETE, null, cancellationToken);

    public Task SetPhotoAsync(string remoteId, byte[] photo, CancellationToken cancellationToken = default)
        => SendNoContentAsync($"https://graph.microsoft.com/v1.0/me/contacts/{Uri.EscapeDataString(remoteId)}/photo/$value", Method.PUT, photo, cancellationToken);

    public async Task<byte[]> GetPhotoAsync(string remoteId, CancellationToken cancellationToken = default)
    {
        var request = CreateRequest($"https://graph.microsoft.com/v1.0/me/contacts/{Uri.EscapeDataString(remoteId)}/photo/$value", Method.GET);
        await using var photoStream = await _adapter.SendPrimitiveAsync<Stream>(request, ErrorMapping, cancellationToken).ConfigureAwait(false);
        if (photoStream is null)
            return [];

        using var photoBytes = new MemoryStream();
        await photoStream.CopyToAsync(photoBytes, cancellationToken).ConfigureAwait(false);
        return photoBytes.ToArray();
    }

    private async Task<T> SendCollectionAsync<T>(string url, ParsableFactory<T> factory, CancellationToken cancellationToken, bool preferPageSize = false) where T : IParsable
    {
        var request = CreateRequest(url, Method.GET);
        if (preferPageSize)
            request.Headers.Add("Prefer", "odata.maxpagesize=100");

        return await _adapter.SendAsync(request, factory, ErrorMapping, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(string url, Method method, ParsableFactory<T> factory, CancellationToken cancellationToken) where T : IParsable
    {
        var request = CreateRequest(url, method);
        return await _adapter.SendAsync(request, factory, ErrorMapping, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Contact> SendParsableAsync(string url, Method method, Contact contact, CancellationToken cancellationToken)
    {
        var request = CreateRequest(url, method);
        request.SetContentFromParsable(_adapter, "application/json", contact);
        return await _adapter.SendAsync(request, Contact.CreateFromDiscriminatorValue, ErrorMapping, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendNoContentAsync(string url, Method method, byte[] content, CancellationToken cancellationToken)
    {
        var request = CreateRequest(url, method);
        if (content is not null)
        {
            request.Content = new MemoryStream(content, writable: false);
            request.Headers.Add("Content-Type", "image/jpeg");
        }
        await _adapter.SendNoContentAsync(request, ErrorMapping, cancellationToken).ConfigureAwait(false);
    }

    private static RequestInformation CreateRequest(string url, Method method)
    {
        var request = new RequestInformation { URI = new Uri(url), HttpMethod = method };
        request.Headers.Add("Prefer", "IdType=\"ImmutableId\"");
        return request;
    }
}

public sealed class OutlookContactCollectionResponse : IParsable
{
    public List<Contact> Value { get; set; } = [];
    public string NextLink { get; set; }
    public string DeltaLink { get; set; }
    public static OutlookContactCollectionResponse CreateFromDiscriminatorValue(IParseNode _) => new();
    public IDictionary<string, Action<IParseNode>> GetFieldDeserializers() => new Dictionary<string, Action<IParseNode>>
    {
        ["value"] = node => Value = node.GetCollectionOfObjectValues<Contact>(Contact.CreateFromDiscriminatorValue)?.ToList() ?? [],
        ["@odata.nextLink"] = node => NextLink = node.GetStringValue(),
        ["@odata.deltaLink"] = node => DeltaLink = node.GetStringValue()
    };
    public void Serialize(ISerializationWriter writer) { }
}

public sealed class OutlookContactFolderCollectionResponse : IParsable
{
    public List<ContactFolder> Value { get; set; } = [];
    public string NextLink { get; set; }
    public static OutlookContactFolderCollectionResponse CreateFromDiscriminatorValue(IParseNode _) => new();
    public IDictionary<string, Action<IParseNode>> GetFieldDeserializers() => new Dictionary<string, Action<IParseNode>>
    {
        ["value"] = node => Value = node.GetCollectionOfObjectValues<ContactFolder>(ContactFolder.CreateFromDiscriminatorValue)?.ToList() ?? [],
        ["@odata.nextLink"] = node => NextLink = node.GetStringValue()
    };
    public void Serialize(ISerializationWriter writer) { }
}
