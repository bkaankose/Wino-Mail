using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.PeopleService.v1.Data;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace Wino.Core.Google;

public sealed class PeopleServiceService : IDisposable
{
    public PeopleServiceService(HttpClient httpClient)
    {
        People = new PeopleResource(httpClient, this);
        Connections = new ConnectionsResource(httpClient, this);
        ContactGroups = new ContactGroupsResource(httpClient, this);
    }

    public PeopleResource People { get; }
    public ConnectionsResource Connections { get; }
    public ContactGroupsResource ContactGroups { get; }

    public void Dispose()
    {
    }

    public sealed class PeopleResource
    {
        private readonly HttpClient _httpClient;
        private readonly object _service;

        internal PeopleResource(HttpClient httpClient, object service)
        {
            _httpClient = httpClient;
            _service = service;
        }

        public GetRequest Get(string resourceName) => new(_httpClient, _service, resourceName);
        public CreateContactRequest CreateContact(Person person) => new(_httpClient, _service, person);
        public UpdateContactRequest UpdateContact(string resourceName, Person person) => new(_httpClient, _service, resourceName, person);
        public DeleteContactRequest DeleteContact(string resourceName) => new(_httpClient, _service, resourceName);

        public sealed class GetRequest : GoogleApiRequest<Person>
        {
            private readonly string _resourceName;

            internal GetRequest(HttpClient httpClient, object service, string resourceName)
                : base(
                    httpClient,
                    service,
                    HttpMethod.Get,
                    () => string.Empty,
                    GoogleApiJsonContext.Default.Person)
            {
                _resourceName = resourceName;
                RequestUriFactory = () => GoogleUrl.AddQuery(
                    $"https://people.googleapis.com/v1/{GoogleUrl.Segment(_resourceName).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}",
                    ("personFields", PersonFields));
            }

            public string PersonFields { get; set; }
        }

        public sealed class CreateContactRequest : GoogleApiRequest<Person>
        {
            internal CreateContactRequest(HttpClient client, object service, Person person)
                : base(client, service, HttpMethod.Post, () => string.Empty,
                    GoogleApiJsonContext.Default.Person, () => GoogleJsonContent.Create(person, GoogleApiJsonContext.Default.Person))
            {
                RequestUriFactory = () => GoogleUrl.AddQuery(
                    "https://people.googleapis.com/v1/people:createContact",
                    ("personFields", PersonFields));
            }
            public string PersonFields { get; set; }
        }

        public sealed class UpdateContactRequest : GoogleApiRequest<Person>
        {
            private const string UpdateFields = "names,emailAddresses,phoneNumbers,addresses,organizations,birthdays,nicknames,biographies,urls,imClients,relations";
            private const string ReturnFields = "names,emailAddresses,phoneNumbers,addresses,organizations,birthdays,nicknames,fileAses,biographies,urls,imClients,relations,photos,metadata";
            internal UpdateContactRequest(HttpClient client, object service, string resourceName, Person person)
                : base(client, service, HttpMethod.Patch,
                    () => GoogleUrl.AddQuery(
                        $"https://people.googleapis.com/v1/{GoogleUrl.Segment(resourceName).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}:updateContact",
                        ("updatePersonFields", UpdateFields),
                        ("personFields", ReturnFields)),
                    GoogleApiJsonContext.Default.Person, () => GoogleJsonContent.Create(person, GoogleApiJsonContext.Default.Person)) { }
        }

        public sealed class DeleteContactRequest : GoogleApiRequest<GoogleEmptyResponse>
        {
            internal DeleteContactRequest(HttpClient client, object service, string resourceName)
                : base(client, service, HttpMethod.Delete,
                    () => $"https://people.googleapis.com/v1/{GoogleUrl.Segment(resourceName).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}:deleteContact",
                    GoogleApiJsonContext.Default.GoogleEmptyResponse) { }
        }
    }

