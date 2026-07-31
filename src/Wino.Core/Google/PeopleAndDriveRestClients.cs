using System;
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
    }

    public PeopleResource People { get; }

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