    public sealed class ConnectionsResource
    {
        private readonly HttpClient _client;
        private readonly object _service;
        internal ConnectionsResource(HttpClient client, object service) { _client = client; _service = service; }
        public ListRequest List(string resourceName) => new(_client, _service, resourceName);

        public sealed class ListRequest : GoogleApiRequest<ListConnectionsResponse>
        {
            private readonly string _resourceName;
            internal ListRequest(HttpClient client, object service, string resourceName)
                : base(client, service, HttpMethod.Get, () => string.Empty, GoogleApiJsonContext.Default.ListConnectionsResponse)
            {
                _resourceName = resourceName;
                RequestUriFactory = () => GoogleUrl.AddQuery(
                    $"https://people.googleapis.com/v1/{GoogleUrl.Segment(_resourceName).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}/connections",
                    ("personFields", PersonFields),
                    ("sources", "READ_SOURCE_TYPE_CONTACT"),
                    ("pageSize", PageSize.ToString()),
                    ("pageToken", PageToken),
                    ("syncToken", SyncToken),
                    ("requestSyncToken", RequestSyncToken ? "true" : null));
            }
            public string PersonFields { get; set; }
            public int PageSize { get; set; } = 1000;
            public string PageToken { get; set; }
            public string SyncToken { get; set; }
            public bool RequestSyncToken { get; set; } = true;
        }
    }

    /// <summary>Google's contact groups: the lists a user files contacts under.</summary>
    public sealed class ContactGroupsResource
    {
        private const string Endpoint = "https://people.googleapis.com/v1/";
        private readonly HttpClient _client;
        private readonly object _service;

        internal ContactGroupsResource(HttpClient client, object service) { _client = client; _service = service; }

        public ListRequest List() => new(_client, _service);
        public CreateRequest Create(string name) => new(_client, _service, name);
        public UpdateRequest Update(string resourceName, string name) => new(_client, _service, resourceName, name);
        public DeleteRequest Delete(string resourceName) => new(_client, _service, resourceName);
        public ModifyMembersRequest ModifyMembers(string resourceName, IList<string> resourceNamesToAdd, IList<string> resourceNamesToRemove)
            => new(_client, _service, resourceName, resourceNamesToAdd, resourceNamesToRemove);

        private static string GroupUrl(string resourceName)
            => Endpoint + GoogleUrl.Segment(resourceName).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

        public sealed class ListRequest : GoogleApiRequest<ListContactGroupsResponse>
        {
            internal ListRequest(HttpClient client, object service)
                : base(client, service, HttpMethod.Get, () => string.Empty, GoogleApiJsonContext.Default.ListContactGroupsResponse)
            {
                RequestUriFactory = () => GoogleUrl.AddQuery(
                    Endpoint + "contactGroups",
                    ("pageSize", PageSize.ToString()),
                    ("pageToken", PageToken),
                    ("groupFields", "name,groupType,metadata"));
            }

            public int PageSize { get; set; } = 1000;
            public string PageToken { get; set; }
        }

        public sealed class CreateRequest : GoogleApiRequest<ContactGroup>
        {
            internal CreateRequest(HttpClient client, object service, string name)
                : base(client, service, HttpMethod.Post, () => Endpoint + "contactGroups",
                    GoogleApiJsonContext.Default.ContactGroup,
                    () => GoogleJsonContent.Create(
                        new CreateContactGroupRequest { ContactGroup = new ContactGroup { Name = name } },
                        GoogleApiJsonContext.Default.CreateContactGroupRequest)) { }
        }

        public sealed class UpdateRequest : GoogleApiRequest<ContactGroup>
        {
            internal UpdateRequest(HttpClient client, object service, string resourceName, string name)
                : base(client, service, HttpMethod.Put, () => GroupUrl(resourceName),
                    GoogleApiJsonContext.Default.ContactGroup,
                    () => GoogleJsonContent.Create(
                        new UpdateContactGroupRequest { ContactGroup = new ContactGroup { Name = name }, UpdateGroupFields = "name" },
                        GoogleApiJsonContext.Default.UpdateContactGroupRequest)) { }
        }

        public sealed class DeleteRequest : GoogleApiRequest<GoogleEmptyResponse>
        {
            // The contacts of a deleted group stay; only the group goes.
            internal DeleteRequest(HttpClient client, object service, string resourceName)
                : base(client, service, HttpMethod.Delete, () => GroupUrl(resourceName), GoogleApiJsonContext.Default.GoogleEmptyResponse) { }
        }

        public sealed class ModifyMembersRequest : GoogleApiRequest<ModifyContactGroupMembersResponse>
        {
            internal ModifyMembersRequest(HttpClient client, object service, string resourceName, IList<string> resourceNamesToAdd, IList<string> resourceNamesToRemove)
                : base(client, service, HttpMethod.Post, () => GroupUrl(resourceName) + "/members:modify",
                    GoogleApiJsonContext.Default.ModifyContactGroupMembersResponse,
                    () => GoogleJsonContent.Create(
                        new ModifyContactGroupMembersRequest
                        {
                            ResourceNamesToAdd = resourceNamesToAdd?.Count > 0 ? resourceNamesToAdd : null,
                            ResourceNamesToRemove = resourceNamesToRemove?.Count > 0 ? resourceNamesToRemove : null
                        },
                        GoogleApiJsonContext.Default.ModifyContactGroupMembersRequest)) { }
        }
    }
}

public sealed class DriveService : IDisposable
{
    public DriveService(HttpClient httpClient)
    {
        Files = new FilesResource(httpClient);
    }

    public FilesResource Files { get; }

    public void Dispose()
    {
    }

    public sealed class FilesResource
    {
        private readonly HttpClient _httpClient;

        internal FilesResource(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public CreateMediaUpload Create(DriveFile body, Stream stream, string contentType)
            => new(_httpClient, body, stream, contentType);

        public sealed class CreateMediaUpload
        {
            private readonly DriveFile _body;
            private readonly string _contentType;
            private readonly HttpClient _httpClient;
            private readonly Stream _stream;

            internal CreateMediaUpload(HttpClient httpClient, DriveFile body, Stream stream, string contentType)
            {
                _httpClient = httpClient;
                _body = body;
                _stream = stream;
                _contentType = contentType;
            }

            public string Fields { get; set; }

            public DriveFile ResponseBody { get; private set; }

            public async Task<GoogleUploadProgress> UploadAsync(CancellationToken cancellationToken = default)
            {
                try
                {
                    var boundary = $"wino_{Guid.NewGuid():N}";
                    using var content = new MultipartContent("related", boundary);

                    var metadata = JsonSerializer.Serialize(_body, GoogleApiJsonContext.Default.DriveFile);
                    var metadataContent = new StringContent(metadata, Encoding.UTF8, "application/json");
                    content.Add(metadataContent);

                    var fileContent = new StreamContent(_stream);
                    fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(_contentType);
                    content.Add(fileContent);

                    var uri = GoogleUrl.AddQuery(
                        "https://www.googleapis.com/upload/drive/v3/files",
                        ("uploadType", "multipart"),
                        ("fields", Fields));

                    using var response = await _httpClient.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
                    await GoogleApiErrorParser.ThrowIfUnsuccessfulAsync(response, cancellationToken).ConfigureAwait(false);

                    await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    ResponseBody = await JsonSerializer.DeserializeAsync(
                        responseStream,
                        GoogleApiJsonContext.Default.DriveFile,
                        cancellationToken).ConfigureAwait(false);

                    return new GoogleUploadProgress(GoogleUploadStatus.Completed);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new GoogleUploadProgress(GoogleUploadStatus.Failed, ex);
                }
            }
        }
    }
}
